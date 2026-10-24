using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace GlassBoard;

// 菜单自动消失：Dock 是 ShowActivated=false + Topmost + AllowsTransparency 的窗口，
// WPF 对"点击菜单外自动关闭"的判定在这些窗口上会失效（点击桌面/其他应用时收不到输入事件）。
// 用全局低级鼠标钩子兜底：菜单打开期间钩住鼠标按下，点击落在菜单与已打开子菜单的屏幕
// 矩形之外时关闭菜单，矩形之内则放行（保证菜单项/子菜单点击正常）。
public static class MenuAutoDismiss
{
	private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

	private static readonly LowLevelMouseProc Callback = OnMouse;

	private static IntPtr hook;

	private static ContextMenu openMenu;

	private static int openedTick;

	public static void Track(ContextMenu menu)
	{
		menu.Opened += (object s, RoutedEventArgs e) =>
		{
			openMenu = menu;
			openedTick = Environment.TickCount;
			if (hook == IntPtr.Zero)
			{
				hook = SetWindowsHookEx(14, Callback, GetModuleHandle(null), 0u);
			}
		};
		menu.Closed += (object s, RoutedEventArgs e) =>
		{
			if (openMenu == menu)
			{
				openMenu = null;
			}
			if (openMenu == null && hook != IntPtr.Zero)
			{
				UnhookWindowsHookEx(hook);
				hook = IntPtr.Zero;
			}
		};
	}

	private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
	{
		if (code >= 0 && (wParam == (IntPtr)513 || wParam == (IntPtr)516 || wParam == (IntPtr)519) && openMenu != null)
		{
			// 菜单打开后 400ms 宽限期：菜单 Popup 刚 Show 完布局尚未完成，此刻判定
			// 矩形可能为 0，会把"点菜单项"误判为"点菜单外"提前关菜单吞掉点击。
			if (unchecked((uint)(Environment.TickCount - openedTick)) < 400u)
			{
				return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
			}
			// MSLLHOOKSTRUCT: POINT pt 在偏移 0/4（x/y 各 4 字节）
			int x = Marshal.ReadInt32(lParam, 0);
			int y = Marshal.ReadInt32(lParam, 4);
			ContextMenu menu = openMenu;
			menu?.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() =>
			{
				if (openMenu == menu && menu != null && menu.IsOpen && !InMenuBounds(menu, x, y))
				{
					menu.IsOpen = false;
				}
			}));
		}
		return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
	}

	private static bool InMenuBounds(ContextMenu menu, int x, int y)
	{
		try
		{
			if (menu.IsOpen)
			{
				// 布局尚未完成时无法判定：宁可放行，绝不在此时关菜单（吞点击的根源）。
				if (menu.ActualWidth <= 0.0 || menu.ActualHeight <= 0.0)
				{
					return true;
				}
				// PointToScreen 返回物理像素，而 ActualWidth/Height 是 DIP——高 DPI 下直接把
				// DIP 尺寸当物理像素会让判定矩形缩水（125% 时只有实际的 80%），点击菜单
				// 右下区域被误判"点菜单外"提前关菜单、吞掉点击（"移除没反应"根因）。
				// 用两个 PointToScreen 对角点构造矩形，让 WPF 自己完成 DIP→物理换算。
				Point topLeft = menu.PointToScreen(new Point(0.0, 0.0));
				Point bottomRight = menu.PointToScreen(new Point(menu.ActualWidth, menu.ActualHeight));
				if (new Rect(topLeft, bottomRight).Contains(x, y))
				{
					return true;
				}
			}
			foreach (Rect item in SubmenuRects(menu))
			{
				if (item.Contains(x, y))
				{
					return true;
				}
			}
		}
		catch
		{
			// 任何判定异常都放行（不关菜单）。
			return true;
		}
		return false;
	}

	private static IEnumerable<Rect> SubmenuRects(ItemsControl itemsControl)
	{
		foreach (object obj in itemsControl.Items)
		{
			if (obj is MenuItem menuItem && menuItem.IsSubmenuOpen)
			{
				foreach (Rect item in SubmenuRects(menuItem))
				{
					yield return item;
				}
			}
		}
	}

	private static IEnumerable<Rect> SubmenuRects(MenuItem item)
	{
		if (item.Template != null && item.Template.FindName("PART_Popup", item) is Popup popup && popup.IsOpen && popup.Child != null)
		{
			UIElement child = popup.Child;
			Point childTopLeft = child.PointToScreen(new Point(0.0, 0.0));
			Point childBottomRight = child.PointToScreen(new Point(child.RenderSize.Width, child.RenderSize.Height));
			yield return new Rect(childTopLeft, childBottomRight);
		}
		foreach (Rect rect in SubmenuRects((ItemsControl)item))
		{
			yield return rect;
		}
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
