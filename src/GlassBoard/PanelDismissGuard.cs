using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBoard;

// 面板点击外部收起（鼠标驱动版）。
// 背景：大面板/控制中心/灵动岛展开态原本用 Deactivated（失焦）收起，但 Win+D 显示桌面
// 会抢走焦点触发失焦，面板跟着消失（灵动岛收起态因 WS_EX_NOACTIVATE 从不拿焦点而免疫）。
// 这里改成不依赖焦点机制：全局低级鼠标钩子监听按下事件，点击落在面板窗口矩形之外
// 且不在任何打开的 Popup（右键菜单/搜索下拉/预览浮层）内时，才触发各窗口的收起回调。
// Win+D 不产生鼠标点击，因此永远不会误收；点击面板内部/菜单/下拉框也都安全。
//
// 线程模型（重要）：WH_MOUSE_LL 钩子挂在专用后台消息泵线程（glassboard-mousehook），
// 绝不挂在 UI 线程——UI 线程消息泵被渲染时钟/DispatcherTimer 灌满时，LL 回调排队等待
// 会导致全系统鼠标移动延迟（"鼠标突然卡顿"），且回调超时触发 LowLevelHooksTimeout
// 静默摘钩 → 心跳重装 → 全局钩子链调整再次卡鼠标。钩子线程回调只做零 WPF 访问的最小
// 工作（存活打点 + BeginInvoke 投递），全部命中判定移入 UI 线程复核。
public static class PanelDismissGuard
{
	private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

	private static readonly LowLevelMouseProc Callback = OnMouse;

	private static readonly List<(Window win, Action dismiss)> Windows = new();

	// 弹出层（Popup，如运行列表/右键菜单的下拉浮层）单独追踪：点击浮层外任何位置（含 Dock 本体）都应收起，
	// 不享受 Window 的「点击本进程其它窗口不收起」豁免。
	private static readonly List<(System.Windows.Controls.Primitives.Popup popup, Action dismiss)> Popups = new();

	private static IntPtr hook;

	private static Thread hookThread;

	private static volatile bool hookThreadQuit;

	// 泵线程的 OS 线程 id（GetCurrentThreadId 在泵线程内采集），Shutdown 用 PostThreadMessage 唤醒。
	private static volatile int hookOsThreadId;

	// 会话结束（关机/注销）停机：唤醒阻塞在 GetMessage 的泵线程，让其摘钩退出。
	// 关机时系统拆全局钩子链，钩子若仍常驻回调会被系统强行切断（LowLevelHooksTimeout），
	// 主动摘钩干净得多。
	public static void Shutdown()
	{
		hookThreadQuit = true;
		try
		{
			int osId = hookOsThreadId;
			if (osId != 0)
			{
				PostThreadMessage((uint)osId, 0x0012u, UIntPtr.Zero, IntPtr.Zero);
			}
		}
		catch
		{
		}
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostThreadMessage(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	private static readonly object hookGate = new object();

	// 最近一次有鼠标事件经过本钩子的时刻：钩子活着时事件流不会断。
	private static volatile int lastMouseTick;

	private static void EnsureHook()
	{
		lock (hookGate)
		{
			if (hookThread != null && hookThread.IsAlive)
			{
				return;
			}
			hookThreadQuit = false;
			hookThread = new Thread(HookPump)
			{
				IsBackground = true,
				Name = "glassboard-mousehook"
			};
			hookThread.Start();
		}
	}

	// 钩子线程体：装钩子 → 消息泵循环（GetMessage）→ 线程内 WM_TIMER(10s) 心跳自愈。
	// 钩子在程序存活期间常驻（Windows/Popups 空时回调空转，成本可忽略），
	// 避免摘/挂 LL 钩子的瞬间系统调整全局钩子链让所有鼠标消息短暂排队。
	private static void HookPump()
	{
		try
		{
			hookOsThreadId = (int)GetCurrentThreadId();
			hook = SetWindowsHookEx(14, Callback, GetModuleHandle(null), 0u);
			if (hook == IntPtr.Zero)
			{
				Store.Trace("PDG hook install failed");
				return;
			}
			lastMouseTick = Environment.TickCount;
			Store.Trace("PDG hook thread started");
			IntPtr wakeTimer = SetTimer(IntPtr.Zero, (IntPtr)1, 10000u, IntPtr.Zero);
			MSG msg;
			while (!hookThreadQuit && !Program.Exiting && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
			{
				if (msg.message == 0x0113 && msg.wParam == (IntPtr)1)
				{
					// 心跳自愈：全局低级钩子回调若超时，系统会静默摘除钩子且无通知
					// （LowLevelHooksTimeout）。钩子活着时每 10 秒都会经过鼠标事件
					// （lastMouseTick 更新），此时绝不重装。只有 10 秒内无事件（疑似被
					// 摘除）才重装，保证自愈能力不变。
					if (Windows.Count == 0 && Popups.Count == 0)
					{
						continue;
					}
					if (Environment.TickCount - lastMouseTick < 10000)
					{
						continue; // 钩子活着：不重装
					}
					UnhookWindowsHookEx(hook);
					hook = SetWindowsHookEx(14, Callback, GetModuleHandle(null), 0u);
					lastMouseTick = Environment.TickCount;
					Store.Trace("PDG hook reinstalled");
					continue;
				}
				TranslateMessage(ref msg);
				DispatchMessage(ref msg);
			}
			KillTimer(IntPtr.Zero, wakeTimer);
		}
		catch (Exception e)
		{
			Store.Trace("PDG hook thread error: " + e.Message);
		}
		finally
		{
			hookOsThreadId = 0;
			if (hook != IntPtr.Zero)
			{
				UnhookWindowsHookEx(hook);
				hook = IntPtr.Zero;
			}
			hookThread = null;
			Store.Trace("PDG hook thread exited");
		}
	}

	public static void Track(Window win, Action dismiss)
	{
		// 确保句柄存在：窗口尚未 Show 时 GetWindowRect 无意义，EnsureHandle 先建立 HWND。
		new WindowInteropHelper(win).EnsureHandle();
		Windows.Add((win, dismiss));
		win.Closed += (object s, EventArgs e) =>
		{
			Windows.RemoveAll(p => ReferenceEquals(p.win, win));
		};
		EnsureHook();
	}

	// 追踪一个 Popup：点击浮层外任意位置时触发 dismiss（Popup 本身的位置由 InOpenPopup 判定）。
	public static void TrackPopup(System.Windows.Controls.Primitives.Popup popup, Action dismiss)
	{
		Popups.Add((popup, dismiss));
		popup.Closed += (object s, EventArgs e) =>
		{
			Popups.RemoveAll(p => ReferenceEquals(p.popup, popup));
		};
		EnsureHook();
	}

	private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
	{
		if (code >= 0)
		{
			// 任何经过本钩子的鼠标事件都证明钩子仍挂在全局链上（供 heartbeat 判断存活）。
			lastMouseTick = Environment.TickCount;
			if (wParam == (IntPtr)513 || wParam == (IntPtr)516)
			{
				// 钩子线程绝不访问 WPF 对象（线程亲和）——坐标捕获 + 投递，判定在 UI 线程复核。
				int x = Marshal.ReadInt32(lParam, 0);
				int y = Marshal.ReadInt32(lParam, 4);
				Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => DismissCheck(x, y)));
			}
		}
		return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
	}

	// UI 线程复核：全部 WPF 对象访问（IsVisible/IsOpen/视觉树）都在这里，钩子回调保持零亲和访问。
	private static void DismissCheck(int x, int y)
	{
		try
		{
			bool inPopup = InOpenPopup(x, y);
			bool inOwnWindow = HitIsOwnWindow(x, y);
			for (int i = 0; i < Windows.Count; i++)
			{
				(Window win, Action dismiss) entry = Windows[i];
				if (!entry.win.IsVisible)
				{
					continue;
				}
				Native.RECT wr;
				if (!Native.GetWindowRect(new WindowInteropHelper(entry.win).Handle, out wr))
				{
					continue;
				}
				if (x >= wr.Left && x < wr.Right && y >= wr.Top && y < wr.Bottom)
				{
					continue;
				}
				if (inPopup || inOwnWindow)
				{
					continue;
				}
				Action dismiss = entry.dismiss;
				try
				{
					// 面板持有键盘焦点时收起，焦点会被系统转移到壁纸层的 NOACTIVATE
					// 窗口链（视频壁纸/第三方壁纸），转移失败播放系统提示音。
					// 点击壁纸区域时前台已切走（GetForegroundWindow != 面板），但焦点
					// 仍在面板内——所以按"焦点是否在面板内"判定，与前台无关。
					// 收起前把焦点静默移到桌面图标列表（可接收焦点、不抢前台），杜绝提示音。
					IntPtr hwnd = new WindowInteropHelper(entry.win).Handle;
					if (hwnd != IntPtr.Zero && entry.win.IsKeyboardFocusWithin)
					{
						IntPtr prog = FindWindowNative("Progman", null);
						IntPtr def = (prog != IntPtr.Zero) ? FindWindowExNative(prog, IntPtr.Zero, "SHELLDLL_DefView", null) : IntPtr.Zero;
						IntPtr list = (def != IntPtr.Zero) ? FindWindowExNative(def, IntPtr.Zero, "SysListView32", null) : IntPtr.Zero;
						if (list != IntPtr.Zero)
						{
							SetFocusNative(list);
						}
					}
				}
				catch
				{
				}
				dismiss();
			}
			// 弹出层（运行列表等）：点击浮层外任何位置（含 Dock 本体、桌面、其它应用）都收起。
			// 判定用 inPopup（是否落在任意打开的 Popup 内）：落在本浮层或其右键菜单内不收起，其余一律收起。
			for (int j = 0; j < Popups.Count; j++)
			{
				(System.Windows.Controls.Primitives.Popup popup, Action dismiss) = Popups[j];
				if (!popup.IsOpen || inPopup)
				{
					continue;
				}
				try
				{
					if (popup.IsOpen)
					{
						dismiss();
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
	}

	private static bool HitIsOwnWindow(int x, int y)
	{
		try
		{
			IntPtr hit = WindowFromPointNative(new POINT { x = x, y = y });
			if (hit == IntPtr.Zero)
			{
				return false;
			}
			Native.GetWindowThreadProcessId(hit, out uint pid);
			return pid == System.Diagnostics.Process.GetCurrentProcess().Id;
		}
		catch
		{
			return false;
		}
	}

	private static bool InOpenPopup(int x, int y)
	{
		try
		{
			foreach (PresentationSource source in PresentationSource.CurrentSources)
			{
				if (source == null || source.RootVisual is not DependencyObject root)
				{
					continue;
				}
				// 打开的 Popup 内容挂在其专属 HwndSource（RootVisual 类名 PopupRoot），
				// 关闭后该 RootVisual 清空。直接对 Child 做矩形判定即可覆盖
				// 右键菜单/ComboBox 下拉/搜索浮层等所有弹出层。
				if (root.GetType().Name != "PopupRoot")
				{
					continue;
				}
				int count = VisualTreeHelper.GetChildrenCount(root);
				for (int i = 0; i < count; i++)
				{
					if (VisualTreeHelper.GetChild(root, i) is FrameworkElement fe && fe.IsVisible && fe.ActualWidth > 0.0 && fe.ActualHeight > 0.0)
					{
						Point tl = fe.PointToScreen(new Point(0.0, 0.0));
						Point br = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
						if (new Rect(tl, br).Contains(x, y))
						{
							return true;
						}
					}
				}
			}
		}
		catch
		{
			// 判定失败一律视为"在弹出层内"——宁可面板不收，也不误收。
			return true;
		}
		return false;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

	[DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
	private static extern IntPtr WindowFromPointNative(POINT pt);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
	private static extern IntPtr FindWindowNative(string cls, string title);

	[DllImport("user32.dll", EntryPoint = "FindWindowExW")]
	private static extern IntPtr FindWindowExNative(IntPtr parent, IntPtr after, string cls, string title);

	[DllImport("user32.dll", EntryPoint = "SetFocus")]
	private static extern IntPtr SetFocusNative(IntPtr h);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnhookWindowsHookEx(IntPtr hhk);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string lpModuleName);

	[DllImport("user32.dll")]
	private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool TranslateMessage(ref MSG lpMsg);

	[DllImport("user32.dll")]
	private static extern IntPtr DispatchMessage(ref MSG lpmsg);

	[DllImport("user32.dll")]
	private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int x;
		public int y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MSG
	{
		public IntPtr hwnd;
		public int message;
		public IntPtr wParam;
		public IntPtr lParam;
		public int time;
		public POINT pt;
	}
}
