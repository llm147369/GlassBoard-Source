using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassBoard;

public static class DockBehaviorChecks
{
	public static void Run()
	{
		string text = RunningWindows.ProcessPath((uint)Process.GetCurrentProcess().Id);
		if (string.IsNullOrEmpty(text))
		{
			throw new Exception("窗口进程路径读取失败");
		}
		Window window = new Window
		{
			Width = 220.0,
			Height = 160.0,
			ShowInTaskbar = false
		};
		try
		{
			window.Show();
			IntPtr handle = new WindowInteropHelper(window).Handle;
			window.WindowState = WindowState.Minimized;
			if (!RunningWindows.ActivateHandle(handle))
			{
				throw new Exception("恢复已有窗口失败");
			}
		}
		finally
		{
			window.Close();
		}
		Controller controller = new Controller(desktop: false);
		try
		{
			for (int i = 0; i < 4; i++)
			{
				controller.Prefs.Apps.Add(new Entry
				{
					Name = "Dock" + i,
					Path = text + "-" + i,
					Pinned = true
				});
			}
			controller.Prefs.Normalize();
			controller.Dock.Refresh(capture: false);
			foreach (HotkeySetting hotkey in controller.Prefs.Hotkeys)
			{
				hotkey.Enabled = false;
			}
			controller.Dock.Show();
			controller.Dock.UpdateLayout();
			Entry entry = controller.Prefs.Apps[0];
			Entry entry2 = controller.Prefs.Apps[3];
			if (!DockOrderRules.Move(controller.Prefs, entry.Path, entry2.Path, before: false))
			{
				throw new Exception("Dock 排序失败");
			}
			controller.Dock.Refresh(capture: false);
			StackPanel stackPanel = FindPanel(controller.Dock);
			Button[] array = ((stackPanel == null) ? new Button[0] : (from b in stackPanel.Children.OfType<Button>()
				where b.Tag is Entry
				select b).ToArray());
			Entry[] source = DockOrderRules.Ordered(controller.Prefs).Take(array.Length).ToArray();
			if (array.Length < 1 || !array.Select((Button b) => ((Entry)b.Tag).Path).SequenceEqual(source.Select((Entry a) => a.Path)) || array.Any((Button b) => b.Tag == null))
			{
				throw new Exception("Dock 渲染顺序或排序入口错误");
			}
			controller.Dock.CheckPointerReorder();
			controller.Dock.CheckTaskDock();
			controller.Dock.CheckMixedPointerReorder();
		}
		finally
		{
			controller.ShuttingDown = true;
			controller.Dock.ReleaseHotkey();
			controller.Board.Close();
			controller.Dock.Close();
		}
	}

	private static StackPanel FindPanel(DependencyObject parent)
	{
		if (parent is StackPanel && ((StackPanel)parent).Orientation == Orientation.Horizontal && ((StackPanel)parent).Children.OfType<Button>().Any((Button b) => b.Tag is Entry))
		{
			return (StackPanel)parent;
		}
		for (int num = 0; num < VisualTreeHelper.GetChildrenCount(parent); num++)
		{
			StackPanel stackPanel = FindPanel(VisualTreeHelper.GetChild(parent, num));
			if (stackPanel != null)
			{
				return stackPanel;
			}
		}
		return null;
	}
}
