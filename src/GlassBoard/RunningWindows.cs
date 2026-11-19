using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

public static class RunningWindows
{
	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

	private delegate bool Callback(IntPtr window, IntPtr value);

	private struct FLASHWINFO
	{
		public uint Size;

		public IntPtr Window;

		public uint Flags;

		public uint Count;

		public uint Timeout;
	}

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private sealed class TargetInfo
	{
		public DateTime Stamp;

		public string Target;

		public string Arguments;
	}

	private static readonly List<RunningWindow> windows = new List<RunningWindow>();
	private static readonly List<RunningWindow> background = new List<RunningWindow>();

	private static readonly BoundedCache<string, TargetInfo> targets = new BoundedCache<string, TargetInfo>(192, StringComparer.OrdinalIgnoreCase);

	private static readonly BoundedCache<string, long> last = new BoundedCache<string, long>(192, StringComparer.OrdinalIgnoreCase);

	// —— 进程元数据缓存：路径 / AppUserModelID 按 pid 缓存，避免每次窗口枚举都 OpenProcess ——
	private struct ProcessMeta
	{
		public string Path;

		public string Aumid;

		public int Tick;
	}

	private static readonly Dictionary<uint, ProcessMeta> pidMeta = new Dictionary<uint, ProcessMeta>();

	private const int PidMetaTtlMs = 15000;

	// —— 后台窗口索引：windows / background / trayWindows 由后台线程维护，UI 线程只读锁内快照 ——
	private static readonly object scanGate = new object();

	private static readonly List<RunningWindow> trayWindows = new List<RunningWindow>();

	private static int windowsScanTick = int.MinValue;

	private static int processesScanTick = int.MinValue;

	private static int trayScanTick = int.MinValue;

	private static int trayInFlight;

	private static bool maintenanceStarted;

	private const int WindowsFreshMs = 1200;

	private const int ProcessesFreshMs = 4000;

	private const int TrayFreshMs = 3000;

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(Callback callback, IntPtr value);

	[DllImport("user32.dll")]
	private static extern IntPtr FindWindow(string className, string windowName);

	[DllImport("user32.dll")]
	private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

	[DllImport("user32.dll")]
	private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, StringBuilder lParam);

	[DllImport("user32.dll")]
	public static extern bool IsWindowVisible(IntPtr window);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(IntPtr window);

	[DllImport("user32.dll")]
	public static extern bool IsIconic(IntPtr window);

	[StructLayout(LayoutKind.Sequential)]
	private struct WINDOWPLACEMENT
	{
		public int length;
		public int flags;
		public int showCmd;
		public int ptMinPositionX;
		public int ptMinPositionY;
		public int ptMaxPositionX;
		public int ptMaxPositionY;
		public RECT rcNormalPosition;
	}

	[DllImport("user32.dll")]
	private static extern bool GetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);

	/// <summary>
	/// 主窗尺寸判定：排除启动过渡窗（微信 4.0 的 176x199 空白过渡窗、托盘小窗等）。
	/// 低于 400x300 的可见窗口不当作可激活的主窗。
	/// 最小化窗口的 GetWindowRect 会返回 -32000 处的小矩形（最小化图标位置），
	/// 此时改用 GetWindowPlacement 的 rcNormalPosition（还原尺寸）判定。
	/// </summary>
	public static bool IsMainWindowSize(IntPtr window)
	{
		int width;
		int height;
		if (IsIconic(window))
		{
			WINDOWPLACEMENT placement = default(WINDOWPLACEMENT);
			placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
			if (!GetWindowPlacement(window, ref placement))
			{
				return false;
			}
			width = placement.rcNormalPosition.Right - placement.rcNormalPosition.Left;
			height = placement.rcNormalPosition.Bottom - placement.rcNormalPosition.Top;
		}
		else
		{
			if (!GetWindowRect(window, out RECT r))
			{
				return false;
			}
			width = r.Right - r.Left;
			height = r.Bottom - r.Top;
		}
		return width >= 400 && height >= 300;
	}

	// 定向枚举入口进程的"隐藏主窗"（大退到托盘的应用：主窗隐藏后不在常规可见快照里，
	// 点击 Dock 图标时用此方法找回并强制显示，恢复原会话而不是重开新窗）。
	public static List<RunningWindow> ForHidden(Entry entry)
	{
		List<RunningWindow> hidden = new List<RunningWindow>();
		if (entry == null || entry.IsFolder || Directory.Exists(entry.Path))
		{
			return hidden;
		}
		Resolve(entry);
		string[] paths = WindowActivationRules.Executables(entry);
		HashSet<string> fallbackNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string text in paths)
		{
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(text);
			if (!string.IsNullOrEmpty(fileNameWithoutExtension) && !GenericExeNames.Any((string n) => n.Equals(fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase)))
			{
				fallbackNames.Add(fileNameWithoutExtension);
			}
		}
		int own = Process.GetCurrentProcess().Id;
		Dictionary<uint, string> byPid = new Dictionary<uint, string>();
		EnumWindows((IntPtr handle, IntPtr value) =>
		{
			GetWindowThreadProcessId(handle, out uint pid);
			if (pid == 0 || pid == (uint)own)
			{
				return true;
			}
			if (!byPid.TryGetValue(pid, out string path))
			{
				try
				{
					path = ProcessPath(pid);
				}
				catch
				{
					path = null;
				}
				byPid[pid] = path;
			}
			if (string.IsNullOrEmpty(path) || !(paths.Contains(path, StringComparer.OrdinalIgnoreCase) || fallbackNames.Contains(Path.GetFileNameWithoutExtension(path))))
			{
				return true;
			}
			StringBuilder clsBuilder = new StringBuilder(128);
			GetClassName(handle, clsBuilder, clsBuilder.Capacity);
			string cls = clsBuilder.ToString();
			if (cls.Contains("Message") || cls.Contains("Tray") || cls.Contains("Hook") || cls.Contains("Notify") || cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "IME" || cls == "MSCTFIME UI" || cls == "Default IME" || cls == "ConsoleWindowClass")
			{
				return true;
			}
			StringBuilder titleBuilder = new StringBuilder(1024);
			GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
			if (string.IsNullOrWhiteSpace(titleBuilder.ToString()))
			{
				return true;
			}
			if (!IsMainWindowSize(handle))
			{
				return true;
			}
			hidden.Add(new RunningWindow
			{
				Handle = handle,
				ProcessId = pid,
				Path = path,
				Title = titleBuilder.ToString(),
				AppUserModelId = Aumid(pid)
			});
			return true;
		}, IntPtr.Zero);
		if (hidden.Count > 0)
		{
			Store.Trace("ACTIVATE hidden-scan count=" + hidden.Count + " first=" + hidden[0].Handle.ToInt64() + " title=" + hidden[0].Title + " iconic=" + IsIconic(hidden[0].Handle) + " visible=" + IsWindowVisible(hidden[0].Handle));
		}
		return hidden;
	}

	[DllImport("user32.dll")]
	private static extern bool IsZoomed(IntPtr window);

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindow(IntPtr window, uint command);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong(IntPtr window, int index);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);

	[DllImport("user32.dll")]
	private static extern bool ShowWindowAsync(IntPtr window, int command);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr window, int command);

	[DllImport("user32.dll")]
	private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

	[DllImport("user32.dll")]
	private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

	[DllImport("user32.dll")]
	private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

	[DllImport("user32.dll")]
	private static extern bool SetCursorPos(int x, int y);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT point);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);

	private struct POINT
	{
		public int X;

		public int Y;
	}

	[DllImport("user32.dll")]
	private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr window, out RECT rect);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern IntPtr GetLastActivePopup(IntPtr window);

	[DllImport("user32.dll")]
	private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate proc, uint processId, uint threadId, uint flags);

	[DllImport("user32.dll")]
	private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachOrDetach);

	[DllImport("user32.dll")]
	private static extern bool BringWindowToTop(IntPtr window);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	// WM_MOUSEACTIVATE 到达时（Dock 拒绝激活、前台被释放之前）记录"点击前的真实前台窗口"。
	// 点击 Dock 会先让旧前台窗口失活，且 MA_NOACTIVATE 使 Dock 不接受激活，
	// 导致点击处理时 GetForegroundWindow() 返回 0，无法判断"用户正在看哪个应用"。
	private static IntPtr pressForeground = IntPtr.Zero;
	private static int pressForegroundTick = 0;

	public static void RecordPressForeground()
	{
		pressForeground = GetForegroundWindow();
		pressForegroundTick = Environment.TickCount;
	}

	// 社区标准方案（taskbar/dock 类程序通用做法）：
	// 用 SetWinEventHook(EVENT_SYSTEM_FOREGROUND) 持续跟踪"最后一个真实前台窗口"，
	// 点击时不再依赖实时 GetForegroundWindow()（点击瞬间它可能已是 0）。
	private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

	private static readonly WinEventDelegate ForegroundProc = OnForegroundChanged;
	private static IntPtr foregroundHook = IntPtr.Zero;
	private static IntPtr trackedForeground = IntPtr.Zero;
	private static int trackedForegroundTick = 0;
	private static int ownPid = Process.GetCurrentProcess().Id;

	// 前台窗口真正变化时触发（UI 线程）：Dock 用它立即刷新运行指示器，不必等定时器。
	public static event Action ForegroundChanged;

	private static void SetTracked(IntPtr hwnd)
	{
		bool changed = trackedForeground != hwnd;
		trackedForeground = hwnd;
		trackedForegroundTick = Environment.TickCount;
		if (changed)
		{
			try
			{
				ForegroundChanged?.Invoke();
			}
			catch
			{
			}
		}
	}

	// GlassBoard 自己的设置窗口句柄（由 Controller 在窗口 Show 后写入、关闭时清空）。
	// 前台事件跟踪默认忽略本进程窗口，但设置窗口需要支持"再点最小化"，
	// 所以把它的句柄单列出来让跟踪器特殊记录。
	public static IntPtr SettingsWindowHandle = IntPtr.Zero;

	public static void InitForegroundTracking()
	{
		if (foregroundHook != IntPtr.Zero)
		{
			return;
		}
		foregroundHook = SetWinEventHook(3u, 3u, IntPtr.Zero, ForegroundProc, 0u, 0u, 0u);
		Store.Trace("FGTRACK init hook=" + foregroundHook.ToInt64());
	}

	// 运行定时器兜底采样（万一事件钩子漏报时仍能更新跟踪值）。
	public static void SampleForeground()
	{
		IntPtr foregroundWindow = GetForegroundWindow();
		if (foregroundWindow == IntPtr.Zero || (long)foregroundWindow == ownPid)
		{
			return;
		}
		uint num;
		GetWindowThreadProcessId(foregroundWindow, out num);
		if (num == (uint)ownPid)
		{
			return;
		}
		if (!IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow))
		{
			return;
		}
		SetTracked(foregroundWindow);
	}

	private static void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
	{
		try
		{
			if (hwnd == IntPtr.Zero)
			{
				RevalidateTracked();
				return;
			}
			uint num;
			GetWindowThreadProcessId(hwnd, out num);
			if (num == (uint)ownPid)
			{
				// 忽略 GlassBoard 自己的窗口（Dock/面板），保留用户应用的最后前台记录；
				// 唯一例外是设置窗口——它要支持"再点最小化"，需要被跟踪。
				if (hwnd == SettingsWindowHandle && SettingsWindowHandle != IntPtr.Zero)
				{
					if (trackedForeground != hwnd)
					{
						Store.Trace("FGTRACK settings hwnd=" + hwnd.ToInt64());
					}
					SetTracked(hwnd);
				}
				else
				{
					RevalidateTracked();
				}
				return;
			}
			if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
			{
				RevalidateTracked();
				return;
			}
			if (trackedForeground != hwnd)
			{
				Store.Trace("FGTRACK hwnd=" + hwnd.ToInt64() + " pid=" + num);
			}
			SetTracked(hwnd);
		}
		catch
		{
		}
	}

	private static void RevalidateTracked()
	{
		IntPtr intPtr = trackedForeground;
		if (intPtr != IntPtr.Zero && (!IsWindow(intPtr) || !IsWindowVisible(intPtr) || IsIconic(intPtr)))
		{
			Store.Trace("FGTRACK clear hwnd=" + intPtr.ToInt64());
			SetTracked(IntPtr.Zero);
		}
	}

	// 取「按下瞬间捕获值」与「事件跟踪值」中更新鲜的一个（点击与前台事件可能先后穿插），
	// 两者都失效时回退实时查询。
	public static IntPtr EffectiveForeground()
	{
		int pressAge = Environment.TickCount - pressForegroundTick;
		bool pressValid = pressForeground != IntPtr.Zero && pressAge >= 0 && pressAge < 1500;
		int trackAge = Environment.TickCount - trackedForegroundTick;
		bool trackValid = trackedForeground != IntPtr.Zero && trackAge >= 0 && trackAge < 8000 && IsWindow(trackedForeground);
		if (pressValid && trackValid)
		{
			return (trackAge < pressAge) ? trackedForeground : pressForeground;
		}
		if (trackValid)
		{
			return trackedForeground;
		}
		if (pressValid)
		{
			return pressForeground;
		}
		return GetForegroundWindow();
	}

	// 激活成功后立刻把目标窗口记为前台候选（乐观跟踪）：
	// 用户紧接着再点图标（想要最小化）时不会因前台事件延迟而多按一次。
	public static void MarkForeground(IntPtr hwnd)
	{
		if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd))
		{
			return;
		}
		SetTracked(hwnd);
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder name);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out RECT rect, int size);

	[DllImport("user32.dll")]
	private static extern bool FlashWindowEx(ref FLASHWINFO info);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr window, StringBuilder text, int length);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

	[DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
	private static extern IntPtr ClassIcon64(IntPtr window, int index);

	[DllImport("user32.dll", EntryPoint = "GetClassLongW")]
	private static extern uint ClassIcon32(IntPtr window, int index);

	public static ImageSource Icon(RunningWindow window)
	{
		try
		{
			SendMessageTimeout(window.Handle, 127u, new IntPtr(1), IntPtr.Zero, 2u, 80u, out var result);
			if (result == IntPtr.Zero)
			{
				SendMessageTimeout(window.Handle, 127u, new IntPtr(2), IntPtr.Zero, 2u, 80u, out result);
			}
			if (result == IntPtr.Zero)
			{
				result = ((IntPtr.Size == 8) ? ClassIcon64(window.Handle, -14) : new IntPtr((int)ClassIcon32(window.Handle, -14)));
			}
			if (result != IntPtr.Zero)
			{
				BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHIcon(result, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				bitmapSource.Freeze();
				return bitmapSource;
			}
			if (!string.IsNullOrEmpty(window.Path))
			{
				ImageSource imageSource = Native.Icon(window.Path);
				if (imageSource != null)
				{
					return imageSource;
				}
			}
		}
		catch
		{
		}
		BitmapSource bitmapSource2 = Imaging.CreateBitmapSourceFromHIcon(SystemIcons.Application.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
		bitmapSource2.Freeze();
		return bitmapSource2;
	}

	public static string ProcessPath(uint id)
	{
		lock (pidMeta)
		{
			if (pidMeta.TryGetValue(id, out var meta) && Environment.TickCount - meta.Tick >= 0 && Environment.TickCount - meta.Tick < PidMetaTtlMs)
			{
				return meta.Path;
			}
		}
		IntPtr intPtr = OpenProcess(4096u, inherit: false, id);
		if (intPtr == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			int length = 512;
			StringBuilder stringBuilder = new StringBuilder(length);
			if (!QueryFullProcessImageName(intPtr, 0u, stringBuilder, ref length))
			{
				if (Marshal.GetLastWin32Error() != 122)
				{
					return null;
				}
				length = 32768;
				stringBuilder = new StringBuilder(length);
				if (!QueryFullProcessImageName(intPtr, 0u, stringBuilder, ref length))
				{
					return null;
				}
			}
			string path = WindowActivationRules.NormalizePath(stringBuilder.ToString());
			lock (pidMeta)
			{
				if (!pidMeta.TryGetValue(id, out var meta))
				{
					meta = default(ProcessMeta);
				}
				meta.Path = path;
				meta.Aumid = AumidOf(intPtr);
				meta.Tick = Environment.TickCount;
				pidMeta[id] = meta;
			}
			return path;
		}
		finally
		{
			CloseHandle(intPtr);
		}
	}

	// 进程 AppUserModelID（Win8+ 打包应用/设置 AppID；失败返回空串）。仅由 ProcessPath 填充缓存。
	private static string AumidOf(IntPtr process)
	{
		try
		{
			uint length = 256u;
			StringBuilder builder = new StringBuilder((int)length);
			if (GetApplicationUserModelId(process, ref length, builder) == 0)
			{
				return builder.ToString();
			}
		}
		catch
		{
		}
		return "";
	}

	// 只读访问：AUMID 由后台扫描经 ProcessPath 填充缓存，调用方不触发任何新查询。
	public static string Aumid(uint pid)
	{
		lock (pidMeta)
		{
			if (pidMeta.TryGetValue(pid, out var meta))
			{
				return meta.Aumid ?? "";
			}
		}
		return "";
	}

	public static void Scan()
	{
		Stopwatch timer = Stopwatch.StartNew();
		int tick = Environment.TickCount;
		lock (scanGate)
		{
			if (tick - windowsScanTick >= 0 && tick - windowsScanTick < WindowsFreshMs)
			{
				return; // 1.2 秒内的重复调用直接吃缓存（右键菜单 / 点击 / 刷新共用）
			}
		}
		ScanWindowsCore();
		lock (scanGate)
		{
			if (tick - processesScanTick < 0 || tick - processesScanTick >= ProcessesFreshMs)
			{
				ScanProcessesCore();
			}
		}
		timer.Stop();
		if (timer.ElapsedMilliseconds > 60)
		{
			Store.Trace("SCAN slow ms=" + timer.ElapsedMilliseconds);
		}
	}

	// —— 后台维护：2.5 秒一拍的窗口索引刷新，UI 线程永不参与窗口枚举 ——
	public static void StartMaintenance()
	{
		lock (scanGate)
		{
			if (maintenanceStarted)
			{
				return;
			}
			maintenanceStarted = true;
		}
		Thread thread = new Thread(() =>
		{
			while (true)
			{
				try
				{
					Thread.Sleep(2500);
					RefreshCore();
				}
				catch
				{
				}
			}
		});
		thread.IsBackground = true;
		thread.Name = "glassboard-windowindex";
		thread.Start();
	}

	// 异步刷新窗口索引（onDone 在后台线程回调，调用方自行 marshal 回 UI）。
	public static void RefreshAsync(Action onDone = null)
	{
		ThreadPool.QueueUserWorkItem(delegate
		{
			try
			{
				RefreshCore();
			}
			catch (Exception ex)
			{
				Store.Trace("REFRESH error " + ex.Message);
			}
			onDone?.Invoke();
		});
	}

	private static void RefreshCore()
	{
		int tick = Environment.TickCount;
		bool doWindows;
		bool doProcesses;
		lock (scanGate)
		{
			doWindows = tick - windowsScanTick < 0 || tick - windowsScanTick >= WindowsFreshMs / 2;
			doProcesses = tick - processesScanTick < 0 || tick - processesScanTick >= ProcessesFreshMs;
		}
		if (doWindows)
		{
			ScanWindowsCore();
		}
		if (doProcesses)
		{
			ScanProcessesCore();
		}
		KickTrayRefresh();
	}

	// 第一遍：可见窗口枚举（原 Scan 主体；与托盘窗口合并后整体换入）。
	private static void ScanWindowsCore()
	{
		List<RunningWindow> next = new List<RunningWindow>();
		Dictionary<uint, string> byPid = new Dictionary<uint, string>();
		int own = Process.GetCurrentProcess().Id;
		EnumWindows((IntPtr handle, IntPtr value) =>
		{
			if (!IsWindowVisible(handle))
			{
				return true;
			}
			int windowLong = GetWindowLong(handle, -20);
			if ((windowLong & 0x80) != 0 || ((windowLong & 0x40000) == 0 && GetWindow(handle, 4u) != IntPtr.Zero && IsWindowVisible(GetWindow(handle, 4u))))
			{
				return true;
			}
			try
			{
				if (DwmGetWindowAttribute(handle, 14, out int value2, 4) == 0 && value2 != 0)
				{
					return true;
				}
			}
			catch
			{
			}
			GetWindowThreadProcessId(handle, out var id);
			if (id == own)
			{
				return true;
			}
			if (!byPid.TryGetValue(id, out var value3))
			{
				try
				{
					value3 = ProcessPath(id);
				}
				catch
				{
					value3 = null;
				}
				byPid[id] = value3;
			}
			StringBuilder stringBuilder = new StringBuilder(1024);
			GetWindowText(handle, stringBuilder, stringBuilder.Capacity);
			if (string.IsNullOrWhiteSpace(stringBuilder.ToString()))
			{
				return true;
			}
			StringBuilder stringBuilder2 = new StringBuilder(128);
			GetClassName(handle, stringBuilder2, stringBuilder2.Capacity);
			string className = stringBuilder2.ToString();
			if (new string[7] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Default IME", "IME", "MSCTFIME UI" }.Contains(className))
			{
				return true;
			}
			// 输入法辅助窗口（标题可能与类名不同）：一律排除，避免提权/无法取路径的程序在 Dock 显示成 "Default IME"
			string titleCheck = stringBuilder.ToString().Trim();
			if (titleCheck == "Default IME" || titleCheck == "MSCTFIME UI" || titleCheck == "IME")
			{
				return true;
			}
			// 传统控制台窗口（cmd/powershell 等）：终端不是「运行的程序」，不进运行列表。
			if (className == "ConsoleWindowClass")
			{
				return true;
			}
			// AMD 显卡驱动组件（RadeonSoftware 面板等）：驱动后台/设置界面不算用户程序。
			if (!string.IsNullOrEmpty(value3))
			{
				string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
				if (!string.IsNullOrEmpty(pf) && value3.StartsWith(pf + "\\AMD", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			next.Add(new RunningWindow
			{
				Handle = handle,
				ProcessId = id,
				Path = value3,
				Title = stringBuilder.ToString(),
				AppUserModelId = Aumid(id)
			});
			return true;
		}, IntPtr.Zero);
		List<RunningWindow> tray;
		lock (scanGate)
		{
			tray = trayWindows.ToList();
			windowsScanTick = Environment.TickCount;
		}
		MergeTrayIntoWindows(next, tray);
		lock (scanGate)
		{
			windows.Clear();
			windows.AddRange(next);
		}
		if (RenderDiagnostics.Verbose)
		{
			// 采样日志：只记录 hwnd/pid/程序路径（不记窗口标题等敏感内容）
			System.Text.StringBuilder sample = new System.Text.StringBuilder();
			foreach (RunningWindow w in next.Take(3))
			{
				sample.Append("[hwnd=").Append(w.Handle.ToInt64()).Append(" pid=").Append(w.ProcessId).Append(" path=").Append(w.Path ?? "").Append("]");
			}
			RenderDiagnostics.Trace("RUN scan windows=" + next.Count + " sample=" + sample);
		}
		KickTrayRefresh();
	}

	// 托盘窗口合并进窗口列表（同 pid 去重，不覆盖可见窗口）。
	private static void MergeTrayIntoWindows(List<RunningWindow> into, List<RunningWindow> tray)
	{
		HashSet<uint> taken = new HashSet<uint>(into.Select((RunningWindow w) => w.ProcessId));
		foreach (RunningWindow w in tray)
		{
			if (taken.Add(w.ProcessId))
			{
				into.Add(w);
			}
		}
	}

	// 托盘隐藏窗口单独后台扫描（8 秒节流 + 单飞限制；绝不占 UI 线程）。
	private static void KickTrayRefresh()
	{
		int tick = Environment.TickCount;
		lock (scanGate)
		{
			if (tick - trayScanTick >= 0 && tick - trayScanTick < TrayFreshMs)
			{
				return;
			}
			if (trayInFlight > 0)
			{
				return;
			}
			trayInFlight++;
		}
		ThreadPool.QueueUserWorkItem(delegate
		{
			try
			{
				ScanTrayCore();
				lock (scanGate)
				{
					trayScanTick = Environment.TickCount;
				}
				MergeTrayIntoWindows();
			}
			catch (Exception ex)
			{
				Store.Trace("TRAY_SCAN error " + ex.Message);
			}
			finally
			{
				lock (scanGate)
				{
					trayInFlight--;
				}
			}
		});
	}

	// 附属/运行时进程名（Windows 自带、框架运行时、内嵌解释器——永远不进运行列表）。
	private static readonly HashSet<string> SubprocessExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"node", "VBCSCompiler", "WeChatAppEx", "msedge", "msedgewebview2", "nahimicNotifSys", "crashpad_handler", "identity_helper", "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost", "TextInputHost", "Widgets", "WidgetService", "CrossDeviceService", "PhoneExperienceHost", "MicrosoftStartFeedProvider", "WidgetBoard", "GameBar", "GameBarFTServer", "aha_doctor",
		// 后台开发/服务工具（跨机器通用，一律不进运行列表）
		"adb", "windows-mcp",
		// 脚本解释器：python/pythonw 是通用宿主，进程名无信息量；纯后台/托盘形态一律过滤。
		// 用户主动运行的 Python GUI 应用有可见主窗口（走 ScanWindowsCore，不受此处影响）仍会正常显示。
		"python", "pythonw", "python3"
	};

	// 系统自带的安装区（OEM 预装/驱动/商店应用后台——永不进运行列表）。
	private static bool IsSystemArea(string path)
	{
		string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		if (path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		if (!string.IsNullOrEmpty(pf) && path.StartsWith(pf + "\\WindowsApps", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (!string.IsNullOrEmpty(pf) && path.StartsWith(pf + "\\AMD", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (path.StartsWith(pf + "\\dotnet", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		if (!string.IsNullOrEmpty(pf86) && path.StartsWith(pf86 + "\\Microsoft\\Edge", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		if (!string.IsNullOrEmpty(common) && (path.StartsWith(common + "\\Lenovo", StringComparison.OrdinalIgnoreCase) || path.StartsWith(common + "\\Microsoft", StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}
		return false;
	}

	// 第三遍：无任何窗口但在运行的软件（仅用户会话，排除服务/更新器/辅助进程）。
	private static void ScanProcessesCore()
	{
		List<RunningWindow> next = new List<RunningWindow>();
		List<RunningWindow> current = Snapshot();
		HashSet<string> havePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<uint> hostPids = TrayHostPids();
		foreach (RunningWindow w in current)
		{
			if (!string.IsNullOrEmpty(w.Path))
			{
				havePaths.Add(w.Path);
			}
		}
		string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		int session = Process.GetCurrentProcess().SessionId;
		Process[] processes;
		try
		{
			processes = Process.GetProcesses();
		}
		catch
		{
			return;
		}
		foreach (Process process in processes)
		{
			try
			{
				if (process.Id == ownPid || process.SessionId != session)
				{
					continue;
				}
				string processName;
				try
				{
					processName = process.ProcessName;
				}
				catch
				{
					continue;
				}
				if (SystemSoftwareExeNames.Contains(processName))
				{
					continue;
				}
				string lower = processName.ToLowerInvariant();
				// 辅助进程过滤词：renderer/update/server/helper 等在任何机器上都是辅助进程，一律排除（不豁免，
				// 否则 wetype_renderer/wetype_update 这类输入法辅助进程会借宿主窗口混进运行区）。
				if (lower.Contains("update") || lower.Contains("crash") || lower.Contains("install") || lower.Contains("setup") || lower.Contains("helper") || lower.Contains("cef") || lower.Contains("service") || lower.Contains("daemon") || lower.Contains("elevat") || lower.Contains("remoting") || lower.Contains("notifier") || lower.Contains("server") || lower.Contains("renderer") || lower.Contains("notif") || lower.Contains("broker") || lower.Contains("agent") || lower.Contains("companion") || lower.Contains("watcher") || lower.Contains("monitor") || lower.Contains("webview") || lower.Contains("background") || lower.Contains("backend"))
				{
					continue;
				}
				// "tray" 词例外：XxxTray.exe 通常是产品主托盘进程（剪映 JianyingProTray 等），有托盘宿主窗口才放行。
				if (lower.Contains("tray") && !hostPids.Contains((uint)process.Id))
				{
					continue;
				}
				if (SubprocessExeNames.Contains(processName))
				{
					continue;
				}
				string path = ProcessPath((uint)process.Id);
				if (string.IsNullOrEmpty(path) || havePaths.Contains(path) || IsSystemArea(path))
				{
					continue;
				}
				havePaths.Add(path);
				next.Add(new RunningWindow
				{
					Handle = IntPtr.Zero,
					ProcessId = (uint)process.Id,
					Path = path,
					Title = processName,
					Tray = true,
					AppUserModelId = Aumid((uint)process.Id)
				});
			}
			catch
			{
			}
			finally
			{
				process.Dispose();
			}
		}
		lock (scanGate)
		{
			background.Clear();
			background.AddRange(next);
			processesScanTick = Environment.TickCount;
		}
	}

	// 第二遍：隐藏窗口——只收"有托盘图标的应用"（托盘图标名匹配，或该进程持有托盘宿主窗口），其余隐藏辅助窗口一律不收。
	private static void ScanTrayCore()
	{
		List<RunningWindow> found = new List<RunningWindow>();
		List<string> tray = TrayNames();
		HashSet<uint> hostPids = TrayHostPids();
		string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		HashSet<uint> taken = new HashSet<uint>();
		List<RunningWindow> current = Snapshot();
		foreach (RunningWindow w in current)
		{
			taken.Add(w.ProcessId);
		}
		string[] systemClasses = new string[12] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "DV2ControlHost", "ForegroundStaging", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "Shell_ToastWindow", "Default IME", "IME", "MSCTFIME UI" };
		EnumWindows((IntPtr handle, IntPtr value) =>
		{
			if (IsWindowVisible(handle))
			{
				return true;
			}
			IntPtr owner = GetWindow(handle, 4u);
			if (owner != IntPtr.Zero && IsWindowVisible(owner))
			{
				return true;
			}
			try
			{
				if (DwmGetWindowAttribute(handle, 14, out int value2, 4) == 0 && value2 != 0)
				{
					return true;
				}
			}
			catch
			{
			}
			GetWindowThreadProcessId(handle, out var id);
			if (id == ownPid || id == 0u || taken.Contains(id))
			{
				return true;
			}
			string value3;
			try
			{
				value3 = ProcessPath(id);
			}
			catch
			{
				value3 = null;
			}
			if (string.IsNullOrEmpty(value3))
			{
				return true;
			}
			if (value3.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			string exeName = Path.GetFileNameWithoutExtension(value3);
			if (SystemSoftwareExeNames.Contains(exeName) || SubprocessExeNames.Contains(exeName))
			{
				return true;
			}
			// 辅助进程过滤词（与进程扫描同一套通用规则）：wetype_renderer/wetype_update 之类即使有隐藏窗口
			// 或标题蹭上托盘图标名也一律排除；仅 "tray" 词在有托盘宿主窗口时豁免。
			string lowerExe = exeName.ToLowerInvariant();
			if (lowerExe.Contains("update") || lowerExe.Contains("crash") || lowerExe.Contains("install") || lowerExe.Contains("setup") || lowerExe.Contains("helper") || lowerExe.Contains("cef") || lowerExe.Contains("service") || lowerExe.Contains("daemon") || lowerExe.Contains("elevat") || lowerExe.Contains("remoting") || lowerExe.Contains("notifier") || lowerExe.Contains("server") || lowerExe.Contains("renderer") || lowerExe.Contains("notif") || lowerExe.Contains("broker") || lowerExe.Contains("agent") || lowerExe.Contains("companion") || lowerExe.Contains("watcher") || lowerExe.Contains("monitor") || lowerExe.Contains("webview") || lowerExe.Contains("background") || lowerExe.Contains("backend"))
			{
				return true;
			}
			if (lowerExe.Contains("tray") && !hostPids.Contains(id))
			{
				return true;
			}
			StringBuilder stringBuilder2 = new StringBuilder(128);
			GetClassName(handle, stringBuilder2, stringBuilder2.Capacity);
			if (systemClasses.Contains(stringBuilder2.ToString()))
			{
				return true;
			}
			StringBuilder stringBuilder = new StringBuilder(1024);
			GetWindowText(handle, stringBuilder, stringBuilder.Capacity);
			string title = stringBuilder.ToString();
			if (string.IsNullOrWhiteSpace(title))
			{
				return true;
			}
			// 托盘归属判定：标题/进程名匹配托盘图标名，或进程持有托盘宿主窗口（WS_EX_TOOLWINDOW 的
			// 托盘主窗口也放行——大退驻留的 QQ/微信/剪映等主隐藏窗口常带该样式，之前被一票否决导致"后台在跑但 Dock 不显示"）。
			if (!MatchesTray(title, exeName, tray) && !hostPids.Contains(id))
			{
				return true;
			}
			taken.Add(id);
			found.Add(new RunningWindow
			{
				Handle = handle,
				ProcessId = id,
				Path = value3,
				Title = title,
				Tray = true,
				AppUserModelId = Aumid(id)
			});
			return true;
		}, IntPtr.Zero);
		lock (scanGate)
		{
			trayWindows.Clear();
			trayWindows.AddRange(found);
		}
	}

	// 托盘扫描完成后把托盘窗口合并进主窗口列表（同 pid 去重）。
	private static void MergeTrayIntoWindows()
	{
		List<RunningWindow> tray;
		lock (scanGate)
		{
			tray = trayWindows.ToList();
		}
		lock (scanGate)
		{
			HashSet<uint> taken = new HashSet<uint>(windows.Select((RunningWindow w) => w.ProcessId));
			foreach (RunningWindow w in tray)
			{
				if (taken.Add(w.ProcessId))
				{
					windows.Add(w);
				}
			}
		}
	}

	// 托盘归属判定：进程名与任一托盘图标名互含（忽略大小写）即视为托盘应用；
	// 标题只做等值匹配——"微信输入法-关于"这类长尾标题不能蹭"微信输入法"图标名（输入法辅助窗口误收根因）。
	// 中英文名不通的应用（微信 Weixin↔微信）由 TrayHostPids 宿主窗口通道兜底。
	private static bool MatchesTray(string title, string exeName, List<string> tray)
	{
		if (tray.Count == 0)
		{
			return false;
		}
		foreach (string name in tray)
		{
			if (string.IsNullOrEmpty(name))
			{
				continue;
			}
			if (exeName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) >= 0 || title.Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	// 托盘宿主窗口：应用自建的托盘图标宿主（Qt/Electron/Flutter 系）。
	// 任务栏被本应用隐藏时 explorer 托盘结构不可枚举，宿主窗口是"该进程确有托盘图标"的最可靠信号。
	private static List<uint> trayHostPidsCache = new List<uint>();
	private static int trayHostPidsTick = 0;

	private static HashSet<uint> TrayHostPids()
	{
		int tick = Environment.TickCount;
		lock (scanGate)
		{
			if (tick - trayHostPidsTick >= 0 && tick - trayHostPidsTick < 3000)
			{
				return new HashSet<uint>(trayHostPidsCache);
			}
		}
		HashSet<uint> pids = new HashSet<uint>();
		EnumWindows((IntPtr handle, IntPtr value) =>
		{
			try
			{
				StringBuilder cls = new StringBuilder(128);
				GetClassName(handle, cls, cls.Capacity);
				string c = cls.ToString();
				if (c.IndexOf("TrayIconMessageWindow", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("StatusTrayWindow", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("NotifyIconHostWindow", StringComparison.OrdinalIgnoreCase) >= 0 || c.Equals("WPETrayWindow", StringComparison.OrdinalIgnoreCase) || c.Equals("TrayWindow", StringComparison.OrdinalIgnoreCase))
				{
					GetWindowThreadProcessId(handle, out var id);
					if (id != 0u)
					{
						pids.Add(id);
					}
				}
			}
			catch
			{
			}
			return true;
		}, IntPtr.Zero);
		lock (scanGate)
		{
			trayHostPidsCache = pids.ToList();
			trayHostPidsTick = Environment.TickCount;
		}
		return pids;
	}

	// 系统托盘图标名：优先 UIA（Win10/11 通用，覆盖 XAML 岛），失败退回经典 ToolbarWindow32 文本。
	private static List<string> trayNameCache = new List<string>();
	private static int trayNameCacheTick = 0;

	private static List<string> TrayNames()
	{
		if (Environment.TickCount - trayNameCacheTick < 5000 && trayNameCache.Count > 0)
		{
			return trayNameCache;
		}
		trayNameCacheTick = Environment.TickCount;
		List<string> names = new List<string>();
		try
		{
			// 只取隐藏图标区（溢出区）：Win11 是顶层 XAML 岛窗口 TopLevelWindowForOverflowXamlIsland，
			// 不在 TrayNotifyWnd 子窗链上；Win10 是 NotifyIconOverflowWindow。
			IntPtr overflowIsland = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
			if (overflowIsland != IntPtr.Zero)
			{
				try
				{
					AutomationElement island = AutomationElement.FromHandle(overflowIsland);
					AutomationElementCollection buttons = island.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
					foreach (AutomationElement button in buttons)
					{
						string name = button.Current.Name;
						if (!string.IsNullOrWhiteSpace(name))
						{
							names.Add(name.Trim());
						}
					}
				}
				catch
				{
				}
				CollectToolbarNames(overflowIsland, names);
			}
			if (names.Count == 0)
			{
				IntPtr shell = FindWindow("Shell_TrayWnd", null);
				IntPtr notify = (shell != IntPtr.Zero) ? FindWindowEx(shell, IntPtr.Zero, "TrayNotifyWnd", null) : IntPtr.Zero;
				IntPtr overflow = (notify != IntPtr.Zero) ? FindWindowEx(notify, IntPtr.Zero, "NotifyIconOverflowWindow", null) : IntPtr.Zero;
				if (overflow != IntPtr.Zero)
				{
					CollectToolbarNames(overflow, names);
				}
				if (names.Count == 0 && notify != IntPtr.Zero)
				{
					// Win10 兜底：SysPager 内第 1 个 ToolbarWindow32 是可见区，第 2 个起是隐藏区
					IntPtr sys = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
					IntPtr child = IntPtr.Zero;
					int index = 0;
					while ((child = FindWindowEx(sys, child, "ToolbarWindow32", null)) != IntPtr.Zero)
					{
						if (index > 0)
						{
							CollectToolbarNames(child, names);
						}
						index++;
					}
				}
			}
		}
		catch
		{
		}
		trayNameCache = names;
		return names;
	}

	private static void CollectToolbarNames(IntPtr parent, List<string> names)
	{
		if (parent == IntPtr.Zero)
		{
			return;
		}
		IntPtr toolbar = FindWindowEx(parent, IntPtr.Zero, "ToolbarWindow32", null);
		if (toolbar == IntPtr.Zero)
		{
			return;
		}
		int count = (int)SendMessage(toolbar, 1048u, IntPtr.Zero, IntPtr.Zero);
		for (int i = 0; i < count; i++)
		{
			StringBuilder builder = new StringBuilder(256);
			if (SendMessage(toolbar, 1099u, (IntPtr)i, builder) != IntPtr.Zero)
			{
				string name = builder.ToString().Trim();
				if (name.Length > 0)
				{
					names.Add(name);
				}
			}
		}
	}

	private static readonly HashSet<string> SystemSoftwareExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"svchost", "dwm", "csrss", "winlogon", "smss", "services", "lsass", "fontdrvhost",
		"Registry", "ctfmon", "sihost", "taskhostw", "ShellExperienceHost", "TextInputHost",
		"StartMenuExperienceHost", "RuntimeBroker", "ApplicationFrameHost", "SearchHost", "SearchApp",
		"dllhost", "conhost", "audiodg", "SecurityHealthService", "MoUsoCoreWorker", "explorer"
	};

	private static void Resolve(Entry entry)
	{
		if (entry == null || entry.IsFolder)
		{
			return;
		}
		string path = entry.Path;
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(entry.LaunchTarget))
			{
				LaunchInfoService.Enrich(entry);
			}
			return;
		}
		DateTime dateTime;
		try
		{
			dateTime = File.GetLastWriteTimeUtc(path);
		}
		catch
		{
			dateTime = DateTime.MinValue;
		}
		if (targets.TryGet(path, out var value) && value.Stamp == dateTime)
		{
			entry.LaunchTarget = value.Target;
			entry.LaunchArguments = value.Arguments;
			return;
		}
		LaunchInfoService.Enrich(entry);
		targets.Put(path, new TargetInfo
		{
			Stamp = dateTime,
			Target = entry.LaunchTarget,
			Arguments = entry.LaunchArguments
		});
	}

	private static readonly string[] GenericExeNames = new string[20]
	{
		"setup", "unins", "uninstall", "update", "updater", "upgrade", "crashpad", "helper", "host", "launcher", "start", "run", "main", "app", "core", "service", "winstub", "stub", "daemon", "agent"
	};

	public static List<RunningWindow> For(Entry entry)
	{
		if (entry == null || entry.IsFolder || Directory.Exists(entry.Path))
		{
			return new List<RunningWindow>();
		}
		Resolve(entry);
		string[] paths = WindowActivationRules.Executables(entry);
		HashSet<string> fallbackNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string[] array = paths;
		foreach (string text in array)
		{
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(text);
			if (!string.IsNullOrEmpty(fileNameWithoutExtension) && !GenericExeNames.Any((string n) => n.Equals(fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase)))
			{
				fallbackNames.Add(fileNameWithoutExtension);
			}
		}
		return (from w in SnapshotWithBackground()
			where !string.IsNullOrEmpty(w.Path) && (paths.Contains(w.Path, StringComparer.OrdinalIgnoreCase) || fallbackNames.Contains(Path.GetFileNameWithoutExtension(w.Path)))
			orderby w.Handle.ToInt64()
			select w).ToList();
	}

	// 前台窗口 → 进程路径（走 pidMeta 缓存，无枚举无扫描，UI 线程可安全调用）。
	public static string PathOfWindow(IntPtr hwnd)
	{
		try
		{
			GetWindowThreadProcessId(hwnd, out uint id);
			if (id == 0)
			{
				return "";
			}
			return ProcessPath(id) ?? "";
		}
		catch
		{
			return "";
		}
	}

	// 判断某前台窗口路径是否属于该入口（与 For 的匹配规则一致，供前台事件领先窗口快照时兜底）。
	public static bool MatchesPath(Entry entry, string windowPath)
	{
		if (entry == null || entry.IsFolder || string.IsNullOrEmpty(windowPath))
		{
			return false;
		}
		Resolve(entry);
		string[] paths = WindowActivationRules.Executables(entry);
		if (paths.Contains(windowPath, StringComparer.OrdinalIgnoreCase))
		{
			return true;
		}
		string name = Path.GetFileNameWithoutExtension(windowPath);
		foreach (string text in paths)
		{
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(text);
			if (!string.IsNullOrEmpty(fileNameWithoutExtension) && !GenericExeNames.Any((string n) => n.Equals(fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase)) && fileNameWithoutExtension.Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	public static List<RunningWindow> Snapshot()
	{
		lock (scanGate)
		{
			return windows.ToList();
		}
	}

	// 运行菜单专用：可见/托盘窗口 + 无窗口在运行的软件（不含服务进程）。
	public static List<RunningWindow> SnapshotWithBackground()
	{
		lock (scanGate)
		{
			return windows.Concat(background).ToList();
		}
	}

	public static bool IsRunning(Entry entry)
	{
		return For(entry).Count > 0;
	}

	public static bool ActivateHandle(IntPtr handle)
	{
		if (!IsWindow(handle))
		{
			return false;
		}
		// 恢复与前台抢占可能耗时（托盘应用的恢复轮询、前台重试最长数百毫秒），
		// 统一后台线程执行：点击图标立即返回，UI 不卡（Pavise 类托盘应用场景）。
		Task.Run(delegate
		{
			try
			{
				RestoreAndForeground(handle);
			}
			catch (Exception)
			{
			}
		});
		return true;
	}

	private static void RestoreAndForeground(IntPtr handle)
	{
		if (IsIconic(handle))
		{
			ShowWindowAsync(handle, 9);
			// 等窗口完成恢复动画再抢前台（微信/Pavise 等恢复慢，60ms 不够：
			// 恢复动画期间 SetForegroundWindow 必被拒）。轮询直到恢复或超时。
			int restoreWait = 0;
			while (IsIconic(handle) && restoreWait < 250)
			{
				Thread.Sleep(20);
				restoreWait += 20;
			}
			if (IsIconic(handle))
			{
				// 托盘类应用会拦截 SC_RESTORE（窗口始终停在被托管状态）：改 SW_SHOWNORMAL 强制显示。
				ShowWindowAsync(handle, 1);
				restoreWait = 0;
				while (IsIconic(handle) && restoreWait < 250)
				{
					Thread.Sleep(20);
					restoreWait += 20;
				}
				if (IsIconic(handle))
				{
					// 仍然无效：该句柄只是遗留的"最小化壳"，应用真正的 UI 主窗体被自己隐藏了。
					// 通用兜底：找同进程最大的非工具类顶层窗口强制显示（Pavise 场景）。
					IntPtr main = FindMainWindowOf(handle);
					Store.Trace("ACTIVATE_R mainFallback hwnd=" + main.ToInt64());
					if (main != IntPtr.Zero)
					{
						ShowWindowAsync(main, 1);
						handle = main;
						restoreWait = 0;
						while (!IsWindowVisible(handle) && restoreWait < 250)
						{
							Thread.Sleep(20);
							restoreWait += 20;
						}
					}
				}
			}
			Store.Trace("ACTIVATE_R restoreDone iconic=" + IsIconic(handle) + " visible=" + IsWindowVisible(handle));
			Thread.Sleep(60);
		}
		if (!IsWindowVisible(handle))
		{
			// 隐藏窗口（微信 4.0 "大退"=隐藏主窗，不是最小化）：Win32 层可见但应用框架状态机
			// （Qt/Electron）仍在"隐藏"态，客户区点击坐标映射错乱（"半死窗口"：标题栏按钮正常、
			// 聊天列表点不动）。
			// 实测有效唤醒序列（等价用户手动操作）：先显示 → 真实最小化 → 恢复。两次状态翻转
			// 强制框架内部几何与 Win32 层对齐。隐藏窗口直接 SW_MINIMIZE 无效（无状态变化），
			// 必须先显示。
			// 注意：必须用同步 ShowWindow（SendMessage 语义）而非 ShowWindowAsync（PostMessage
			// 语义）——异步连发时三个命令在目标消息队列里连续处理，框架状态机来不及逐状态
			// 走完，等效于"只显示"，唤醒不生效。
			PostMessage(handle, 0x0112u, (IntPtr)0xF120, IntPtr.Zero);
			Thread.Sleep(80);
			ShowWindow(handle, 1);
			Thread.Sleep(350);
			ShowWindow(handle, 6);
			Thread.Sleep(550);
			ShowWindow(handle, 9);
			int visWait = 0;
			while (!IsWindowVisible(handle) && visWait < 500)
			{
				Thread.Sleep(20);
				visWait += 20;
			}
			Store.Trace("ACTIVATE_R hiddenShown visible=" + IsWindowVisible(handle));
			Thread.Sleep(60);
		}
		// 几何抖动：1px 位移强制 WM_WINDOWPOSCHANGED，让框架内部几何与 Win32 层重新对齐
		// （隐藏恢复后的窗口客户区坐标映射可能错乱，1px 抖动肉眼无感）。
		if (GetWindowRect(handle, out RECT shakeRect))
		{
			int sw = shakeRect.Right - shakeRect.Left;
			int sh = shakeRect.Bottom - shakeRect.Top;
			if (sw > 40 && sh > 40)
			{
				SetWindowPos(handle, IntPtr.Zero, shakeRect.Left + 1, shakeRect.Top, sw, sh, 0x0014u);
				Thread.Sleep(60);
				SetWindowPos(handle, IntPtr.Zero, shakeRect.Left, shakeRect.Top, sw, sh, 0x0014u);
				Thread.Sleep(60);
			}
		}
		// 伪造鼠标激活：WM_MOUSEACTIVATE（HTCLIENT + WM_LBUTTONDOWN）让框架走应用层激活路径
		// （托盘点击恢复的本质=窗口激活，此消息等价于用户点击客户区）。
		PostMessage(handle, 0x0021u, (IntPtr)handle, (IntPtr)((long)0x0201 << 16 | 1));
		IntPtr lastActivePopup = GetLastActivePopup(handle);
		IntPtr intPtr = ((lastActivePopup != IntPtr.Zero && IsWindowVisible(lastActivePopup)) ? lastActivePopup : handle);
		// AttachThreadInput+BringWindowToTop 链：让本线程短暂拥有前台线程的输入权限，
		// 使 SetForegroundWindow 一次成功（仅 SetForegroundWindow 在此环境常被忽略）。
		IntPtr foregroundWindow = GetForegroundWindow();
		uint num = 0u;
		if (foregroundWindow != IntPtr.Zero)
		{
			GetWindowThreadProcessId(foregroundWindow, out num);
		}
		uint currentThreadId = GetCurrentThreadId();
		bool flag = num != 0 && num != currentThreadId && AttachThreadInput(currentThreadId, num, attachOrDetach: true);
		BringWindowToTop(intPtr);
		SetWindowPos(intPtr, IntPtr.Zero, 0, 0, 0, 0, 3u);
		// Windows 前台锁定：SetForegroundWindow 通常只允许前台进程调用。
		// 通用破解：模拟一次 Alt 键，系统会重置前台锁定状态。
		keybd_event(18, 0, 0u, UIntPtr.Zero);
		keybd_event(18, 0, 2u, UIntPtr.Zero);
		bool flag2 = Native.SetForegroundWindow(intPtr);
		int fgTries = 0;
		while (!flag2 && fgTries < 3)
		{
			// 部分应用恢复/创建窗口后短时间拒绝前台请求，BringWindowToTop 后重试。
			Thread.Sleep(50);
			keybd_event(18, 0, 0u, UIntPtr.Zero);
			keybd_event(18, 0, 2u, UIntPtr.Zero);
			BringWindowToTop(intPtr);
			flag2 = Native.SetForegroundWindow(intPtr);
			fgTries++;
		}
		if (!flag2)
		{
			// 最终兜底：任务栏同款强切（未文档化 API，强制前台+恢复）。
			SwitchToThisWindow(intPtr, true);
			flag2 = GetForegroundWindow() == intPtr;
		}
		if (!flag2)
		{
			// 前台锁无法突破（此环境前台进程为第三方桌面壳，SetForegroundWindow 全被拒）：
			// 注入一次真实鼠标点击到目标窗口标题栏，Windows 视为真实输入 → 目标窗口
			// 获得激活 + 键盘焦点（与用户亲手点击等价）。点击后恢复鼠标原位。
			ClickToActivate(intPtr);
			flag2 = GetForegroundWindow() == intPtr;
		}
		Store.Trace("ACTIVATE_R foreground " + (flag2 ? "ok" : "blocked") + " hwnd=" + intPtr.ToInt64() + " fg=" + GetForegroundWindow().ToInt64());
		if (flag)
		{
			AttachThreadInput(currentThreadId, num, attachOrDetach: false);
		}
		if (!flag2)
		{
			FLASHWINFO info = new FLASHWINFO
			{
				Size = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
				Window = intPtr,
				Flags = 3u,
				Count = 2u
			};
			FlashWindowEx(ref info);
			Store.Trace("WINDOW_SWITCH foreground request blocked handle=" + intPtr);
		}
	}

	/// <summary>
	/// 终极激活兜底：前台锁无法突破时，注入一次真实鼠标点击到目标窗口标题栏
	/// （Windows 将注入输入视为真实用户操作 → 目标窗口获得激活+键盘焦点）。
	/// 点标题栏左 1/4 处避开右侧按钮，点击后恢复鼠标原位。
	/// </summary>
	private static void ClickToActivate(IntPtr handle)
	{
		try
		{
			if (!GetWindowRect(handle, out RECT rect))
			{
				return;
			}
			int w = rect.Right - rect.Left;
			int h = rect.Bottom - rect.Top;
			if (w < 60 || h < 40)
			{
				return;
			}
			int x = rect.Left + w / 4;
			int y = rect.Top + Math.Min(18, h / 4);
			int screenW = Math.Max(1, GetSystemMetrics(0));
			int screenH = Math.Max(1, GetSystemMetrics(1));
			uint sx = (uint)(x * 65535 / Math.Max(1, screenW - 1));
			uint sy = (uint)(y * 65535 / Math.Max(1, screenH - 1));
			GetCursorPos(out POINT orig);
			Store.Trace("ACTIVATE_R clickToActivate at=" + x + "," + y + " orig=" + orig.X + "," + orig.Y);
			SetCursorPos(x, y);
			mouse_event(2u, sx, sy, 0u, UIntPtr.Zero);
			Thread.Sleep(40);
			mouse_event(4u, sx, sy, 0u, UIntPtr.Zero);
			Thread.Sleep(150);
			SetCursorPos(orig.X, orig.Y);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	/// <summary>
	/// 同进程主窗体兜底：目标窗口无法恢复时，枚举同 pid 的顶层窗口，
	/// 排除 tooltip/IME/消息壳等工具类，取面积最大者（应用真正的 UI 主窗体）。
	/// </summary>
	private static IntPtr FindMainWindowOf(IntPtr handle)
	{
		IntPtr best = IntPtr.Zero;
		long bestArea = 0L;
		try
		{
			GetWindowThreadProcessId(handle, out uint targetPid);
			if (targetPid == 0)
			{
				return IntPtr.Zero;
			}
			EnumWindows(delegate (IntPtr h, IntPtr value)
			{
				if (!IsWindow(h))
				{
					return true;
				}
				GetWindowThreadProcessId(h, out uint pid);
				if (pid != targetPid)
				{
					return true;
				}
				StringBuilder cls = new StringBuilder(256);
				GetClassName(h, cls, 256);
				string c = cls.ToString();
				if (c.IndexOf("tooltips_class", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("MSCTFIME", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("BroadcastEventWindow", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("GDI+", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("ADLXEventWindow", StringComparison.OrdinalIgnoreCase) >= 0 || (c.StartsWith("IME", StringComparison.OrdinalIgnoreCase) && c.Length <= 12) || c.IndexOf("MessageWindow", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("Message", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("HookWindow", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("TrayWindow", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("TrayIcon", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("Notify", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
				int w;
				int hh;
				if (IsIconic(h))
				{
					// 最小化窗口的 GetWindowRect 是 -32000 处的小矩形，用还原尺寸判定面积。
					WINDOWPLACEMENT placement = default(WINDOWPLACEMENT);
					placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
					if (!GetWindowPlacement(h, ref placement))
					{
						return true;
					}
					w = placement.rcNormalPosition.Right - placement.rcNormalPosition.Left;
					hh = placement.rcNormalPosition.Bottom - placement.rcNormalPosition.Top;
				}
				else
				{
					GetWindowRect(h, out RECT r);
					w = r.Right - r.Left;
					hh = r.Bottom - r.Top;
				}
				if (w < 80 || hh < 60)
				{
					return true;
				}
				long area = (long)w * (long)hh;
				if (area > bestArea)
				{
					bestArea = area;
					best = h;
				}
				return true;
			}, IntPtr.Zero);
		}
		catch (Exception)
		{
		}
		return best;
	}

	/// <summary>
	/// 窗口"还原尺寸"面积：最小化窗口 GetWindowRect 是 -32000 处小矩形，
	/// 用 GetWindowPlacement 的 rcNormalPosition 还原真实尺寸参与比较。
	/// </summary>
	private static long NormalArea(IntPtr h)
	{
		try
		{
			if (IsIconic(h))
			{
				WINDOWPLACEMENT p = default(WINDOWPLACEMENT);
				p.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
				if (GetWindowPlacement(h, ref p))
				{
					return (long)(p.rcNormalPosition.Right - p.rcNormalPosition.Left) * (long)(p.rcNormalPosition.Bottom - p.rcNormalPosition.Top);
				}
			}
			GetWindowRect(h, out RECT r);
			return (long)(r.Right - r.Left) * (long)(r.Bottom - r.Top);
		}
		catch (Exception)
		{
			return 0L;
		}
	}

	public static bool Activate(Entry entry)
	{
		Scan();
		// 微信 4.0（Qt 多进程+托盘驻留）特例：主窗"大退"/隐藏到托盘后 Qt 应用层进入
		// 锁定态，外部 ShowWindow 恢复链只能唤醒 Win32 壳（标题栏按钮可用、客户区
		// 聊天列表点击无响应="半死窗口"，实测多轮修复均无效）。唯一可靠解锁路径=
		// 微信单实例启动转发（等效托盘点击，微信自己走完整恢复流程）。
		// 检测到隐藏主窗时放弃激活返回 false，让 Controller.Launch 走 Process.Start，
		// 后续 delayed-activate 轮询等主窗重新出现再激活（IsMainWindowSize 已过滤过渡小窗）。
		// 最小化（IsIconic）窗口不受影响：走正常恢复链即可。
		string entryPath = entry == null ? "" : entry.Path;
		string entryTarget = entry == null ? "" : (entry.LaunchTarget ?? "");
		if (entryPath.IndexOf("Weixin", StringComparison.OrdinalIgnoreCase) >= 0 ||
			entryPath.IndexOf("WeChat", StringComparison.OrdinalIgnoreCase) >= 0 ||
			entryTarget.IndexOf("Weixin", StringComparison.OrdinalIgnoreCase) >= 0 ||
			entryTarget.IndexOf("WeChat", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			List<RunningWindow> wechatHidden = ForHidden(entry);
			if (wechatHidden.Any((RunningWindow w) => !IsWindowVisible(w.Handle) || IsIconic(w.Handle)))
			{
				// 微信 4.0"大退"后主窗进入 Qt 应用层锁定态（"半死窗口"）：外部 ShowWindow
				// 恢复链只能唤醒 Win32 壳（标题栏按钮可用、客户区聊天列表无响应），
				// 实测多轮修复无效。唯一可靠的官方路径=微信全局热键 Ctrl+Alt+W
				// （微信自己注册、自己处理=等效托盘点击），热键无效时回退单实例启动转发。
				// 全程后台线程执行，UI 线程立即返回（点击图标零卡顿）。
				Store.Trace("ACTIVATE wechat hidden-main → hotkey restore path");
				string launchTarget = entryTarget;
				System.Threading.Tasks.Task.Run(delegate
				{
					try
					{
						WechatHotkeyRestore(entry, launchTarget);
					}
					catch (Exception)
					{
					}
				});
				return true;
			}
		}
		List<RunningWindow> list = For(entry);
		if (list.Count == 0)
		{
			// launcher 入口（汽水音乐等）：入口路径与主程序窗口路径不一致，
			// 用本 Dock 的启动记录找回窗口，让点击行为与普通应用完全一致。
			List<RunningWindow> launchWins = LaunchedWindows(entry == null ? "" : entry.Path);
			if (launchWins.Count > 0)
			{
				list = launchWins;
				Store.Trace("ACTIVATE launch-window fallback count=" + list.Count);
			}
			// 快照只收可见且未被 DWM cloaked 的窗口：大退/最小化到托盘的应用
			// （微信 4.0 关主窗=最小化到 -32000，cloaked 被快照排除）主窗不在快照里，
			// 定向枚举该入口进程的隐藏/最小化主窗找回，点击图标恢复原窗口而不是重开。
			if (list.Count == 0)
			{
				list = ForHidden(entry);
				if (list.Count > 0)
				{
					Store.Trace("ACTIVATE hidden-window fallback count=" + list.Count);
				}
			}
		}
		if (list.Count > 0)
		{
			// 多进程应用（微信 4.0）：主窗大退/最小化到 -32000 后被 DWM cloaked 移出快照，
			// 但同程序子进程的可见小窗（登录窗/过渡窗）仍在快照里会 match——
			// 直接激活它用户看到的是"新的窗口"。这里把隐藏/最小化的主窗找回来优先恢复。
			List<RunningWindow> hiddenMain = ForHidden(entry).Where((RunningWindow w) => !IsWindowVisible(w.Handle) || IsIconic(w.Handle)).ToList();
			if (hiddenMain.Count > 0)
			{
				hiddenMain.Sort((RunningWindow a, RunningWindow b) => NormalArea(b.Handle).CompareTo(NormalArea(a.Handle)));
				list = hiddenMain;
				Store.Trace("ACTIVATE hidden-main prefer count=" + hiddenMain.Count + " first=" + hiddenMain[0].Handle.ToInt64());
			}
			// 只过滤不可见窗口（DDE 服务窗、隐藏的 Chrome_WidgetWin_0 等被误当候选时，
			// 点击会去"激活"一个看不见的窗口）。最小化窗口必须保留：点击=恢复最大化。
			list = list.Where((RunningWindow w) => IsWindowVisible(w.Handle)).ToList();
			if (list.Count == 0)
			{
				// 应用"大退"到托盘（如微信：关闭主窗后进程驻留，主窗被隐藏）：
				// 全部窗口不可见时，定向枚举该入口进程的隐藏主窗强制显示，
				// 点击 Dock 图标即恢复窗口；否则会走到 Process.Start 重开，
				// 单实例应用会弹出全新窗口而不是恢复原会话。
				// 注意：For(entry) 的窗口快照只收可见窗口，隐藏窗必须用 ForHidden 定向枚举。
				List<RunningWindow> hidden = ForHidden(entry);
				if (hidden.Count == 0)
				{
					hidden = For(entry).Where((RunningWindow w) => !IsWindowVisible(w.Handle)).ToList();
				}
				if (hidden.Count > 0)
				{
					IntPtr main = FindMainWindowOf(hidden[0].Handle);
					if (main != IntPtr.Zero)
					{
						if (!IsWindowVisible(main))
						{
							// 隐藏主窗：同步 ShowWindow 显示→真实最小化→恢复（见 ACTIVATE_R hiddenShown 注释）。
							PostMessage(main, 0x0112u, (IntPtr)0xF120, IntPtr.Zero);
							Thread.Sleep(80);
							ShowWindow(main, 1);
							Thread.Sleep(350);
							ShowWindow(main, 6);
							Thread.Sleep(550);
							ShowWindow(main, 9);
						}
						Store.Trace("ACTIVATE hidden-main restore hwnd=" + main.ToInt64());
						list.Add(new RunningWindow
						{
							Handle = main,
							ProcessId = hidden[0].ProcessId,
							Path = hidden[0].Path,
							Title = hidden[0].Title
						});
					}
				}
			}
		}
		Store.Trace("ACTIVATE entry=" + (entry == null ? "null" : entry.Path) + " launchTarget=" + (entry == null ? "" : entry.LaunchTarget) + " windowTarget=" + (entry == null ? "" : entry.WindowTarget) + " matches=" + list.Count);
		IntPtr foregroundWindow = EffectiveForeground();
		GetWindowThreadProcessId(foregroundWindow, out var id);
		Store.Trace("ACTIVATE foreground=" + foregroundWindow.ToInt64() + " pid=" + id + " path=" + ProcessPath(id) + " own=" + (id == (uint)Process.GetCurrentProcess().Id));
		// 前台被置顶工具（DSH Desktop 等）抢占时 EffectiveForeground 不属于本应用，
		// 但用户眼前可能正是该应用的窗口：用 Z 序最上层可见窗口兜底判定"已在最上层"。
		if (foregroundWindow == IntPtr.Zero || !list.Any((RunningWindow w) => w.Handle == foregroundWindow))
		{
			IntPtr topVisible = TopVisibleOf(list);
			if (topVisible != IntPtr.Zero)
			{
				foregroundWindow = topVisible;
				id = 0u;
			}
		}		if (list.Any((RunningWindow w) => w.Handle == foregroundWindow))
		{
			// 前台窗口属于该应用：沿 Z 序向下找同一应用的下一个可见窗口并提到上层
			//（两个窗口时点 Dock 把下面的换上来）；只有一个可见窗口才走"再点最小化"。
			IntPtr below = NextVisibleBelow(foregroundWindow, list);
			if (below != IntPtr.Zero)
			{
				Store.Trace("ACTIVATE raise below hwnd=" + below.ToInt64());
				return ActivateHandle(below);
			}
			Store.Trace("ACTIVATE minimize branch");
			Minimize(foregroundWindow);
			return true;
		}
		if (list.Count == 0)
		{
			// 路径匹配失败（提权 / UWP / 启动器→子进程等）时，用"本 Dock 启动过的进程"兜底：
			// 前台窗口属于此前从该入口启动的进程（或其子孙进程）→ 同样执行"再点最小化"。
			if (foregroundWindow != IntPtr.Zero && id != 0u && MatchesLaunch(entry.Path, (int)id))
			{
				Store.Trace("ACTIVATE launch-track minimize");
				Minimize(foregroundWindow);
				return true;
			}
			return false;
		}
		last.TryGet(entry.Path, out var value);
		long value2 = WindowActivationRules.Select(list.Select((RunningWindow w) => w.Handle.ToInt64()), foregroundWindow.ToInt64(), value, id == (uint)Process.GetCurrentProcess().Id);
		Store.Trace("ACTIVATE switch to=" + value2);
		if (!ActivateHandle(new IntPtr(value2)))
		{
			return false;
		}
		// 乐观跟踪：激活成功后立即把目标窗口记为前台候选，
		// 用户紧接着再点图标（想要最小化）时不会因前台事件延迟而多按一次。
		SetTracked(new IntPtr(value2));
		last.Put(entry.Path, value2);
		return true;
	}

	// 微信 4.0 大退主窗恢复：模拟微信全局热键 Ctrl+Alt+W（微信自己注册、自己处理，
	// 等效托盘点击——托盘点击是唯一能解锁 Qt 层"大退锁定"的官方路径，外部 ShowWindow
	// 恢复链实测无效）。热键无效时回退 Process.Start 单实例转发。全程后台线程执行。
	private static void WechatHotkeyRestore(Entry entry, string launchTarget)
	{
		// 1) 模拟 Ctrl+Alt+W
		keybd_event(0x11, 0, 0u, UIntPtr.Zero);
		keybd_event(0x12, 0, 0u, UIntPtr.Zero);
		Thread.Sleep(30);
		keybd_event(0x57, 0, 0u, UIntPtr.Zero);
		Thread.Sleep(60);
		keybd_event(0x57, 0, 2u, UIntPtr.Zero);
		keybd_event(0x12, 0, 2u, UIntPtr.Zero);
		keybd_event(0x11, 0, 2u, UIntPtr.Zero);
		// 2) 轮询 2.5s 等主窗恢复（微信处理热键需要几百 ms）
		for (int i = 0; i < 25; i++)
		{
			Thread.Sleep(100);
			List<RunningWindow> wins = ForHidden(entry);
			RunningWindow main = wins.FirstOrDefault((RunningWindow w) => IsWindowVisible(w.Handle) && !IsIconic(w.Handle));
			if (main != null)
			{
				Store.Trace("ACTIVATE wechat hotkey restore ok hwnd=" + main.Handle.ToInt64());
				RestoreAndForeground(main.Handle);
				return;
			}
		}
		// 3) 热键无效（用户在微信设置里关了热键）→ 回退单实例启动转发
		Store.Trace("ACTIVATE wechat hotkey restore failed → single-instance launch fallback");
		try
		{
			string target = string.IsNullOrEmpty(launchTarget) ? entry.Path : launchTarget;
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
		}
		catch (Exception)
		{
		}
	}

	public static bool IsForeground(IntPtr handle)
	{
		return EffectiveForeground() == handle;
	}

	// —— 任务栏式窗口操作（右键菜单用）：全部 O(1) 状态判定 + 单条 Win32 调用 ——
	public static bool IsMinimized(IntPtr handle)
	{
		return IsWindow(handle) && IsIconic(handle);
	}

	public static bool IsMaximized(IntPtr handle)
	{
		return IsWindow(handle) && IsZoomed(handle);
	}

	public static void Restore(IntPtr handle)
	{
		if (IsWindow(handle))
		{
			ShowWindowAsync(handle, 9);
		}
	}

	public static void Maximize(IntPtr handle)
	{
		if (IsWindow(handle))
		{
			ShowWindowAsync(handle, 3);
		}
	}

	// 任务栏式「关闭所有窗口」：逐个发 WM_CLOSE，优雅关闭优先。
	public static void CloseAll(Entry entry)
	{
		foreach (RunningWindow w in For(entry))
		{
			Close(w.Handle);
		}
	}

	// 再点 Dock 图标时把前台窗口最小化（任务栏式切换；进程保留，再点恢复）。
	public static void Close(IntPtr handle)
	{
		PostMessage(handle, 16u, IntPtr.Zero, IntPtr.Zero);
	}

	public static void Minimize(IntPtr handle)
	{
		bool flag = IsWindow(handle);
		Store.Trace("MINIMIZE hwnd=" + handle.ToInt64() + " valid=" + flag);
		if (flag)
		{
			ShowWindowAsync(handle, 6);
			// 提权窗口（如管理员权限的任务管理器）受 UIPI 拦截，ShowWindowAsync 静默失败。
			// 兜底：稍后检查若窗口仍在前台且未最小化，用物理键盘输入 Win+Down 最小化。
			Task.Run(delegate
			{
				Thread.Sleep(160);
				try
				{
					if (IsIconic(handle) || !IsForeground(handle))
					{
						return;
					}
					Store.Trace("MINIMIZE fallback keybd Win+Down hwnd=" + handle.ToInt64());
					keybd_event(0x5B, 0, 0u, UIntPtr.Zero);
					keybd_event(0x28, 0, 0u, UIntPtr.Zero);
					keybd_event(0x28, 0, 2u, UIntPtr.Zero);
					keybd_event(0x5B, 0, 2u, UIntPtr.Zero);
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			});
		}
	}

	// 从 Z 序顶端向下找第一个可见且非置顶的窗口；若它属于 list 则视为"已在最上层"
	//（前台被置顶工具抢占时 EffectiveForeground 不可靠，此兜底保证再点最小化仍生效）。
	private static IntPtr TopVisibleOf(List<RunningWindow> list)
	{
		HashSet<long> candidates = new HashSet<long>(list.Select((RunningWindow w) => w.Handle.ToInt64()));
		IntPtr handle = GetWindow(IntPtr.Zero, 0u);
		while (handle != IntPtr.Zero)
		{
			if (IsWindowVisible(handle) && (GetWindowLong(handle, -20) & 0x8) == 0)
			{
				if (!candidates.Contains(handle.ToInt64()))
				{
					return IntPtr.Zero;
				}
				return handle;
			}
			handle = GetWindow(handle, 2u);
		}
		return IntPtr.Zero;
	}

	// 从 start 沿 Z 序向下（GW_HWNDNEXT）找 list 中第一个可见且未最小化的其他窗口。
	private static IntPtr NextVisibleBelow(IntPtr start, List<RunningWindow> list)
	{
		HashSet<long> candidates = new HashSet<long>(list.Where((RunningWindow w) => w.Handle != start).Select((RunningWindow w) => w.Handle.ToInt64()));
		IntPtr handle = GetWindow(start, 2u);
		while (handle != IntPtr.Zero)
		{
			if (candidates.Contains(handle.ToInt64()) && IsWindowVisible(handle) && !IsIconic(handle))
			{
				return handle;
			}
			handle = GetWindow(handle, 2u);
		}
		return IntPtr.Zero;
	}

	// —— 启动跟踪：路径匹配失败时的"再点最小化"兜底 ——

	private sealed class LaunchInfo
	{
		public int Pid;

		public string ProcessName;

		public int Tick;
	}

	private static readonly Dictionary<string, List<LaunchInfo>> launched = new Dictionary<string, List<LaunchInfo>>(StringComparer.OrdinalIgnoreCase);

	// 启动器→主程序常见命名（SodaMusicLauncher→SodaMusic）：进程名去掉这些后缀后
	// 再与运行进程匹配，保证"点启动器图标"能找到真正的主程序窗口。
	private static string RootName(string processName)
	{
		if (string.IsNullOrEmpty(processName))
		{
			return "";
		}
		string text = processName;
		foreach (string suffix in new string[6] { "Launcher", "Loader", "Starter", "Boot", "Helper", "Handler" })
		{
			if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && text.Length > suffix.Length)
			{
				text = text.Substring(0, text.Length - suffix.Length);
				break;
			}
		}
		return text;
	}

	// 返回该入口"本 Dock 启动过"的当前存活进程 id 集合：
	// ① 记录 pid 本身仍存活；② 记录进程名 / 去后缀根名 仍能枚举到进程（启动器退出、
	// 主程序独立运行的场景）。所有进程枚举都包 try/catch，失败不影响点击。
	public static HashSet<int> ActiveLaunchPids(string path)
	{
		HashSet<int> result = new HashSet<int>();
		if (string.IsNullOrEmpty(path) || !launched.TryGetValue(path, out var list))
		{
			return result;
		}
		foreach (LaunchInfo info in list)
		{
			try
			{
				using (Process p = Process.GetProcessById(info.Pid))
				{
					result.Add(info.Pid);
				}
			}
			catch
			{
			}
			HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { info.ProcessName, RootName(info.ProcessName) };
			foreach (string name in names)
			{
				if (string.IsNullOrEmpty(name))
				{
					continue;
				}
				try
				{
					Process[] processesByName = Process.GetProcessesByName(name);
					foreach (Process p2 in processesByName)
					{
						result.Add(p2.Id);
						p2.Dispose();
					}
				}
				catch
				{
				}
			}
		}
		return result;
	}

	// 路径匹配失败时用"启动记录"找回窗口：汽水音乐这类 launcher 入口，
	// 主程序窗口路径与入口不同，靠它实现统一的"点击打开 / 再点最小化"。
	private static List<RunningWindow> LaunchedWindows(string path)
	{
		HashSet<int> pids = ActiveLaunchPids(path);
		if (pids.Count == 0)
		{
			return new List<RunningWindow>();
		}
		return (from w in Snapshot()
			where pids.Contains((int)w.ProcessId)
			orderby w.Handle.ToInt64()
			select w).ToList();
	}

	// 最近一次从该入口启动是否发生在冷却期内（避免 launcher 应用被反复拉起）。
	public static bool RecentlyLaunched(string path)
	{
		if (string.IsNullOrEmpty(path) || !launched.TryGetValue(path, out var list) || list.Count == 0)
		{
			return false;
		}
		int tick = Environment.TickCount;
		LaunchInfo launchInfo = list[list.Count - 1];
		int num = tick - launchInfo.Tick;
		return num >= 0 && num < 12000;
	}

	public static void NoteLaunch(string path, int pid, string processName)
	{
		if (string.IsNullOrEmpty(path) || pid <= 0)
		{
			return;
		}
		if (!launched.TryGetValue(path, out var list))
		{
			list = new List<LaunchInfo>();
			launched[path] = list;
		}
		list.RemoveAll((LaunchInfo x) => x.Pid == pid);
		list.Add(new LaunchInfo
		{
			Pid = pid,
			ProcessName = processName,
			Tick = Environment.TickCount
		});
		if (list.Count > 4)
		{
			list.RemoveRange(0, list.Count - 4);
		}
		if (launched.Count > 96)
		{
			string oldest = launched.Keys.FirstOrDefault();
			if (oldest != null)
			{
				launched.Remove(oldest);
			}
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ProcessEntry32
	{
		public uint Size;

		public uint Usage;

		public uint ProcessId;

		public UIntPtr DefaultHeapId;

		public uint ModuleId;

		public uint Threads;

		public uint ParentProcessId;

		public int PriorityClassBase;

		public uint Flags;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string ExeFile;
	}

	private static Dictionary<int, int> parentMap;

	private static int parentMapTick;

	private static Dictionary<int, int> ParentMap()
	{
		int tick = Environment.TickCount;
		if (parentMap != null && tick - parentMapTick >= 0 && tick - parentMapTick < 2000)
		{
			return parentMap;
		}
		Dictionary<int, int> map = new Dictionary<int, int>();
		IntPtr snapshot = CreateToolhelp32Snapshot(2u, 0u);
		if (snapshot != (IntPtr)(-1))
		{
			try
			{
				ProcessEntry32 entry = default(ProcessEntry32);
				entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
				if (Process32FirstW(snapshot, ref entry))
				{
					do
					{
						map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
					}
					while (Process32NextW(snapshot, ref entry));
				}
			}
			finally
			{
				CloseHandle(snapshot);
			}
		}
		parentMap = map;
		parentMapTick = tick;
		return map;
	}

	private static bool DescendantOf(int pid, int ancestor)
	{
		if (pid == ancestor)
		{
			return true;
		}
		Dictionary<int, int> map = ParentMap();
		int num = 0;
		int current = pid;
		while (num < 10 && map.TryGetValue(current, out var parent) && parent != 0 && parent != current)
		{
			if (parent == ancestor)
			{
				return true;
			}
			current = parent;
			num++;
		}
		return false;
	}

	private static bool MatchesLaunch(string path, int foregroundPid)
	{
		if (string.IsNullOrEmpty(path) || !launched.TryGetValue(path, out var list))
		{
			return false;
		}
		foreach (LaunchInfo info in list)
		{
			if (foregroundPid == info.Pid || DescendantOf(foregroundPid, info.Pid))
			{
				return true;
			}
		}
		return false;
	}
}
