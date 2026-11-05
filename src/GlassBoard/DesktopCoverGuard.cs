using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlassBoard;

// 显示桌面（Win+D）守护：Windows 显示桌面时 explorer 把 Progman（桌面窗口）提升到
// Z 序的普通窗口层顶部（topmost 层之下），所有非置顶窗口被它盖住——dock（默认非置顶）
// 与桌面组件（默认非置顶）就是这样"消失"的；灵动岛因 Topmost=true 天然免疫。
// 状态机：
//   idle   → 任一注册窗口的 Z 序位于 Progman 之下（被桌面盖住）→ raised：全部临时置顶
//   raised → Progman 之下不再有可见的非本进程窗口（桌面已回底）→ idle：全部复位
// 150ms 轮询，开销仅数次 GetWindow。
public static class DesktopCoverGuard
{
	private static readonly List<(Window win, Action<bool> apply)> Windows = new();

	private static DispatcherTimer timer;

	private static bool raised;

	public static void Track(Window win, Action<bool> apply)
	{
		new WindowInteropHelper(win).EnsureHandle();
		Windows.Add((win, apply));
		win.Closed += (object s, EventArgs e) =>
		{
			Windows.RemoveAll(p => ReferenceEquals(p.win, win));
			if (Windows.Count == 0 && timer != null)
			{
				timer.Stop();
				timer = null;
			}
		};
		if (timer == null)
		{
			timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(150.0)
			};
			timer.Tick += delegate
			{
				if (!raised)
				{
					for (int i = 0; i < Windows.Count; i++)
					{
						Window win = Windows[i].win;
						if (win.IsVisible && CoveredByDesktop(new WindowInteropHelper(win).Handle))
						{
							raised = true;
							break;
						}
					}
				}
				else if (DesktopRelaxed())
				{
					raised = false;
				}
				if (raised)
				{
					for (int i = 0; i < Windows.Count; i++)
					{
						Windows[i].apply(true);
					}
				}
				else
				{
					for (int i = 0; i < Windows.Count; i++)
					{
						Windows[i].apply(false);
					}
				}
			};
			timer.Start();
		}
	}

	// 窗口是否位于 Progman 之下（向上 64 步内遇到 Progman = 被桌面窗口盖住）
	private static bool CoveredByDesktop(IntPtr hwnd)
	{
		IntPtr prog = FindWindow("Progman", null);
		if (prog == IntPtr.Zero || hwnd == IntPtr.Zero)
		{
			return false;
		}
		IntPtr w = hwnd;
		for (int i = 0; i < 64; i++)
		{
			w = GetWindow(w, 3u); // GW_HWNDPREV：朝 Z 序顶部方向
			if (w == IntPtr.Zero)
			{
				return false;
			}
			if (w == prog)
			{
				return true;
			}
		}
		return false;
	}

	// 桌面是否已回底：Progman 向下 64 步内不存在"可见的非本进程非系统窗口"。
	// 显示桌面期间被最小化的普通窗口仍 vis=1 且排在 Progman 之下，故能区分
	// "桌面抬升中"（false）与"已恢复/未抬升"（true）。
	private static bool DesktopRelaxed()
	{
		IntPtr prog = FindWindow("Progman", null);
		if (prog == IntPtr.Zero)
		{
			return true;
		}
		uint selfPid = GetCurrentProcessId();
		IntPtr w = prog;
		for (int i = 0; i < 64; i++)
		{
			w = GetWindow(w, 2u); // GW_HWNDNEXT：朝 Z 序底部方向
			if (w == IntPtr.Zero)
			{
				return true;
			}
			if (!IsWindowVisible(w))
			{
				continue;
			}
			if (IsSystemClass(w))
			{
				continue;
			}
			uint pid;
			GetWindowThreadProcessId(w, out pid);
			if (pid == selfPid)
			{
				continue;
			}
			return false;
		}
		return true;
	}

	private static bool IsSystemClass(IntPtr w)
	{
		StringBuilder cls = new StringBuilder(64);
		GetClassName(w, cls, 64);
		string c = cls.ToString();
		return c == "WorkerW" || c == "Progman" || c == "tooltips_class32" || c == "IME" || c == "MSCTFIME UI" || c == "CiceroUIWndFrame" || c == "ForegroundStaging" || c == "Windows.UI.Core.CoreWindow" || c == "Windows.UI.Composition.DesktopWindowContentBridge" || c == "TopLevelWindowForOverflowXamlIsland" || c == "Shell_TrayWnd" || c == "NotifyIconOverflowWindow" || c.StartsWith("WindowsForms10.") || c == "GDI+ Hook Window Class" || c.StartsWith(".NET-BroadcastEventWindow");
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindow(string cls, string title);

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindow(IntPtr h, uint cmd);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr h);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentProcessId();
}
