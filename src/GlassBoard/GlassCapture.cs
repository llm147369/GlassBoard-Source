using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassBoard;

// 背景只允许来自壁纸文件或专用壁纸 WorkerW。禁止整屏截图：透明浮窗、图标、
// 桌宠和任何应用窗口都不属于壁纸，采进底图会造成脏影；捕获不可信时由调用方
// 保留上一张干净底图，绝不猜测。

internal static class GlassCapture
{
	private const uint PW_RENDERFULLCONTENT = 0x2u;

	// 优先从 Windows 壁纸文件直接裁切；文件不可读时只读取专用壁纸 WorkerW，
	// 不读取屏幕合成结果。
	// fromFile=true 表示结果来自壁纸文件，调用方可以跳过黑帧重试（文件源不可能捕到黑帧）。
	public static Bitmap Acquire(Native.RECT rect, out bool fromFile)
	{
		fromFile = false;
		Bitmap bitmap = WallpaperSource.TryCapture(rect);
		if (bitmap != null)
		{
			fromFile = true;
			return bitmap;
		}
		return TryCapture(rect);
	}

	// 渲染指定物理像素矩形对应的壁纸 WorkerW 区域，并做“纯净度校验”：
	// 把候选 WorkerW 的渲染结果与壁纸文件的静态渲染逐格对比。桌面宠物、
	// 悬浮窗、应用窗口画进壁纸层的外来内容会破坏一致性 → 丢弃该层，
	// 直接返回壁纸文件的干净渲染。静态参考（壁纸文件）不可用时一律放弃捕获。
	// 失败/近黑帧时返回 null（调用方保留现有底图，绝不回退整屏截图）。
	public static Bitmap TryCapture(Native.RECT rect)
	{
		try
		{
			int w = rect.Right - rect.Left;
			int h = rect.Bottom - rect.Top;
			if (w < 8 || h < 8)
			{
				return null;
			}
			List<IntPtr> candidates = FindWallpaperCandidates();
			if (candidates.Count == 0)
			{
				return null;
			}
			// 壁纸文件的静态渲染：既是“干净标准”，也是壁纸层被污染时的回退结果。
			// 静态参考不可用时壁纸层与任意自建 WorkerW / 合成打印结果无法区分，
			// 绝不猜测 → 放弃捕获，调用方保留现有干净底图
			Bitmap reference = WallpaperSource.RenderStatic(rect);
			if (reference == null)
			{
				return null;
			}
			bool dynamic = WallpaperSource.DynamicWallpaperActive();
			Bitmap best = null;
			double bestMean = double.MaxValue;
			double bestMax = 0.0;
			double bestRatio = 0.0;
			bool[] bestMask = null;
			foreach (IntPtr hwnd in candidates)
			{
				Bitmap crop = CaptureRegion(hwnd, rect);
				if (crop == null)
				{
					continue;
				}
				if (IsBlank(crop))
				{
					// 近黑帧（锁屏/显示器关闭时壁纸层渲染为黑）：该候选作废
					crop.Dispose();
					continue;
				}
				GridDiff(crop, reference, out double mean, out int changed, out double maxCell, out bool[] mask);
				if (mean < bestMean)
				{
					if (best != null)
					{
						best.Dispose();
					}
					best = crop;
					bestMean = mean;
					bestMax = maxCell;
					bestRatio = changed / 1024.0;
					bestMask = mask;
				}
				else
				{
					crop.Dispose();
				}
			}
			if (best == null)
			{
				reference?.Dispose();
				return null;
			}
			if (!dynamic)
			{
				// 静态壁纸：壁纸层必须与文件渲染一致。任何局部高反差内容（宠物/悬浮层
				// 画进壁纸层）都判为污染 → 丢弃捕获，直接返回文件渲染的干净壁纸
				if (bestRatio <= 0.06 && bestMax <= 24.0)
				{
					reference.Dispose();
					return best;
				}
				best.Dispose();
				return reference;
			}
			// 动态壁纸（Lively / Wallpaper Engine / 元气 / 火萤等）：接受大面积变化的
			// 实时壁纸层，两道防线防脏：
			// 1) 与静态参考几乎一致的候选（静止帧 / 静态垫底副本 / 陈旧的系统壁纸
			//    缓存）→ 直接采纳；
			// 2) 其余做块状判定：窗口/图标/桌宠被带进壁纸层时 diff 聚成连通块
			//    → 判污染回退文件渲染；动画壁纸（粒子/光照/视频）diff 散布全图 → 放行采纳。
			// 不再因为“与静态参考全域不符”而回退：壁纸软件的正常画面本来就与壁纸
			// 文件处处不同，那正是用户要的动态画面。
			if (bestRatio <= 0.06 && bestMax <= 24.0)
			{
				reference.Dispose();
				return best;
			}
			if (Blocky(bestMask))
			{
				best.Dispose();
				return reference;
			}
			reference.Dispose();
			return best;
		}
		catch
		{
			return null;
		}
	}

	// 打印单个 WorkerW 自身窗口矩形（不依赖虚拟屏尺寸）并裁出目标矩形。
	// 目标矩形必须完全落在该 WorkerW 内，否则跳过：多显示器下壁纸层可能只覆盖
	// 主屏，按虚拟屏打印会被拉伸、裁切错位，把别的屏幕内容当壁纸裁进来。
	private static Bitmap CaptureRegion(IntPtr hwnd, Native.RECT rect)
	{
		int w = rect.Right - rect.Left;
		int h = rect.Bottom - rect.Top;
		if (w < 8 || h < 8)
		{
			return null;
		}
		if (!Native.GetWindowRect(hwnd, out Native.RECT wr))
		{
			return null;
		}
		int ww = wr.Right - wr.Left;
		int wh = wr.Bottom - wr.Top;
		if (ww < 8 || wh < 8)
		{
			return null;
		}
		if (rect.Left < wr.Left || rect.Top < wr.Top || rect.Right > wr.Right || rect.Bottom > wr.Bottom)
		{
			return null;
		}
		using (Bitmap full = new Bitmap(ww, wh, PixelFormat.Format24bppRgb))
		{
			bool ok;
			using (Graphics g = Graphics.FromImage(full))
			{
				IntPtr hdc = g.GetHdc();
				ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
				g.ReleaseHdc(hdc);
			}
			if (!ok)
			{
				return null;
			}
			Bitmap crop = new Bitmap(w, h, PixelFormat.Format24bppRgb);
			using (Graphics g = Graphics.FromImage(crop))
			{
				g.DrawImage(full, new Rectangle(0, 0, w, h), new Rectangle(rect.Left - wr.Left, rect.Top - wr.Top, w, h), GraphicsUnit.Pixel);
			}
			return crop;
		}
	}

	// 均匀采样判定近黑帧（与 GlassWindow.IsBlackFrame 同参数：阈值 4、8×8 网格）
	private static bool IsBlank(Bitmap bitmap)
	{
		int num = bitmap.Width - 1;
		int num2 = bitmap.Height - 1;
		for (int i = 0; i < 8; i++)
		{
			for (int j = 0; j < 8; j++)
			{
				Color color = bitmap.GetPixel(num * i / 7, num2 * j / 7);
				if (color.R >= 4 || color.G >= 4 || color.B >= 4)
				{
					return false;
				}
			}
		}
		return true;
	}

	// 32×32 网格均值差：mean=平均格差、changed=格差>24 的格数（全图 1024 格）、maxCell=最大格差、
	// mask=changed 格掩码（供块状判定）。纯净壁纸层与文件渲染是同一张图，格差接近 0；
	// 宠物/悬浮窗只在局部制造高反差（maxCell 大），自建 WorkerW 则是全域不符（changed 接近 1024）。
	private static void GridDiff(Bitmap a, Bitmap b, out double mean, out int changed, out double maxCell, out bool[] mask)
	{
		const int G = 32;
		using (Bitmap sa = new Bitmap(G, G, PixelFormat.Format24bppRgb))
		using (Bitmap sb = new Bitmap(G, G, PixelFormat.Format24bppRgb))
		{
			using (Graphics ga = Graphics.FromImage(sa))
			{
				ga.InterpolationMode = InterpolationMode.HighQualityBilinear;
				ga.DrawImage(a, new Rectangle(0, 0, G, G), new Rectangle(0, 0, a.Width, a.Height), GraphicsUnit.Pixel);
			}
			using (Graphics gb = Graphics.FromImage(sb))
			{
				gb.InterpolationMode = InterpolationMode.HighQualityBilinear;
				gb.DrawImage(b, new Rectangle(0, 0, G, G), new Rectangle(0, 0, b.Width, b.Height), GraphicsUnit.Pixel);
			}
			double sum = 0.0;
			int cnt = 0;
			maxCell = 0.0;
			mask = new bool[G * G];
			for (int y = 0; y < G; y++)
			{
				for (int x = 0; x < G; x++)
				{
					Color ca = sa.GetPixel(x, y);
					Color cb = sb.GetPixel(x, y);
					double d = (Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B)) / 3.0;
					sum += d;
					if (d > 24.0)
					{
						cnt++;
						mask[y * G + x] = true;
					}
					if (d > maxCell)
					{
						maxCell = d;
					}
				}
			}
			mean = sum / (double)(G * G);
			changed = cnt;
		}
	}

	// 壁纸层候选：动态壁纸激活时优先取壁纸软件自己的可见大窗口（Wallpaper Engine /
	// Lively / 元气 / 火萤等用自己的顶层窗口渲染，系统壁纸 WorkerW 只剩陈旧的静态
	// 垫底图）；否则只认带 SHELLDLL_DefView（图标层）的 WorkerW 之后紧跟的专用壁纸
	// WorkerW，唯一候选。不兜底其它顶层 WorkerW：第三方软件（桌宠、悬浮窗、桌面
	// 整理）自建的 WorkerW 内容正是“背景里的脏东西”，且 Win11 上某些 WorkerW 的
	// PrintWindow 会打印出整桌面合成（含窗口）。找不到候选就返回空，
	// 调用方保留现有干净底图。不主动向 Progman 发送 0x052C（会改变桌面窗口层级）。
	private static List<IntPtr> FindWallpaperCandidates()
	{
		List<IntPtr> list = new List<IntPtr>();
		if (WallpaperSource.DynamicWallpaperActive())
		{
			HashSet<int> pids = WallpaperSource.WallpaperProcessIds();
			if (pids.Count > 0)
			{
				EnumWindows(delegate (IntPtr h, IntPtr l)
				{
					GetWindowThreadProcessId(h, out uint pid);
					if (pids.Contains((int)pid) && IsWindowVisible(h) && (GetWindowLong(h, -20) & 0x80u) == 0u)
					{
						Native.RECT r;
						if (Native.GetWindowRect(h, out r) && r.Right - r.Left >= 640 && r.Bottom - r.Top >= 360)
						{
							list.Add(h);
						}
					}
					return true;
				}, IntPtr.Zero);
				if (list.Count > 0)
				{
					return list;
				}
			}
		}
		if (FindWindow("Progman", null) == IntPtr.Zero)
		{
			return list;
		}
		IntPtr defViewHost = IntPtr.Zero;
		EnumWindows(delegate (IntPtr h, IntPtr l)
		{
			if (defViewHost == IntPtr.Zero && ClassName(h) == "WorkerW" && FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
			{
				defViewHost = h;
			}
			return true;
		}, IntPtr.Zero);
		if (defViewHost == IntPtr.Zero)
		{
			return list;
		}
		IntPtr wall = FindWindowEx(IntPtr.Zero, defViewHost, "WorkerW", null);
		if (wall != IntPtr.Zero)
		{
			list.Add(wall);
		}
		return list;
	}

	// 4 连通块状判定：changed 格聚成连通大块 = 窗口/图标/桌宠等矩形内容被带进
	// 壁纸层（脏东西的特征是局部成块的尖锐 diff）；动画壁纸（粒子/光照/水波）的
	// diff 散布全图、小块孤立。总数过少（<60，壁纸时钟类小动画）或过多（>700，
	// 全域重绘）不判块状。
	private static bool Blocky(bool[] changed)
	{
		const int G = 32;
		int total = 0;
		for (int i = 0; i < changed.Length; i++)
		{
			if (changed[i])
			{
				total++;
			}
		}
		if (total < 60 || total > 700)
		{
			return false;
		}
		bool[] visited = new bool[changed.Length];
		int biggest = 0;
		for (int i = 0; i < changed.Length; i++)
		{
			if (!changed[i] || visited[i])
			{
				continue;
			}
			Stack<int> stack = new Stack<int>();
			stack.Push(i);
			visited[i] = true;
			int size = 0;
			while (stack.Count > 0)
			{
				int c = stack.Pop();
				size++;
				int x = c % G;
				int y = c / G;
				if (x > 0 && changed[c - 1] && !visited[c - 1])
				{
					visited[c - 1] = true;
					stack.Push(c - 1);
				}
				if (x < G - 1 && changed[c + 1] && !visited[c + 1])
				{
					visited[c + 1] = true;
					stack.Push(c + 1);
				}
				if (y > 0 && changed[c - G] && !visited[c - G])
				{
					visited[c - G] = true;
					stack.Push(c - G);
				}
				if (y < G - 1 && changed[c + G] && !visited[c + G])
				{
					visited[c + G] = true;
					stack.Push(c + G);
				}
			}
			if (size > biggest)
			{
				biggest = size;
			}
		}
		return biggest >= total * 0.35;
	}

	private static string ClassName(IntPtr h)
	{
		StringBuilder stringBuilder = new StringBuilder(64);
		GetClassName(h, stringBuilder, 64);
		return stringBuilder.ToString();
	}

	private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint nFlags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
}
