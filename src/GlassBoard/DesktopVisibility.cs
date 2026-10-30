using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Threading;

namespace GlassBoard;

public static class DesktopVisibility
{
	private delegate bool EnumCallback(IntPtr h, IntPtr data);

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct POINT
	{
		public int X;

		public int Y;
	}

	private struct APPBARDATA
	{
		public uint Size;

		public IntPtr Window;

		public uint Message;

		public uint Edge;

		public RECT Rect;

		public IntPtr Param;
	}

	private static readonly string RecoveryFile;

	private static Dictionary<long, ShellState> original;

	private static bool? taskbarHidden;

	private static bool? iconsHidden;

	// 隐藏桌面图标时的原注册表 HideIcons 值：退出/崩溃恢复时还原。
	private static int? originalHideIcons;

	// 隐藏任务栏时记录的 AppBar 原始状态：恢复时还原工作区占用。
	private static uint? originalAppBarState;

	// 任务栏窗口隐藏节流：explorer 重启重建窗口时避免高频拉扯（comctl32 崩溃循环防护）。
	private static readonly Dictionary<long, DateTime> lastHide = new Dictionary<long, DateTime>();

	// 用户正在与任务栏/开始菜单/搜索交互的最后时刻（TickCount）：交互期间与之后的宽限期
	// 内暂停一切压制（SW_HIDE 与 AppBar 重断言），避免开始菜单/搜索卡顿和动态壁纸黑屏。
	private static int lastInteractionTick;

	// AppBar 状态重断言冷却：工作区变化会触发桌面与动态壁纸层重新布局（黑屏），限制频率。
	private static int lastAppBarTick;

	// 任务栏"闪现"防护：ABS_AUTOHIDE 只是为了释放全屏工作区，但 Explorer 的自动隐藏
	// 状态机在鼠标触及屏幕底边时仍会自己把 Shell_TrayWnd 重新显示出来（我们只是 SW_HIDE
	// 了窗口）。用 WinEvent 监听 SHOW 事件即时压回，不等 2 秒维护循环，避免拖到底边弹任务栏。
	private static IntPtr revealHook = IntPtr.Zero;
	private static WinEventProc revealProc;
	private static readonly Dictionary<long, DateTime> lastRehide = new Dictionary<long, DateTime>();

	private const string ExplorerAdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

	private static readonly DispatcherTimer maintenance;

	// 两阶段隐藏任务栏的延迟定时器（见 SetHidden）：AppBar 工作区变化与 SW_HIDE
	// 分开执行，避免桌面级变更连续叠加引发动态壁纸（Wallpaper Engine）黑屏。
	private static DispatcherTimer hideTaskbarTimer;

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindow(string cls, string title);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr h);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(IntPtr h);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr h, int cmd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool ShowWindowAsync(IntPtr h, int cmd);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr h, out uint processId);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong(IntPtr h, int index);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr h, StringBuilder text, int count);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumCallback callback, IntPtr data);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT pt);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr h, out RECT rect);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr data);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

	[DllImport("shell32.dll")]
	private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

	private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

	[DllImport("user32.dll")]
	private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);

	[DllImport("user32.dll")]
	private static extern bool UnhookWinEvent(IntPtr hook);

	// 桌面图标走 Explorer 原生开关（HideIcons 注册表值 + 广播 WM_SETTINGCHANGE）：
	// Explorer 自己负责隐藏/显示，窗口层不再反复 SW_HIDE，彻底消除图标闪烁。
	private static int? ReadHideIcons()
	{
		using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ExplorerAdvancedKey))
		{
			object value = key?.GetValue("HideIcons");
			return (value is int num) ? new int?(num) : null;
		}
	}

	private static void WriteHideIcons(int value)
	{
		using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ExplorerAdvancedKey))
		{
			key.SetValue("HideIcons", value, Microsoft.Win32.RegistryValueKind.DWord);
		}
		// 广播上限 250ms：个别窗口响应慢时不能让 UI 线程跟着卡 2 秒。
		// SMTO_ABORTIFHUNG(2) 已保证挂起窗口立即跳过，其余窗口 250ms 内足够入队。
		SendMessageTimeout(new IntPtr(0xFFFF), 26u, IntPtr.Zero, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer", 2u, 250u, out IntPtr _);
	}

	static DesktopVisibility()
	{
		RecoveryFile = Path.Combine(Store.Folder, "shell-recovery.json");
		original = new Dictionary<long, ShellState>();
		maintenance = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(2000.0)
		};
		maintenance.Tick += (object s, EventArgs e) =>
		{
			Maintain();
		};
	}

	private static string Class(IntPtr h)
	{
		StringBuilder stringBuilder = new StringBuilder(256);
		GetClassName(h, stringBuilder, 256);
		return stringBuilder.ToString();
	}

	// —— AppBar 状态：ABS_AUTOHIDE(0x1) 让 Explorer 释放任务栏占用的屏幕工作区 ——
	// 单纯 SW_HIDE 任务栏窗口并不会释放工作区：全屏/最大化应用仍按“任务栏高度”留空，
	// 底部出现一条透明任务栏位置。给 AppBar 加上 ABS_AUTOHIDE 后 Explorer 才会广播全屏工作区。
	private static APPBARDATA AppBarData()
	{
		return new APPBARDATA
		{
			Size = (uint)Marshal.SizeOf(typeof(APPBARDATA)),
			Window = FindWindow("Shell_TrayWnd", null)
		};
	}

	private static uint AppBarState()
	{
		APPBARDATA data = AppBarData();
		// 任务栏窗口不存在（explorer 重启中、第三方外壳替换或系统级移除）时静默返回 0，
		// 不能抛异常——Maintain 每 5 秒重试一次，抛异常会刷爆 error.log。
		if (data.Window == IntPtr.Zero)
		{
			return 0u;
		}
		return (uint)SHAppBarMessage(4u, ref data).ToUInt64();
	}

	private static void ApplyAppBarState(uint state)
	{
		APPBARDATA data = AppBarData();
		if (data.Window == IntPtr.Zero)
		{
			return;
		}
		data.Param = new IntPtr(state);
		SHAppBarMessage(10u, ref data);
	}

	// 还原隐藏前记录的 AppBar 状态（含 ABS_AUTOHIDE 位），恢复任务栏工作区占用
	private static void RestoreAppBarState()
	{
		if (originalAppBarState.HasValue)
		{
			try
			{
				ApplyAppBarState(originalAppBarState.Value);
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
			originalAppBarState = null;
		}
	}

	private static List<IntPtr> Taskbars()
	{
		List<IntPtr> result = new List<IntPtr>();
		IntPtr intPtr = FindWindow("Shell_TrayWnd", null);
		if (intPtr != IntPtr.Zero)
		{
			result.Add(intPtr);
		}
		EnumWindows((IntPtr h, IntPtr d) =>
		{
			string text = Class(h);
			if ((text == "Shell_TrayWnd" || text == "Shell_SecondaryTrayWnd") && !result.Contains(h))
			{
				result.Add(h);
			}
			return true;
		}, IntPtr.Zero);
		return result;
	}

	private static List<IntPtr> DesktopLists()
	{
		HashSet<IntPtr> result = new HashSet<IntPtr>();
		Action<IntPtr> addView = (IntPtr view) =>
		{
			result.Add(view);
			EnumChildWindows(view, (IntPtr child, IntPtr data) =>
			{
				if (Class(child) == "SysListView32")
				{
					result.Add(child);
				}
				return true;
			}, IntPtr.Zero);
		};
		EnumWindows((IntPtr h, IntPtr d) =>
		{
			string text = Class(h);
			IntPtr intPtr = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
			if (intPtr != IntPtr.Zero)
			{
				addView(intPtr);
			}
			if (text == "Progman" || text == "WorkerW")
			{
				EnumChildWindows(h, (IntPtr child, IntPtr data) =>
				{
					if (Class(child) == "SHELLDLL_DefView")
					{
						addView(child);
					}
					return true;
				}, IntPtr.Zero);
			}
			return true;
		}, IntPtr.Zero);
		return result.OrderBy((IntPtr h) => (!(Class(h) == "SHELLDLL_DefView")) ? 1 : 0).ToList();
	}

	private static bool MatchesSaved(ShellState state)
	{
		IntPtr intPtr = new IntPtr(state.Handle);
		if (!IsWindow(intPtr) || Class(intPtr) != state.ClassName)
		{
			return false;
		}
		if (state.ClassName == "Shell_TrayWnd" || state.ClassName == "Shell_SecondaryTrayWnd")
		{
			return Taskbars().Contains(intPtr);
		}
		if (state.ClassName == "SysListView32" || state.ClassName == "SHELLDLL_DefView")
		{
			return DesktopLists().Contains(intPtr);
		}
		return false;
	}

	private static void Remember(IEnumerable<IntPtr> handles)
	{
		bool flag = false;
		foreach (IntPtr handle in handles)
		{
			if (!original.ContainsKey(handle.ToInt64()))
			{
				original[handle.ToInt64()] = new ShellState
				{
					Handle = handle.ToInt64(),
					ClassName = Class(handle),
					Visible = ((GetWindowLong(handle, -16) & 0x10000000) != 0),
					HideIcons = originalHideIcons,
					AppBarState = originalAppBarState
				};
				flag = true;
			}
		}
		if (flag)
		{
			Directory.CreateDirectory(Store.Folder);
			string text = RecoveryFile + ".tmp";
			using (FileStream stream = File.Create(text))
			{
				new DataContractJsonSerializer(typeof(List<ShellState>)).WriteObject((Stream)stream, (object)original.Values.ToList());
			}
			if (File.Exists(RecoveryFile))
			{
				File.Replace(text, RecoveryFile, null);
			}
			else
			{
				File.Move(text, RecoveryFile);
			}
		}
	}

	// 只返回桌面图标列表（SysListView32）。隐藏桌面图标时绝不能隐藏 SHELLDLL_DefView，
	// 否则桌面右键菜单会一起失效。
	private static List<IntPtr> IconLists()
	{
		return DesktopLists().Where((IntPtr h) => Class(h) == "SysListView32").ToList();
	}

	public static bool? IsHidden(bool taskbar)
	{
		List<IntPtr> list = (taskbar ? Taskbars() : DesktopLists());
		if (list.Count == 0)
		{
			return null;
		}
		List<IntPtr> list2 = list.Where((IntPtr h) => Class(h) == "SysListView32").ToList();
		if (!taskbar && list2.Count > 0)
		{
			list = list2;
		}
		return !list.Any(IsWindowVisible);
	}

	public static bool DesiredHidden(bool taskbar)
	{
		return (taskbar ? taskbarHidden : iconsHidden) ?? (IsHidden(taskbar) == true);
	}

	public static string SetHidden(bool taskbar, bool hide)
	{
		List<IntPtr> list = (taskbar ? Taskbars() : IconLists());
		string text = (taskbar ? "任务栏" : "桌面图标");
		if (list.Count == 0)
		{
			return "未找到" + text + "，请确认 Windows Explorer 已运行。";
		}
		int num = 0;
		Remember(list);
		if (taskbar && hide)
		{
			if (IsHidden(taskbar: true) == true && AppBarAutohideActive())
			{
				// 已是隐藏状态（崩溃恢复/重复启动等）：不再重复切换 AppBar 与窗口，
				// 避免工作区反复变化引发 Wallpaper Engine 等动态壁纸黑屏。
				Store.Trace("SHELL taskbar already hidden -> skip");
			}
			else
			{
				// 记录原始 AppBar 状态并加上 ABS_AUTOHIDE：Explorer 释放工作区，全屏不留透明条。
				try
				{
					if (originalAppBarState == null)
					{
						originalAppBarState = AppBarState();
					}
					uint state = originalAppBarState.Value;
					if ((state & 1u) == 0)
					{
						long tick = Environment.TickCount;
						ApplyAppBarState(state | 1u);
						Store.Trace("SHELL appbar autohide set ms=" + (Environment.TickCount - tick));
					}
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
				// 两阶段：先让 AppBar 工作区变化被 Explorer/DWM 消化（桌面与动态壁纸层重新
				// 布局），再把任务栏窗口藏起来。同一时刻连发两个桌面级变更时，Wallpaper
				// Engine 的壁纸层容易瞬间黑屏——错开 400ms 后单次过渡更平滑。
				if (hideTaskbarTimer == null)
				{
					hideTaskbarTimer = new DispatcherTimer
					{
						Interval = TimeSpan.FromMilliseconds(400.0)
					};
					hideTaskbarTimer.Tick += delegate
					{
						hideTaskbarTimer.Stop();
						HideTaskbarWindows(list);
						// 即时压回：鼠标触及底边时 Explorer 会把任务栏重新显示出来，不等维护循环
						InstallRevealHook();
					};
				}
				hideTaskbarTimer.Stop();
				hideTaskbarTimer.Start();
			}
		}
		else if (taskbar)
		{
			hideTaskbarTimer?.Stop();
			UninstallRevealHook();
			RestoreAppBarState();
			foreach (IntPtr item in list)
			{
				if (!IsWindowVisible(item))
				{
					ShowWindowAsync(item, 8);
				}
			}
		}
		if (!taskbar)
		{
			if (hide && (ReadHideIcons() ?? 0) == 1 && IsHidden(taskbar: false) == true)
			{
				// 图标已处于隐藏状态（上次会话保持）：跳过注册表写入与 WM_SETTINGCHANGE
				// 全桌面广播。该广播会让 Explorer 重新布局桌面，Wallpaper Engine 等动态
				// 壁纸会瞬间黑屏，且 SendMessageTimeout 最长阻塞 2 秒——启动时不应重发。
				Store.Trace("SHELL icons already hidden -> skip");
			}
			else
			{
				// 诊断（WE 兼容排查）：记录未命中早期返回的原因
				Store.Trace("SHELL icons full path reg=" + (ReadHideIcons() ?? -1) + " hidden=" + IsHidden(taskbar: false) + " lists=" + DesktopLists().Count);
				// 桌面图标：写 Explorer 原生 HideIcons 开关（首次记录原值，退出/崩溃恢复时还原）
				if (originalHideIcons == null)
				{
					originalHideIcons = ReadHideIcons() ?? 0;
				}
				WriteHideIcons(hide ? 1 : 0);
				foreach (IntPtr item in list)
				{
					if (hide && ShellBooting(item))
					{
						// explorer 正在重启初始化：先跳过，由维护循环在窗口稳定后再隐藏，避免崩溃循环
						continue;
					}
					if (!ShowWindowAsync(item, (!hide) ? 8 : 0))
					{
						num++;
						Store.Log(new Win32Exception(Marshal.GetLastWin32Error(), "无法切换 " + Class(item)));
					}
				}
			}
		}
		if (taskbar)
		{
			taskbarHidden = hide;
		}
		else
		{
			iconsHidden = hide;
		}
		if (taskbarHidden == true || iconsHidden == true)
		{
			maintenance.Start();
		}
		else
		{
			maintenance.Stop();
		}
		if (num <= 0)
		{
			return "已请求" + (hide ? "隐藏" : "显示") + text;
		}
		return text + "部分窗口切换失败，详情已记录在 error.log。";
	}

	// SW_HIDE 全部任务栏窗口（两阶段隐藏的第二阶段）。彻底隐藏后悬停不弹出。
	private static int HideTaskbarWindows(List<IntPtr> list)
	{
		int num = 0;
		foreach (IntPtr item in list)
		{
			if (ShellBooting(item) || !ThrottledHide(item))
			{
				// explorer 正在重启初始化或刚隐藏过：由维护循环稍后处理，避免崩溃循环
				continue;
			}
			if (!ShowWindowAsync(item, 0))
			{
				num++;
				Store.Log(new Win32Exception(Marshal.GetLastWin32Error(), "无法隐藏 " + Class(item)));
			}
		}
		Store.Trace("SHELL tray hidden windows=" + (list.Count - num) + " failed=" + num);
		return num;
	}

	private static bool AppBarAutohideActive()
	{
		try
		{
			return (AppBarState() & 1u) != 0;
		}
		catch
		{
			return false;
		}
	}

	public static string ToggleTaskbar()
	{
		return SetHidden(taskbar: true, !DesiredHidden(taskbar: true));
	}

	public static string ToggleIcons()
	{
		return SetHidden(taskbar: false, !DesiredHidden(taskbar: false));
	}

	// explorer 刚启动的一小段时间里桌面图标列表仍在初始化，此时 SW_HIDE 容易与
	// comctl32 的 SysListView32 初始化竞争，引发 explorer 崩溃循环（0xc0000005）。
	private static bool ShellBooting(IntPtr h)
	{
		try
		{
			GetWindowThreadProcessId(h, out uint pid);
			if (pid == 0)
			{
				return true;
			}
			using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById((int)pid);
			return DateTime.UtcNow - process.StartTime.ToUniversalTime() < TimeSpan.FromSeconds(8.0);
		}
		catch
		{
			return true;
		}
	}

	// 单窗口隐藏节流（3 秒），避免 explorer 反复重建时高频 ShowWindowAsync。
	private static bool ThrottledHide(IntPtr h)
	{
		long key = h.ToInt64();
		DateTime utcNow = DateTime.UtcNow;
		if (lastHide.TryGetValue(key, out DateTime value) && utcNow - value < TimeSpan.FromSeconds(3.0))
		{
			return false;
		}
		lastHide[key] = utcNow;
		if (lastHide.Count > 64)
		{
			long oldest = lastHide.OrderBy((KeyValuePair<long, DateTime> pair) => pair.Value).First().Key;
			lastHide.Remove(oldest);
		}
		return true;
	}

	private static void InstallRevealHook()
	{
		if (revealHook != IntPtr.Zero)
		{
			return;
		}
		revealProc = OnReveal;
		revealHook = SetWinEventHook(0x8002u, 0x8002u, IntPtr.Zero, revealProc, 0u, 0u, 0u);
		Store.Trace("TASKBAR reveal hook=" + revealHook.ToInt64());
	}

	private static void UninstallRevealHook()
	{
		if (revealHook == IntPtr.Zero)
		{
			return;
		}
		UnhookWinEvent(revealHook);
		revealHook = IntPtr.Zero;
		revealProc = null;
		lastRehide.Clear();
	}

	// 用户正在与任务栏区域交互：前台是开始菜单/搜索/任务栏本身，或鼠标悬停在已显示的任务栏上。
	// 此时 Explorer 的状态机正在驱动显示动画，任何 SW_HIDE/AppBar 变更都会拉扯它
	// （开始菜单与搜索卡顿、动态壁纸黑屏），必须完全放手。
	private static bool TaskbarInteractionActive()
	{
		IntPtr foreground = GetForegroundWindow();
		if (foreground != IntPtr.Zero)
		{
			string cls = Class(foreground);
			if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "Windows.UI.Core.CoreWindow" || cls == "StartMenuDocked" || cls == "SearchApp" || cls == "Shell_CharmWindow")
			{
				return true;
			}
		}
		if (GetCursorPos(out POINT pt))
		{
			foreach (IntPtr item in Taskbars())
			{
				if (IsWindowVisible(item) && GetWindowRect(item, out RECT rect) && pt.X >= rect.Left && pt.X < rect.Right && pt.Y >= rect.Top && pt.Y < rect.Bottom)
				{
					return true;
				}
			}
		}
		return false;
	}

	// 交互结束后给 Explorer 状态机一个宽限期收尾动画（开始菜单关闭、任务栏自动隐藏回落），
	// 宽限期内不压制，过期后由维护循环接管。
	private static bool InteractionGraceActive()
	{
		return Environment.TickCount - lastInteractionTick < 8000;
	}

	// 任务栏被 Explorer 重新显示时立即压回（WinEvent 回调线程，仅罕见事件）。
	private static void OnReveal(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
	{
		if (taskbarHidden != true || idObject != 0)
		{
			return;
		}
		try
		{
			string cls = Class(hwnd);
			if (cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd")
			{
				return;
			}
			if (!IsWindowVisible(hwnd) || ShellBooting(hwnd))
			{
				return;
			}
			if (TaskbarInteractionActive())
			{
				// 用户在任务栏上（悬停显示/开始菜单打开）：交给 Explorer 的 ABS_AUTOHIDE
				// 状态机自行管理，鼠标离开后它会自己收起，不再 SW_HIDE 拉扯。
				lastInteractionTick = Environment.TickCount;
				return;
			}
			if (InteractionGraceActive())
			{
				return;
			}
			if (!ThrottledRehide(hwnd))
			{
				return;
			}
			Store.Trace("TASKBAR reveal " + cls + " -> re-hide");
			ShowWindowAsync(hwnd, 0);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// 单窗口压回节流（800ms）：Explorer 反复重试显示时避免高频拉扯闪屏。
	private static bool ThrottledRehide(IntPtr h)
	{
		long key = h.ToInt64();
		DateTime utcNow = DateTime.UtcNow;
		if (lastRehide.TryGetValue(key, out DateTime value) && utcNow - value < TimeSpan.FromMilliseconds(800.0))
		{
			return false;
		}
		lastRehide[key] = utcNow;
		if (lastRehide.Count > 32)
		{
			long oldest = lastRehide.OrderBy((KeyValuePair<long, DateTime> pair) => pair.Value).First().Key;
			lastRehide.Remove(oldest);
		}
		return true;
	}

	private static void Maintain()
	{
		try
		{
			// 桌面图标已由 Explorer 原生 HideIcons 负责，这里只做状态对齐：
			// 若用户在桌面右键菜单手动改回显示，重新写回隐藏值即可，不触碰任何窗口，不会闪烁。
			if (iconsHidden == true && (ReadHideIcons() ?? 0) != 1)
			{
				WriteHideIcons(1);
			}
			if (taskbarHidden == true)
			{
				// 用户正在打开/使用开始菜单、搜索或任务栏：完全放手（Explorer 状态机正在
				// 驱动显示动画，此时 SW_HIDE 或工作区变化会让开始菜单卡顿、动态壁纸黑屏）。
				if (TaskbarInteractionActive())
				{
					lastInteractionTick = Environment.TickCount;
					return;
				}
				// 交互刚结束的宽限期：等 Explorer 的自动隐藏动画收尾，不立即压制。
				if (InteractionGraceActive())
				{
					return;
				}
				// explorer 重启/其他软件改动后可能清掉 ABS_AUTOHIDE：重新断言，保持工作区全屏。
				// 冷却 5 秒：工作区变化会触发桌面与壁纸层重新布局，频繁翻转必黑屏。
				try
				{
					if (originalAppBarState.HasValue && (AppBarState() & 1u) == 0 && Environment.TickCount - lastAppBarTick > 5000)
					{
						lastAppBarTick = Environment.TickCount;
						ApplyAppBarState(originalAppBarState.Value | 1u);
					}
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
				// 任务栏窗口被 explorer 重建为可见时重新隐藏（带启动宽限与节流）。
				foreach (IntPtr item in Taskbars())
				{
					if (IsWindowVisible(item) && !ShellBooting(item) && ThrottledHide(item))
					{
						ShowWindowAsync(item, 0);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			maintenance.Stop();
		}
	}

	public static void Recover(bool keepIconsHidden = false, bool keepTaskbarHidden = false)
	{
		if (!File.Exists(RecoveryFile))
		{
			return;
		}
		// 崩溃恢复 + 正常重启共用的路径。若新会话本来就要隐藏任务栏/桌面图标，
		// 就不要先把它们还原再重新隐藏：还原+重隐会各自触发一次桌面级变更
		// （WM_SETTINGCHANGE 广播 / 工作区变化），Wallpaper Engine 等动态壁纸会瞬间黑屏。
		using (FileStream stream = File.OpenRead(RecoveryFile))
		{
			foreach (ShellState item in (List<ShellState>)new DataContractJsonSerializer(typeof(List<ShellState>)).ReadObject((Stream)stream))
			{
				if (item.AppBarState.HasValue)
				{
					if (keepTaskbarHidden)
					{
						Store.Trace("SHELL recover keep taskbar hidden -> skip appbar");
					}
					else
					{
						try
						{
							ApplyAppBarState(item.AppBarState.Value);
						}
						catch (Exception ex)
						{
							Store.Log(ex);
						}
					}
					break;
				}
			}
		}
		using (FileStream stream = File.OpenRead(RecoveryFile))
		{
			foreach (ShellState item in (List<ShellState>)new DataContractJsonSerializer(typeof(List<ShellState>)).ReadObject((Stream)stream))
			{
				if (item.HideIcons.HasValue)
				{
					if (keepIconsHidden)
					{
						Store.Trace("SHELL recover keep icons hidden -> skip reg restore");
					}
					else
					{
						WriteHideIcons(item.HideIcons.Value);
					}
					break;
				}
			}
		}
		using (FileStream stream2 = File.OpenRead(RecoveryFile))
		{
			foreach (ShellState item in (List<ShellState>)new DataContractJsonSerializer(typeof(List<ShellState>)).ReadObject((Stream)stream2))
			{
				if (!MatchesSaved(item))
				{
					continue;
				}
				bool iconWindow = item.ClassName == "SysListView32" || item.ClassName == "SHELLDLL_DefView";
				bool taskbarWindow = item.ClassName == "Shell_TrayWnd" || item.ClassName == "Shell_SecondaryTrayWnd";
				if ((iconWindow && keepIconsHidden) || (taskbarWindow && keepTaskbarHidden))
				{
					continue;
				}
				ShowWindow(new IntPtr(item.Handle), item.Visible ? 8 : 0);
			}
		}
		File.Delete(RecoveryFile);
	}

	public static void RestoreOriginal(bool keepIconsHidden = false)
	{
		maintenance.Stop();
		hideTaskbarTimer?.Stop();
		UninstallRevealHook();
		taskbarHidden = null;
		iconsHidden = null;
		RestoreAppBarState();
		if (originalHideIcons.HasValue)
		{
			if (keepIconsHidden)
			{
				// 用户设置「隐藏桌面图标」为持久偏好：退出时不还原图标，
				// 下次启动直接跳过隐藏操作（不广播 WM_SETTINGCHANGE），避免动态壁纸黑屏。
				Store.Trace("SHELL keep icons hidden on exit");
			}
			else
			{
				WriteHideIcons(originalHideIcons.Value);
			}
			originalHideIcons = null;
		}
		foreach (ShellState value in original.Values)
		{
			if (MatchesSaved(value))
			{
				ShowWindow(new IntPtr(value.Handle), value.Visible ? 8 : 0);
			}
		}
		original.Clear();
		if (File.Exists(RecoveryFile))
		{
			File.Delete(RecoveryFile);
		}
	}

	public static void ShowAll()
	{
		maintenance.Stop();
		hideTaskbarTimer?.Stop();
		UninstallRevealHook();
		taskbarHidden = false;
		iconsHidden = false;
		RestoreAppBarState();
		if (originalHideIcons.HasValue)
		{
			WriteHideIcons(originalHideIcons.Value);
			originalHideIcons = null;
		}
		IntPtr[] array = Taskbars().Concat(DesktopLists()).ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			ShowWindow(array[i], 8);
		}
		original.Clear();
		if (File.Exists(RecoveryFile))
		{
			File.Delete(RecoveryFile);
		}
	}
}
