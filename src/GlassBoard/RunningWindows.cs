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
	private static extern bool IsWindowVisible(IntPtr window);

	[DllImport("user32.dll")]
	private static extern bool SetCursorPos(int x, int y);

	[DllImport("user32.dll")]
	private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(IntPtr window);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(IntPtr window);

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindow(IntPtr window, uint command);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong(IntPtr window, int index);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);

	[DllImport("user32.dll")]
	private static extern bool ShowWindowAsync(IntPtr window, int command);

	[DllImport("user32.dll")]
	private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

	[DllImport("user32.dll")]
	private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

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
		trackedForeground = foregroundWindow;
		trackedForegroundTick = Environment.TickCount;
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
					trackedForeground = hwnd;
					trackedForegroundTick = Environment.TickCount;
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
			trackedForeground = hwnd;
			trackedForegroundTick = Environment.TickCount;
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
			trackedForeground = IntPtr.Zero;
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
		trackedForeground = hwnd;
		trackedForegroundTick = Environment.TickCount;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

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
			return WindowActivationRules.NormalizePath(stringBuilder.ToString());
		}
		finally
		{
			CloseHandle(intPtr);
		}
	}

	public static void Scan()
	{
		windows.Clear();
		background.Clear();
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
				if (DwmGetWindowAttribute(handle, 14, out var value2, 4) == 0 && value2 != 0)
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
			if (new string[4] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" }.Contains(stringBuilder2.ToString()))
			{
				return true;
			}
			windows.Add(new RunningWindow
			{
				Handle = handle,
				ProcessId = id,
				Path = value3,
				Title = stringBuilder.ToString()
			});
			return true;
		}, IntPtr.Zero);
		ScanTray(byPid, own);
		ScanProcesses(byPid, own);
	}

	// 附属/运行时进程名（Windows 自带、框架运行时、内嵌解释器——永远不进运行列表）。
	private static readonly HashSet<string> SubprocessExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"node", "VBCSCompiler", "WeChatAppEx", "msedge", "msedgewebview2", "nahimicNotifSys", "crashpad_handler", "identity_helper", "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost", "TextInputHost", "Widgets", "WidgetService", "CrossDeviceService", "PhoneExperienceHost", "MicrosoftStartFeedProvider", "WidgetBoard", "GameBar", "GameBarFTServer"
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
	private static void ScanProcesses(Dictionary<uint, string> byPid, int own)
	{
		HashSet<string> havePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (RunningWindow w in windows)
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
				if (process.Id == own || process.SessionId != session)
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
				if (lower.Contains("update") || lower.Contains("crash") || lower.Contains("install") || lower.Contains("setup") || lower.Contains("helper") || lower.Contains("cef") || lower.Contains("service") || lower.Contains("daemon") || lower.Contains("elevat") || lower.Contains("remoting") || lower.Contains("notifier") || lower.Contains("tray") || lower.Contains("server") || lower.Contains("renderer") || lower.Contains("notif") || lower.Contains("broker") || lower.Contains("agent") || lower.Contains("companion") || lower.Contains("watcher") || lower.Contains("monitor") || lower.Contains("webview") || lower.Contains("background") || lower.Contains("backend"))
				{
					continue;
				}
				if (SubprocessExeNames.Contains(processName))
				{
					continue;
				}
				if (!byPid.TryGetValue((uint)process.Id, out var path))
				{
					try
					{
						path = ProcessPath((uint)process.Id);
					}
					catch
					{
						path = null;
					}
					if (path == null)
					{
						continue;
					}
					byPid[(uint)process.Id] = path;
				}
				if (string.IsNullOrEmpty(path) || havePaths.Contains(path) || IsSystemArea(path))
				{
					continue;
				}
				havePaths.Add(path);
				background.Add(new RunningWindow
				{
					Handle = IntPtr.Zero,
					ProcessId = (uint)process.Id,
					Path = path,
					Title = processName,
					Tray = true
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
	}

	// 第二遍：隐藏窗口——只收"系统托盘里真实存在的应用"（窗口标题/进程名与托盘图标名匹配），其余隐藏辅助窗口一律不收。
	private static void ScanTray(Dictionary<uint, string> byPid, int own)
	{
		List<string> tray = TrayNames();
		string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		HashSet<uint> taken = new HashSet<uint>();
		foreach (RunningWindow w in windows)
		{
			taken.Add(w.ProcessId);
		}
		string[] systemClasses = new string[9] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "DV2ControlHost", "ForegroundStaging", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "Shell_ToastWindow" };
		EnumWindows((IntPtr handle, IntPtr value) =>
		{
			if (IsWindowVisible(handle))
			{
				return true;
			}
			int windowLong = GetWindowLong(handle, -20);
			if ((windowLong & 0x80) != 0)
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
				if (DwmGetWindowAttribute(handle, 14, out var value2, 4) == 0 && value2 != 0)
				{
					return true;
				}
			}
			catch
			{
			}
			GetWindowThreadProcessId(handle, out var id);
			if (id == own || id == 0u || taken.Contains(id))
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
			if (string.IsNullOrEmpty(value3))
			{
				return true;
			}
			if (value3.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			string exeName = Path.GetFileNameWithoutExtension(value3);
			if (SystemSoftwareExeNames.Contains(exeName))
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
			if (!MatchesTray(title, exeName, tray))
			{
				return true;
			}
			taken.Add(id);
			windows.Add(new RunningWindow
			{
				Handle = handle,
				ProcessId = id,
				Path = value3,
				Title = title,
				Tray = true
			});
			return true;
		}, IntPtr.Zero);
	}

	// 窗口标题/进程名与任一托盘图标名互含即视为托盘应用（双向 Contains，忽略大小写）。
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
			if (title.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0 || exeName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		return false;
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
			IntPtr shell = FindWindow("Shell_TrayWnd", null);
			if (shell != IntPtr.Zero)
			{
				IntPtr notify = FindWindowEx(shell, IntPtr.Zero, "TrayNotifyWnd", null);
				if (notify != IntPtr.Zero)
				{
					try
					{
						AutomationElement notifyElement = AutomationElement.FromHandle(notify);
						AutomationElementCollection buttons = notifyElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
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
				}
				if (names.Count == 0)
				{
					IntPtr sys = (notify != IntPtr.Zero) ? FindWindowEx(notify, IntPtr.Zero, "SysPager", null) : IntPtr.Zero;
					CollectToolbarNames(sys, names);
					IntPtr overflow = (notify != IntPtr.Zero) ? FindWindowEx(notify, IntPtr.Zero, "NotifyIconOverflowWindow", null) : IntPtr.Zero;
					CollectToolbarNames(overflow, names);
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

	// 唤醒纯托盘程序（无可见窗口，如 FlClash）：
	// 1) 先尝试恢复该进程的隐藏窗口（很多应用"关闭"只是隐藏，句柄仍在）；
	// 2) 仍无窗口时用 UIA 找到系统托盘里对应的图标并模拟单击（等同用户手动点托盘图标），
	//    程序会自己弹回主窗口——单实例应用也吃这一套，不会触发重复启动。
	// 返回是否已唤醒（false 时调用方退回 Process.Start 兜底）。
	public static bool WakeTray(RunningWindow window, string[] names)
	{
		if (window == null)
		{
			return false;
		}
		try
		{
			int pid = (int)window.ProcessId;
			// 1) 隐藏窗口恢复
			List<IntPtr> hidden = new List<IntPtr>();
			EnumWindows((IntPtr handle, IntPtr value) =>
			{
				uint owner;
				GetWindowThreadProcessId(handle, out owner);
				if (owner == (uint)pid && !IsWindowVisible(handle) && (GetWindowLong(handle, -20) & 0x80) == 0)
				{
					hidden.Add(handle);
				}
				return true;
			}, IntPtr.Zero);
			foreach (IntPtr handle2 in hidden)
			{
				ShowWindowAsync(handle2, 9);
			}
			Thread.Sleep(300);
			List<IntPtr> visible = new List<IntPtr>();
			EnumWindows((IntPtr handle, IntPtr value) =>
			{
				uint owner;
				GetWindowThreadProcessId(handle, out owner);
				if (owner == (uint)pid && IsWindowVisible(handle) && !IsIconic(handle) && (GetWindowLong(handle, -20) & 0x80) == 0)
				{
					visible.Add(handle);
				}
				return true;
			}, IntPtr.Zero);
			if (visible.Count > 0)
			{
				ActivateHandle(visible[0]);
				Store.Trace("WAKETRAY hidden-window hwnd=" + visible[0].ToInt64());
				return true;
			}
			// 2) UIA 模拟单击托盘图标
			if (InvokeTrayButton(names))
			{
				Store.Trace("WAKETRAY tray-invoke ok");
				return true;
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
		return false;
	}

	// 在系统托盘区域（含溢出区）找名称匹配的图标按钮并单击。Win11 托盘是 XAML 岛，
	// UIA 挂在 TrayNotifyWnd 上仍可枚举到按钮；经典 ToolbarWindow32 则用鼠标坐标点击。
	private static bool InvokeTrayButton(string[] names)
	{
		if (names == null || names.Length == 0)
		{
			return false;
		}
		List<string> candidates = names.Where((string n) => !string.IsNullOrWhiteSpace(n)).Select((string n) => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		bool Matches(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				return false;
			}
			foreach (string candidate in candidates)
			{
				if (candidate.Length < 3)
				{
					continue;
				}
				if (name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0 || candidate.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
			return false;
		}
		try
		{
			IntPtr shell = FindWindow("Shell_TrayWnd", null);
			if (shell != IntPtr.Zero)
			{
				IntPtr notify = FindWindowEx(shell, IntPtr.Zero, "TrayNotifyWnd", null);
				if (notify != IntPtr.Zero)
				{
					AutomationElement notifyElement = AutomationElement.FromHandle(notify);
					AutomationElementCollection buttons = notifyElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
					foreach (AutomationElement button in buttons)
					{
						if (!Matches(button.Current.Name))
						{
							continue;
						}
						if (InvokeElement(button))
						{
							return true;
						}
					}
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool InvokeElement(AutomationElement element)
	{
		try
		{
			if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object patternObject) && patternObject is InvokePattern invokePattern)
			{
				invokePattern.Invoke();
				return true;
			}
		}
		catch
		{
		}
		try
		{
			System.Windows.Point point = element.GetClickablePoint();
			if (point.X > 0.0 || point.Y > 0.0)
			{
				SetCursorPos((int)point.X, (int)point.Y);
				mouse_event(2u, 0u, 0u, 0u, UIntPtr.Zero);
				mouse_event(4u, 0u, 0u, 0u, UIntPtr.Zero);
				return true;
			}
		}
		catch
		{
		}
		return false;
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
		return (from w in windows
			where !string.IsNullOrEmpty(w.Path) && (paths.Contains(w.Path, StringComparer.OrdinalIgnoreCase) || fallbackNames.Contains(Path.GetFileNameWithoutExtension(w.Path)))
			orderby w.Handle.ToInt64()
			select w).ToList();
	}

	public static List<RunningWindow> Snapshot()
	{
		return windows.ToList();
	}

	// 运行菜单专用：可见/托盘窗口 + 无窗口在运行的软件（不含服务进程）。
	public static List<RunningWindow> SnapshotWithBackground()
	{
		return windows.Concat(background).ToList();
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
		if (IsIconic(handle))
		{
			ShowWindowAsync(handle, 9);
			// 等窗口完成恢复动画再抢前台，否则 SetForegroundWindow 会被忽略（微信场景）。
			Thread.Sleep(60);
		}
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
		bool flag2 = Native.SetForegroundWindow(intPtr);
		if (!flag2)
		{
			// 部分应用（微信）恢复后短时间拒绝前台请求，稍候重试一次。
			Thread.Sleep(40);
			flag2 = Native.SetForegroundWindow(intPtr);
		}
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
		return true;
	}

	public static bool Activate(Entry entry)
	{
		Scan();
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
		}
		if (list.Any((RunningWindow w) => w.Handle == foregroundWindow))
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
		trackedForeground = new IntPtr(value2);
		trackedForegroundTick = Environment.TickCount;
		last.Put(entry.Path, value2);
		return true;
	}

	public static bool IsForeground(IntPtr handle)
	{
		return EffectiveForeground() == handle;
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
		return (from w in windows
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
