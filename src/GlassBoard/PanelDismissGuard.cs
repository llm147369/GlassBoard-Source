using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
public static class PanelDismissGuard
{
	private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

	private static readonly LowLevelMouseProc Callback = OnMouse;

	private static readonly List<(Window win, Action dismiss)> Windows = new();

	private static IntPtr hook;

	private static DispatcherTimer heartbeat;

	public static void Track(Window win, Action dismiss)
	{
		// 确保句柄存在：窗口尚未 Show 时 GetWindowRect 无意义，EnsureHandle 先建立 HWND。
		new WindowInteropHelper(win).EnsureHandle();
		Windows.Add((win, dismiss));
		win.Closed += (object s, EventArgs e) =>
		{
			Windows.RemoveAll(p => ReferenceEquals(p.win, win));
			if (Windows.Count == 0 && hook != IntPtr.Zero)
			{
				UnhookWindowsHookEx(hook);
				hook = IntPtr.Zero;
			}
		};
		if (hook == IntPtr.Zero)
		{
			hook = SetWindowsHookEx(14, Callback, GetModuleHandle(null), 0u);
		}
		// 心跳重装：全局低级钩子回调若因 UI 线程繁忙超时，系统会静默摘除钩子且无通知
		// （LowLevelHooksTimeout）。定期重装保证摘除后自动恢复，面板收起永不永久失效。
		if (heartbeat == null)
		{
			heartbeat = new DispatcherTimer(DispatcherPriority.Background)
			{
				Interval = TimeSpan.FromSeconds(5.0)
			};
			heartbeat.Tick += delegate
			{
				if (Windows.Count == 0)
				{
					return;
				}
				if (hook != IntPtr.Zero)
				{
					UnhookWindowsHookEx(hook);
				}
				hook = SetWindowsHookEx(14, Callback, GetModuleHandle(null), 0u);
			};
			heartbeat.Start();
		}
	}

	private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
	{
		if (code >= 0 && (wParam == (IntPtr)513 || wParam == (IntPtr)516))
		{
			int x = Marshal.ReadInt32(lParam, 0);
			int y = Marshal.ReadInt32(lParam, 4);
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
				entry.win.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
				{
					// 面板持有键盘焦点时收起，焦点会被系统转移到壁纸层的 NOACTIVATE
					// 窗口链（视频壁纸/第三方壁纸），转移失败播放系统提示音。
					// 点击壁纸区域时前台已切走（GetForegroundWindow != 面板），但焦点
					// 仍在面板内——所以按"焦点是否在面板内"判定，与前台无关。
					// 收起前把焦点静默移到桌面图标列表（可接收焦点、不抢前台），杜绝提示音。
					try
					{
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
				}));
			}
		}
		return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
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

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int x;
		public int y;
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnhookWindowsHookEx(IntPtr hhk);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string lpModuleName);
}
