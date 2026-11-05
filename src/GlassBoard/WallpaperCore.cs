using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace GlassBoard;

// 静态桌面壁纸设置引擎：
// 优先用 IDesktopWallpaper（Win8+：逐显示器设置 + 摆放方式），
// 失败时回退 SystemParametersInfo(SPI_SETDESKWALLPAPER) + 注册表样式（Win7/受限环境）。
// 与 WallpaperSource（只读壁纸做背景捕获）互不依赖，各自持有 COM 封装。
internal static class WallpaperCore
{
	// 样式顺序与设置页下拉一致：0 填充 / 1 适应 / 2 拉伸 / 3 居中 / 4 平铺
	public const int Fill = 0;
	public const int Fit = 1;
	public const int Stretch = 2;
	public const int Center = 3;
	public const int Tile = 4;

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

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

	private const uint SPI_SETDESKWALLPAPER = 0x0014u;
	private const uint SPIF_UPDATEINIFILE = 0x01u;
	private const uint SPIF_SENDCHANGE = 0x02u;

	private static IDesktopWallpaper wallpaper;

	private static bool wallpaperTried;

	// 串行化所有 COM 调用：设置页多个后台任务（应用/还原）并发时避免交错
	private static readonly object Gate = new object();

	private static IDesktopWallpaper EnsureDesktop()
	{
		if (wallpaper != null)
		{
			return wallpaper;
		}
		if (wallpaperTried)
		{
			return null;
		}
		wallpaperTried = true;
		try
		{
			wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
		}
		catch
		{
			wallpaper = null;
		}
		return wallpaper;
	}

	private static int ToPosition(int style)
	{
		switch (style)
		{
			case 1: return (int)DesktopWallpaperPosition.Fit;      // 适应
			case 2: return (int)DesktopWallpaperPosition.Stretch; // 拉伸
			case 3: return (int)DesktopWallpaperPosition.Center;  // 居中
			case 4: return (int)DesktopWallpaperPosition.Tile;    // 平铺
			default: return (int)DesktopWallpaperPosition.Fill;   // 填充
		}
	}

	// 应用壁纸：COM 逐显示器设置 + 摆放方式；不可用时注册表样式 + SystemParametersInfo。
	// 成功返回 true。文件必须已存在于本地（壁纸库内）。
	public static bool Apply(string path, int style)
	{
		lock (Gate)
		{
			return ApplyCore(path, style);
		}
	}

	private static bool ApplyCore(string path, int style)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return false;
		}
		string full = Path.GetFullPath(path);
		int pos = ToPosition(style);
		IDesktopWallpaper dw = EnsureDesktop();
		bool ok = false;
		if (dw != null)
		{
			try
			{
				uint count = dw.GetMonitorDevicePathCount();
				bool any = false;
				for (uint i = 0u; i < count; i++)
				{
					dw.GetMonitorDevicePathAt(i, out string id);
					if (string.IsNullOrWhiteSpace(id))
					{
						continue;
					}
					dw.SetWallpaper(id, full);
					any = true;
				}
				if (any)
				{
					dw.SetPosition((DesktopWallpaperPosition)pos);
					ok = true;
				}
			}
			catch
			{
			}
		}
		if (!ok)
		{
			SetRegistryStyle(style);
			ok = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0u, full, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
		}
		return ok;
	}

	// 记录当前系统的原始壁纸（显示器ID → 壁纸文件），用于"恢复系统壁纸"。
	// COM 不可用时退化为注册表主壁纸（键 ""）。
	public static Dictionary<string, string> Capture()
	{
		lock (Gate)
		{
			return CaptureCore();
		}
	}

	private static Dictionary<string, string> CaptureCore()
	{
		Dictionary<string, string> map = new Dictionary<string, string>();
		IDesktopWallpaper dw = EnsureDesktop();
		if (dw != null)
		{
			try
			{
				uint count = dw.GetMonitorDevicePathCount();
				for (uint i = 0u; i < count; i++)
				{
					dw.GetMonitorDevicePathAt(i, out string id);
					if (string.IsNullOrWhiteSpace(id))
					{
						continue;
					}
					try
					{
						dw.GetWallpaper(id, out string w);
						if (!string.IsNullOrWhiteSpace(w))
						{
							map[id] = w;
						}
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
			if (map.Count > 0)
			{
				// 同时记录摆放方式（全局一份），还原时恢复原样
				try
				{
					map["@pos"] = ((int)dw.GetPosition()).ToString();
				}
				catch
				{
				}
			}
		}
		if (map.Count == 0)
		{
			string p = RegistryWallpaper();
			if (!string.IsNullOrWhiteSpace(p))
			{
				map[""] = p;
			}
		}
		return map;
	}

	// 还原 Capture() 记录的壁纸。目标文件已不存在时跳过（保持当前壁纸）。
	public static bool Restore(Dictionary<string, string> map)
	{
		lock (Gate)
		{
			return RestoreCore(map);
		}
	}

	private static bool RestoreCore(Dictionary<string, string> map)
	{
		if (map == null || map.Count == 0)
		{
			return false;
		}
		IDesktopWallpaper dw = EnsureDesktop();
		bool ok = false;
		if (dw != null)
		{
			try
			{
				// 先还原摆放方式（Capture 时记录的 @pos），再逐显示器还原图片
				if (map.TryGetValue("@pos", out string posText) && int.TryParse(posText, out int posValue))
				{
					dw.SetPosition((DesktopWallpaperPosition)Math.Max(0, Math.Min(5, posValue)));
				}
				uint count = dw.GetMonitorDevicePathCount();
				for (uint i = 0u; i < count; i++)
				{
					dw.GetMonitorDevicePathAt(i, out string id);
					if (map.TryGetValue(id, out string w) && !string.IsNullOrWhiteSpace(w) && File.Exists(w))
					{
						dw.SetWallpaper(id, w);
						ok = true;
					}
				}
			}
			catch
			{
			}
		}
		if (!ok && map.TryGetValue("", out string legacy) && !string.IsNullOrWhiteSpace(legacy) && File.Exists(legacy))
		{
			ok = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0u, legacy, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
		}
		return ok;
	}

	private static string RegistryWallpaper()
	{
		try
		{
			using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop"))
			{
				return key?.GetValue("Wallpaper") as string;
			}
		}
		catch
		{
			return null;
		}
	}

	// SystemParametersInfo 回退路径的样式：写入注册表后刷新桌面。
	private static void SetRegistryStyle(int style)
	{
		try
		{
			using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Control Panel\\Desktop"))
			{
				switch (style)
				{
					case 1: key.SetValue("WallpaperStyle", "6"); key.SetValue("TileWallpaper", "0"); break;  // 适应
					case 2: key.SetValue("WallpaperStyle", "2"); key.SetValue("TileWallpaper", "0"); break;  // 拉伸
					case 3: key.SetValue("WallpaperStyle", "0"); key.SetValue("TileWallpaper", "0"); break;  // 居中
					case 4: key.SetValue("WallpaperStyle", "0"); key.SetValue("TileWallpaper", "1"); break;  // 平铺
					default: key.SetValue("WallpaperStyle", "10"); key.SetValue("TileWallpaper", "0"); break; // 填充
				}
			}
		}
		catch
		{
		}
	}
}
