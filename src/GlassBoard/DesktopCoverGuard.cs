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
//   raised → 所有注册窗口连续 3 tick 不再被桌面盖住（或桌面确认回底）→ idle：全部复位
// 150ms 轮询，开销仅数次 GetWindow。
public static class DesktopCoverGuard
{
	private static readonly List<(Window win, Action<bool> apply)> Windows = new();

	private static DispatcherTimer timer;

	private static bool raised;

	// raised 复位的连续稳定 tick 数：置顶解除后窗口可能立刻又被桌面盖住（振荡），
	// 需要连续几拍都确认"不再被盖"才真正复位。
	private static int relaxTicks;

	// 显示桌面期间用户点了 dock 图标：随后 60 拍（约 9 秒）内若冒出新的顶层窗口，
	// 就把它还原并拉到前台。绝不调用 ToggleDesktop —— 那等于再按一次 Win+D，
	// 会把 Win+D 之前的所有窗口一并恢复，用户看到的正是"点一个图标全屏窗口全回来了"。
	private static int watchTicks;

	private static readonly HashSet<IntPtr> knownWindows = new();

	public static void Track(Window win, Action<bool> apply)
	{
		// 注意：这里绝不 EnsureHandle——强制创建窗口句柄会把 SourceInitialized
		// 提前到调用方构造器内部触发，导致构造器后半段挂上的 SourceInitialized
		// 订阅（如 DockWindow 的全局热键注册）永远错过事件。
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
				// 桌面是否还在台面上。判据与"自己是否置顶"无关：只看普通窗口层（非
				// WS_EX_TOPMOST）自顶向下第一个可见窗口是不是桌面壳层。置顶后窗口不再
				// 属于"被盖"状态，若只看被盖状态会 450ms 一周期反复置顶/取消。
				bool peeked = DesktopPeeked();
				bool anyCovered = false;
				for (int i = 0; i < Windows.Count; i++)
				{
					Window win = Windows[i].win;
					if (win.IsVisible && CoveredByDesktop(new WindowInteropHelper(win).Handle))
					{
						anyCovered = true;
						break;
					}
				}
				if (!peeked)
				{
					// 桌面已回底：本轮没有"新窗口需要拉上来"的场景了。
					watchTicks = 0;
					if (knownWindows.Count > 0)
					{
						knownWindows.Clear();
					}
				}
				if (watchTicks > 0)
				{
					TickActivation();
				}
				bool wasRaised = raised;
				if (!raised)
				{
					if (peeked || anyCovered)
					{
						raised = true;
						relaxTicks = 0;
						Store.Trace("COVERGUARD raised=true");
					}
				}
				else if (peeked || anyCovered)
				{
					relaxTicks = 0;
				}
				else
				{
					relaxTicks++;
					if (relaxTicks >= 3)
					{
						raised = false;
						relaxTicks = 0;
						Store.Trace("COVERGUARD raised=false");
					}
				}
				if (raised)
				{
					for (int i = 0; i < Windows.Count; i++)
					{
						Window w2 = Windows[i].win;
						// 个别环境显示桌面会把窗口最小化：先还原再置顶，否则窗口仍然缺席。
						if (w2.IsVisible && w2.WindowState == WindowState.Minimized)
						{
							w2.WindowState = WindowState.Normal;
						}
						Windows[i].apply(true);
					}
				}
				else
				{
					for (int i = 0; i < Windows.Count; i++)
					{
						Windows[i].apply(false);
						if (wasRaised && string.Equals(Windows[i].win.Title, "GlassBoard Dock", StringComparison.Ordinal))
						{
							// WPF 的 Topmost=false 只走 SetWindowPos(HWND_NOTOPMOST)：窗口被
							// 放到"所有非置顶窗口之上"，dock 就会浮在刚打开的大窗口上方，
							// 直到下一个窗口激活才被压下去。这里显式沉到普通层底部。
							// 只对 dock 做：组件/灵动岛不应被强制压到所有窗口之下。
							SinkBelowNormal(new WindowInteropHelper(Windows[i].win).Handle);
						}
					}
				}
			};
			timer.Start();
		}
	}

	// 窗口是否位于 Progman 之下（向上 64 步内遇到 Progman = 被桌面窗口盖住）
	private static bool CoveredByDesktop(IntPtr hwnd)
	{
		if (hwnd == IntPtr.Zero)
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
			if (IsDesktopLayer(w))
			{
				return true;
			}
		}
		return false;
	}

	// 用户按下 dock 图标（WM_MOUSEACTIVATE 时机）：记下当前顶层窗口快照，并开启
	// 短时观察窗。之后 9 秒内若有新的顶层窗口出现（从 Dock 启动的应用/启动台里的
	// 应用），就把它还原并拉到前台。
	// 曾经的实现是 ToggleDesktop（再按一次 Win+D）——那是错的：Win+D 把当时所有
	// 窗口最小化了，再切换一次会把它们全部恢复，用户看到"点一个图标，Win+D 之前
	// 停着的窗口全都回来了"。单窗口前台化同时也让 explorer 结束显示桌面。
	public static void NotifyPointerActivation()
	{
		if (!raised)
		{
			return;
		}
		knownWindows.Clear();
		IntPtr w = GetTopWindow(IntPtr.Zero);
		for (int i = 0; i < 600 && w != IntPtr.Zero; i++)
		{
			knownWindows.Add(w);
			w = GetWindow(w, 2u);
		}
		watchTicks = 60;
		Store.Trace("COVERGUARD pointer-activation -> watch new window count=" + knownWindows.Count);
	}

	// 观察窗内的每一拍：按 Z 序自顶向下找第一个"点击前不存在、可见、非置顶、非桌面壳层、
	// 非本进程"的顶层窗口，用任务栏同款的还原+前台链把它拉上来。
	private static void TickActivation()
	{
		watchTicks--;
		uint selfPid = GetCurrentProcessId();
		IntPtr w = GetTopWindow(IntPtr.Zero);
		for (int i = 0; i < 600 && w != IntPtr.Zero; i++)
		{
			IntPtr cur = w;
			w = GetWindow(w, 2u);
			if (knownWindows.Contains(cur) || !IsWindowVisible(cur))
			{
				continue;
			}
			if ((GetWindowLong(cur, -20) & 8) != 0)
			{
				continue;
			}
			StringBuilder cls = new StringBuilder(64);
			GetClassName(cur, cls, 64);
			string c = cls.ToString();
			if (c == "Progman" || c == "WorkerW" || c == "SHELLDLL_DefView" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "Windows.UI.Core.CoreWindow" || c.StartsWith("Windows.UI.Composition"))
			{
				continue;
			}
			uint pid;
			GetWindowThreadProcessId(cur, out pid);
			if (pid == selfPid)
			{
				continue;
			}
			RunningWindows.ActivateHandle(cur);
			Store.Trace("COVERGUARD watch -> foreground new window hwnd=" + cur.ToInt64());
			watchTicks = 0;
			return;
		}
	}

	// 显示桌面（Win+D）是否把桌面壳层抬到了普通窗口层顶部。
	// Win10 上被抬升的是 WorkerW（壁纸/图标宿主），Progman 反而在下面，所以不能只认
	// Progman；也不能只看"自己是否被盖"（置顶后立刻不成立）。只看普通层顶部窗口类名，
	// 因此本进程窗口置顶与否都不影响这个判据。
	private static bool DesktopPeeked()
	{
		IntPtr w = GetTopWindow(IntPtr.Zero);
		for (int i = 0; i < 600 && w != IntPtr.Zero; i++)
		{
			if (IsWindowVisible(w) && (GetWindowLong(w, -20) & 8) == 0)
			{
				StringBuilder cls = new StringBuilder(64);
				GetClassName(w, cls, 64);
				string c = cls.ToString();
				return c == "Progman" || c == "WorkerW" || c == "SHELLDLL_DefView";
			}
			w = GetWindow(w, 2u); // GW_HWNDNEXT
		}
		return false;
	}

	// 把窗口沉到普通层底部（仍高于桌面壳层与任务栏），避免从置顶带降级后浮在
	// 其它窗口之上。SetWindowPos 的 hWndInsertAfter 语义是"插在该窗口之后（下方）"。
	private static void SinkBelowNormal(IntPtr hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		uint selfPid = GetCurrentProcessId();
		IntPtr bottom = IntPtr.Zero;
		IntPtr w = GetTopWindow(IntPtr.Zero);
		for (int i = 0; i < 600 && w != IntPtr.Zero; i++)
		{
			if (w != hwnd && IsWindowVisible(w) && (GetWindowLong(w, -20) & 8) == 0)
			{
				StringBuilder cls = new StringBuilder(64);
				GetClassName(w, cls, 64);
				string c = cls.ToString();
				if (c != "Progman" && c != "WorkerW" && c != "SHELLDLL_DefView" && c != "Shell_TrayWnd" && c != "Shell_SecondaryTrayWnd")
				{
					uint pid;
					GetWindowThreadProcessId(w, out pid);
					if (pid != selfPid)
					{
						bottom = w;
					}
				}
			}
			w = GetWindow(w, 2u);
		}
		if (bottom != IntPtr.Zero)
		{
			SetWindowPos(hwnd, bottom, 0, 0, 0, 0, 0x0001u | 0x0002u | 0x0010u);
		}
	}

	// 桌面壳层窗口：Progman（程序管理器），以及「显示桌面」时被 explorer 抬升到
	// 普通窗口层顶部的 WorkerW / SHELLDLL_DefView（壁纸与图标宿主）。
	// 未显示的 WorkerW 是各应用自建的空壳窗口，必须排除，否则正常使用时会误判。
	private static bool IsDesktopLayer(IntPtr w)
	{
		if (!IsWindowVisible(w))
		{
			return false;
		}
		StringBuilder cls = new StringBuilder(64);
		GetClassName(w, cls, 64);
		string c = cls.ToString();
		return c == "Progman" || c == "WorkerW" || c == "SHELLDLL_DefView";
	}

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

	[DllImport("user32.dll")]
	private static extern IntPtr GetTopWindow(IntPtr h);

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(IntPtr h, int index);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
