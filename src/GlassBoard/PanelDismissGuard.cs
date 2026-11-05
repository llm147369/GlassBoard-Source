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
	}

	private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
	{
		if (code >= 0 && (wParam == (IntPtr)513 || wParam == (IntPtr)516))
		{
			int x = Marshal.ReadInt32(lParam, 0);
			int y = Marshal.ReadInt32(lParam, 4);
			bool inPopup = InOpenPopup(x, y);
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
				if (inPopup)
				{
					continue;
				}
				Action dismiss = entry.dismiss;
				entry.win.Dispatcher.BeginInvoke(DispatcherPriority.Input, dismiss);
			}
		}
		return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
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

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnhookWindowsHookEx(IntPtr hhk);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string lpModuleName);
}
