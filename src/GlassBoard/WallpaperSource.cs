using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace GlassBoard;

// 壁纸文件直读（“干净背景”的终局方案）：
// 用 IDesktopWallpaper 拿到每个显示器当前的壁纸文件与摆放方式（填充/适应/拉伸/居中/平铺/跨屏），
// 把任意屏幕矩形按摆放方式精确映射成壁纸源图区域。
// 不截屏、不渲染桌面层 —— 屏幕上任何窗口、图标、悬浮层都不可能出现在底图里，
// “捕获到组件上层的东西”这类污点从机制上消失；刷新也在原地完成，完全不可见。
// 返回 null = 无法直读（动态壁纸 / 无壁纸文件 / API 或文件不可用），调用方改走壁纸层渲染
// （GlassCapture 会用 RenderStatic 对壁纸层做“纯净度校验”，壁纸层被污染时直接采用
// RenderStatic 的干净渲染）；两者都不可用则保留现有底图，绝不截取整屏。
internal static class WallpaperSource
{
	private sealed class MonitorInfo
	{
		public string Id;
		public Native.RECT Rect;
		public MonitorInfo(string id, Native.RECT rect)
		{
			Id = id;
			Rect = rect;
		}
	}

	[ComImport]
	[Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
	private class DesktopWallpaperClass
	{
	}

	[ComImport]
	[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDesktopWallpaper
	{
		void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

		void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);

		void GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorID);

		uint GetMonitorDevicePathCount();

		void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out Native.RECT rect);

		void SetBackgroundColor(uint color);

		uint GetBackgroundColor();

		void SetPosition(DesktopWallpaperPosition position);

		DesktopWallpaperPosition GetPosition();
	}

	private enum DesktopWallpaperPosition
	{
		Center = 0,
		Tile = 1,
		Stretch = 2,
		Fit = 3,
		Fill = 4,
		Span = 5
	}

	private static IDesktopWallpaper wallpaper;

	private static long wallpaperProbeTick;

	private static bool wallpaperUnavailable;

	// 全分辨率壁纸缓存（按路径，最多 4 张；DrawImage 用，不把缓存对象交给调用方）
	private static readonly Dictionary<string, Bitmap> imageCache = new Dictionary<string, Bitmap>();

	private static long processProbeTick;

	private static bool dynamicActive;

	public static Bitmap TryCapture(Native.RECT rect)
	{
		if (DynamicWallpaperActive())
		{
			return null;
		}
		return RenderStatic(rect);
	}

	// 壁纸文件的静态渲染（不设动态壁纸门控）。两个用途：一是 GlassCapture 对壁纸层
	// 做纯净度校验时的“干净参考图”，二是壁纸层被宠物/悬浮窗污染或不可用时的干净回退。
	internal static Bitmap RenderStatic(Native.RECT rect)
	{
		try
		{
			int w = rect.Right - rect.Left;
			int h = rect.Bottom - rect.Top;
			if (w < 8 || h < 8)
			{
				return null;
			}
			IDesktopWallpaper dw = EnsureDesktop();
			if (dw == null)
			{
				return null;
			}
			Native.RECT vm = VirtualScreen();
			uint count = dw.GetMonitorDevicePathCount();
			List<MonitorInfo> monitors = new List<MonitorInfo>();
			for (uint i = 0u; i < count; i++)
			{
				dw.GetMonitorDevicePathAt(i, out string id);
				dw.GetMonitorRECT(id, out Native.RECT mr);
				monitors.Add(new MonitorInfo(id, mr));
			}
			if (monitors.Count == 0)
			{
				return null;
			}
			monitors = FitMonitors(monitors, vm);
			DesktopWallpaperPosition pos = Position(dw);
			// 跨屏模式整张图铺满虚拟屏幕；否则选包含区域中心的显示器
			MonitorInfo unit;
			if (pos == DesktopWallpaperPosition.Span)
			{
				unit = new MonitorInfo(monitors[0].Id, vm);
			}
			else
			{
				int cx = rect.Left + w / 2;
				int cy = rect.Top + h / 2;
				MonitorInfo match = null;
				foreach (MonitorInfo m in monitors)
				{
					if (cx >= m.Rect.Left && cx < m.Rect.Right && cy >= m.Rect.Top && cy < m.Rect.Bottom)
					{
						match = m;
						break;
					}
				}
				unit = match ?? new MonitorInfo(monitors[0].Id, vm);
			}
			dw.GetWallpaper(unit.Id, out string path);
			return RenderRegion(dw, unit.Rect, rect, path, pos);
		}
		catch
		{
			return null;
		}
	}

	private static Bitmap RenderRegion(IDesktopWallpaper dw, Native.RECT mr, Native.RECT rect, string path, DesktopWallpaperPosition pos)
	{
		int w = rect.Right - rect.Left;
		int h = rect.Bottom - rect.Top;
		double ox = rect.Left - mr.Left;
		double oy = rect.Top - mr.Top;
		Bitmap image = LoadImage(path);
		if (image == null)
		{
			// 壁纸文件缺失/不可解码：不臆造纯色结果，返回 null 让调用方走桌面层渲染
			return null;
		}
		Color bg = BackgroundColor(dw);
		Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
		using (Graphics g = Graphics.FromImage(bmp))
		{
			g.InterpolationMode = InterpolationMode.HighQualityBicubic;
			g.PixelOffsetMode = PixelOffsetMode.HighQuality;
			g.Clear(bg);
			double iw = image.Width;
			double ih = image.Height;
			double mw = Math.Max(1, mr.Right - mr.Left);
			double mh = Math.Max(1, mr.Bottom - mr.Top);
			if (pos == DesktopWallpaperPosition.Tile)
			{
				DrawTiled(g, image, ox, oy, w, h);
			}
			else
			{
				double sx;
				double sy;
				double sw;
				double sh;
				if (pos == DesktopWallpaperPosition.Stretch)
				{
					sx = ox * iw / mw;
					sy = oy * ih / mh;
					sw = w * iw / mw;
					sh = h * ih / mh;
				}
				else if (pos == DesktopWallpaperPosition.Fit)
				{
					double scale = Math.Min(mw / iw, mh / ih);
					double dx = iw * scale;
					double dy = ih * scale;
					sx = (ox - (mw - dx) / 2.0) / scale;
					sy = (oy - (mh - dy) / 2.0) / scale;
					sw = w / scale;
					sh = h / scale;
				}
				else if (pos == DesktopWallpaperPosition.Center)
				{
					sx = ox - (mw - iw) / 2.0;
					sy = oy - (mh - ih) / 2.0;
					sw = w;
					sh = h;
				}
				else
				{
					// Fill（默认）与 Span：等比放大铺满，裁掉超出部分
					double scale = Math.Max(mw / iw, mh / ih);
					double dx = iw * scale;
					double dy = ih * scale;
					sx = (ox - (mw - dx) / 2.0) / scale;
					sy = (oy - (mh - dy) / 2.0) / scale;
					sw = w / scale;
					sh = h / scale;
				}
				// 源矩形部分越界时 GDI+ 自动裁剪到图像内并同步移动目标，露出的部分显示背景色
				g.DrawImage(image, new RectangleF(0f, 0f, w, h), new RectangleF((float)sx, (float)sy, (float)sw, (float)sh), GraphicsUnit.Pixel);
			}
		}
		return bmp;
	}

	private static void DrawTiled(Graphics g, Bitmap image, double ox, double oy, double w, double h)
	{
		double iw = image.Width;
		double ih = image.Height;
		int firstTx = (int)Math.Floor(ox / iw);
		int firstTy = (int)Math.Floor(oy / ih);
		int lastTx = (int)Math.Floor((ox + w - 0.01) / iw);
		int lastTy = (int)Math.Floor((oy + h - 0.01) / ih);
		for (int ty = firstTy; ty <= lastTy; ty++)
		{
			for (int tx = firstTx; tx <= lastTx; tx++)
			{
				float sx = (float)(ox - tx * iw);
				float sy = (float)(oy - ty * ih);
				float dx = (float)(tx * iw - ox);
				float dy = (float)(ty * ih - oy);
				float sw = Math.Min((float)(iw - sx), (float)(w - dx));
				float sh = Math.Min((float)(ih - sy), (float)(h - dy));
				if (sw <= 0f || sh <= 0f)
				{
					continue;
				}
				g.DrawImage(image, new RectangleF(dx, dy, sw, sh), new RectangleF(sx, sy, sw, sh), GraphicsUnit.Pixel);
			}
		}
	}

	// 加载并缓存壁纸文件（EXIF 方向归一：手机竖拍直接设壁纸的文件方向是横的）
	private static Bitmap LoadImage(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		lock (imageCache)
		{
			if (imageCache.TryGetValue(path, out Bitmap hit))
			{
				return hit;
			}
		}
		Bitmap loaded = null;
		try
		{
			using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				using (Image src = Image.FromStream(fs, false, false))
				{
					loaded = ApplyOrientation(new Bitmap(src));
				}
			}
		}
		catch
		{
			if (loaded != null)
			{
				loaded.Dispose();
			}
			return null;
		}
		lock (imageCache)
		{
			if (imageCache.Count >= 4)
			{
				foreach (string k in imageCache.Keys.ToList())
				{
					imageCache.Remove(k);
					break;
				}
			}
			imageCache[path] = loaded;
		}
		return loaded;
	}

	private static Bitmap ApplyOrientation(Bitmap bmp)
	{
		try
		{
			if (Array.IndexOf(bmp.PropertyIdList, 274) < 0)
			{
				return bmp;
			}
			PropertyItem pi = bmp.GetPropertyItem(274);
			if (pi.Value == null || pi.Value.Length < 2)
			{
				return bmp;
			}
			int v = BitConverter.ToUInt16(pi.Value, 0);
			RotateFlipType? rf = null;
			if (v == 2)
			{
				rf = RotateFlipType.RotateNoneFlipX;
			}
			else if (v == 3)
			{
				rf = RotateFlipType.Rotate180FlipNone;
			}
			else if (v == 4)
			{
				rf = RotateFlipType.RotateNoneFlipY;
			}
			else if (v == 5)
			{
				rf = RotateFlipType.Rotate90FlipX;
			}
			else if (v == 6)
			{
				rf = RotateFlipType.Rotate90FlipNone;
			}
			else if (v == 7)
			{
				rf = RotateFlipType.Rotate270FlipX;
			}
			else if (v == 8)
			{
				rf = RotateFlipType.Rotate270FlipNone;
			}
			if (!rf.HasValue)
			{
				return bmp;
			}
			Bitmap rotated = (Bitmap)bmp.Clone();
			rotated.RotateFlip(rf.Value);
			bmp.Dispose();
			return rotated;
		}
		catch
		{
			return bmp;
		}
	}

	private static Color BackgroundColor(IDesktopWallpaper dw)
	{
		try
		{
			uint c = dw.GetBackgroundColor();
			return Color.FromArgb((byte)(c & 0xFFu), (byte)((c >> 8) & 0xFFu), (byte)((c >> 16) & 0xFFu));
		}
		catch
		{
			return Color.Black;
		}
	}

	private static DesktopWallpaperPosition Position(IDesktopWallpaper dw)
	{
		try
		{
			return dw.GetPosition();
		}
		catch
		{
			return DesktopWallpaperPosition.Fill;
		}
	}

	private static IDesktopWallpaper EnsureDesktop()
	{
		if (wallpaper != null)
		{
			return wallpaper;
		}
		long now = Environment.TickCount;
		if (wallpaperUnavailable && now - wallpaperProbeTick < 30000)
		{
			return null;
		}
		wallpaperProbeTick = now;
		try
		{
			wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
			return wallpaper;
		}
		catch
		{
			wallpaperUnavailable = true;
			return null;
		}
	}

	// 显示器矩形兜底：shell 正常返回物理像素（与虚拟屏幕一致）；异常环境（个别 shell/DPI 组合
	// 返回逻辑像素）时按系统 DPI 归一，保证与 GetWindowRect 的物理坐标在同一坐标系。
	private static List<MonitorInfo> FitMonitors(List<MonitorInfo> raw, Native.RECT vm)
	{
		long uL = long.MaxValue;
		long uT = long.MaxValue;
		long uR = long.MinValue;
		long uB = long.MinValue;
		foreach (MonitorInfo m in raw)
		{
			uL = Math.Min(uL, m.Rect.Left);
			uT = Math.Min(uT, m.Rect.Top);
			uR = Math.Max(uR, m.Rect.Right);
			uB = Math.Max(uB, m.Rect.Bottom);
		}
		if (uL == vm.Left && uT == vm.Top && uR == vm.Right && uB == vm.Bottom)
		{
			return raw;
		}
		int dpi = SystemDpi();
		if (dpi == 96)
		{
			return raw;
		}
		double s = dpi / 96.0;
		List<MonitorInfo> scaled = new List<MonitorInfo>();
		foreach (MonitorInfo m in raw)
		{
			Native.RECT r = new Native.RECT
			{
				Left = (int)Math.Round(m.Rect.Left * s),
				Top = (int)Math.Round(m.Rect.Top * s),
				Right = (int)Math.Round(m.Rect.Right * s),
				Bottom = (int)Math.Round(m.Rect.Bottom * s)
			};
			scaled.Add(new MonitorInfo(m.Id, r));
		}
		return scaled;
	}

	private static int SystemDpi()
	{
		try
		{
			using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
			{
				return (int)g.DpiX;
			}
		}
		catch
		{
			return 96;
		}
	}

	// 动态壁纸进程（Wallpaper Engine / Lively / 元气 / 火萤 / DeskScapes）：壁纸文件 ≠
	// 用户看到的桌面，直读会得到静态垫底图，与所见不符 → 文件直读放弃，交给调用方走
	// 壁纸层渲染（经纯净度校验）；壁纸层也不可用/被污染则采用 RenderStatic 的静态渲染。
	internal static bool DynamicWallpaperActive()
	{
		long now = Environment.TickCount;
		if (now - processProbeTick > 10000)
		{
			processProbeTick = now;
			try
			{
				if (Process.GetProcessesByName("wallpaper64").Length > 0 || Process.GetProcessesByName("wallpaper32").Length > 0)
				{
					dynamicActive = true;
				}
				else
				{
					dynamicActive = Process.GetProcesses().Any((Process p) =>
						p.ProcessName.StartsWith("Lively", StringComparison.OrdinalIgnoreCase) ||
						p.ProcessName.StartsWith("元气", StringComparison.Ordinal) ||
						p.ProcessName.StartsWith("火萤", StringComparison.Ordinal) ||
						p.ProcessName.StartsWith("DeskScapes", StringComparison.OrdinalIgnoreCase));
				}
			}
			catch
			{
				dynamicActive = false;
			}
		}
		return dynamicActive;
	}

	// 壁纸软件渲染窗口的进程 id 集合。壁纸软件用自己的顶层窗口画壁纸（系统壁纸
	// WorkerW 只剩陈旧的静态垫底图），调用方枚举这些进程的可见大窗口作为壁纸层
	// 候选；窗口级过滤（可见性、尺寸）由调用方完成。
	internal static HashSet<int> WallpaperProcessIds()
	{
		HashSet<int> ids = new HashSet<int>();
		try
		{
			foreach (Process p in Process.GetProcesses())
			{
				string name;
				try
				{
					name = p.ProcessName;
				}
				catch
				{
					continue;
				}
				if (name.Equals("wallpaper64", StringComparison.OrdinalIgnoreCase) ||
					name.Equals("wallpaper32", StringComparison.OrdinalIgnoreCase) ||
					name.StartsWith("Lively", StringComparison.OrdinalIgnoreCase) ||
					name.StartsWith("元气", StringComparison.Ordinal) ||
					name.StartsWith("火萤", StringComparison.Ordinal) ||
					name.StartsWith("DeskScapes", StringComparison.OrdinalIgnoreCase))
				{
					ids.Add(p.Id);
				}
			}
		}
		catch
		{
		}
		return ids;
	}

	private static Native.RECT VirtualScreen()
	{
		return new Native.RECT
		{
			Left = GetSystemMetrics(76),
			Top = GetSystemMetrics(77),
			Right = GetSystemMetrics(76) + GetSystemMetrics(78),
			Bottom = GetSystemMetrics(77) + GetSystemMetrics(79)
		};
	}

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int nIndex);
}
