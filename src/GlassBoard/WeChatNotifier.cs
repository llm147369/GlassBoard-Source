using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Interop;

namespace GlassBoard
{
	// 微信桌面版（Weixin 4.x，Qt）专用通知检测。
	//
	// 原理：微信不在前台时，新消息会弹一个短暂的通知气泡（屏幕右上角、无标题栏的
	// 小窗，几秒后自动消失）。这里用 WinEvent Hook 事件驱动地观察该类窗口的
	// 显示/隐藏与微信主窗口的前台切换，不轮询窗口、不注入进程、不读窗口文字。
	//
	// 备用信号：注册 Shell Hook 窗口，观察 HSHELL_FLASH（任务栏闪烁）——微信在后台
	// 收到消息若触发任务栏闪烁，也能点亮红点。两条路径互补。
	//
	// 点亮：检测到气泡出现 → Dock 微信入口亮红点 + 一次轻弹跳（2 秒防抖）。
	// 熄灭：气泡被点击（气泡消失后微信进入前台）或用户在微信主窗口持续停留 1.5 秒，
	// 或微信进程退出。气泡超时自动消失（用户没理）不熄灭。
	//
	// 诊断：全链路分阶段日志（hook 初始化/原始事件计数/窗口-进程关联/特征识别/
	// 状态判定/UI 交付），事件节流防刷屏。日志只记窗口类名/位置/尺寸与状态，
	// 不记标题或聊天内容。
	public static class WeChatNotifier
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct RECT { public int L; public int T; public int R; public int B; }

		private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

		[DllImport("user32.dll")]
		private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate cb, uint pid, uint tid, uint flags);
		[DllImport("user32.dll")]
		private static extern bool UnhookWinEvent(IntPtr hook);
		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
		[DllImport("user32.dll")]
		private static extern int GetClassName(IntPtr hwnd, StringBuilder s, int n);
		[DllImport("user32.dll")]
		private static extern bool IsWindowVisible(IntPtr hwnd);
		[DllImport("user32.dll")]
		private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
		[DllImport("user32.dll")]
		private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
		[DllImport("user32.dll")]
		private static extern int GetWindowLong(IntPtr hwnd, int index);
		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();
		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr hwnd);
		[DllImport("user32.dll")]
		private static extern bool SystemParametersInfo(uint action, uint param, ref RECT v, uint winIni);
		[DllImport("user32.dll")]
		private static extern bool RegisterShellHookWindow(IntPtr hwnd);
		[DllImport("user32.dll")]
		private static extern bool DeregisterShellHookWindow(IntPtr hwnd);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern uint RegisterWindowMessage(string name);

		private const uint EVENT_OBJECT_CREATE = 0x8000;
		private const uint EVENT_OBJECT_DESTROY = 0x8001;
		private const uint EVENT_OBJECT_SHOW = 0x8002;
		private const uint EVENT_OBJECT_HIDE = 0x8003;
		private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
		private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
		private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
		private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
		private const uint OBJID_WINDOW = 0;
		private const uint GW_OWNER = 4;
		private const int GWL_STYLE = -16;
		private const long WS_CHILD = 0x40000000;
		private const long WS_CAPTION = 0x00C00000;
		private const uint SPI_GETWORKAREA = 0x0030;
		private const uint HSHELL_FLASH = 0x8006;

		private static WinEventDelegate showHideCallback;
		private static WinEventDelegate foregroundCallback;
		private static WinEventDelegate locationCallback;
		private static IntPtr showHideHook;
		private static IntPtr foregroundHook;
		private static IntPtr locationHook;
		private static Controller controller;

		private static readonly object gate = new object();
		private static HashSet<uint> weixinPids = new HashSet<uint>();
		private static int lastPidRefresh;
		private static IntPtr activePopup;
		private static long popupShownTick;
		private static long lastPulseTick;
		private static long foregroundSince;
		private static bool badgeLitByUs;
		private static string weChatPath = "";
		private static long weChatPathTick;
		private static RECT workArea;
		private static int workAreaTick;
		private static Timer idleTimer;

		// —— 诊断计数器（阶段 2/3 用）——
		private static long rawShowHideCount;
		private static long rawFgCount;
		private static long rawLocCount;
		private static long weixinEventCount;
		private static long shellFlashCount;
		private static int rawSampleSeq;
		private static int lastHeartbeatTick;
		private static Dictionary<IntPtr, int> diagTick = new Dictionary<IntPtr, int>();

		// Shell Hook（HSHELL_FLASH 备用路径）
		private static HwndSource shellSource;
		private static uint shellHookMessage;
		private static bool shellHookRegistered;

		public static void Start(Controller c)
		{
			controller = c;
			Store.Trace("WEIXIN start");
			try
			{
				SystemParametersInfo(SPI_GETWORKAREA, 0, ref workArea, 0);
			}
			catch
			{
			}
			try
			{
				showHideCallback = OnShowHideEvent;
				foregroundCallback = OnForegroundEvent;
				locationCallback = OnLocationEvent;
				showHideHook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero,
					showHideCallback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
				foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
					foregroundCallback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
				locationHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
					locationCallback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
				Store.Trace("WEIXIN hook show range=0x8000-0x8002 ret=" + (showHideHook != IntPtr.Zero)
					+ " fg range=0x0003 ret=" + (foregroundHook != IntPtr.Zero)
					+ " loc range=0x800B ret=" + (locationHook != IntPtr.Zero)
					+ " pid=0 tid=0 thread=" + Environment.CurrentManagedThreadId);
			}
			catch (Exception ex)
			{
				Store.Trace("WEIXIN hook failed " + ex.Message);
			}
			InstallShellHook();
			RefreshPids(true);
			// 低频看门狗：仅在“红点由本模块点亮或气泡存在”时每 10 秒检查一次，
			// 处理微信退出/重启后的清理与重建；无活动时零开销
			idleTimer = new Timer(OnIdleTick, null, 10000, 10000);
		}

		private static void InstallShellHook()
		{
			try
			{
				shellHookMessage = RegisterWindowMessage("SHELLHOOK");
				shellSource = new HwndSource(new HwndSourceParameters("GlassBoardShellHook")
				{
					Width = 0,
					Height = 0,
					WindowStyle = 0,
					ExtendedWindowStyle = 0x80 // WS_EX_TOOLWINDOW：不占任务栏
				});
				shellSource.AddHook(ShellWndProc);
				shellHookRegistered = RegisterShellHookWindow(shellSource.Handle);
				Store.Trace("WEIXIN shellhook msg=" + shellHookMessage + " registered=" + shellHookRegistered);
			}
			catch (Exception ex)
			{
				Store.Trace("WEIXIN shellhook failed " + ex.Message);
			}
		}

		private static IntPtr ShellWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			if (shellHookMessage != 0 && (uint)msg == shellHookMessage)
			{
				uint kind = unchecked((uint)wParam.ToInt64());
				if (kind == HSHELL_FLASH)
				{
					try
					{
						IntPtr flashHwnd = lParam;
						uint pid;
						GetWindowThreadProcessId(flashHwnd, out pid);
						StringBuilder cls = new StringBuilder(128);
						GetClassName(flashHwnd, cls, 128);
						lock (gate)
						{
							shellFlashCount++;
						}
						if (Throttle(flashHwnd, 3000))
						{
							Store.Trace("WEIXIN shellflash hwnd=" + flashHwnd + " pid=" + pid + " class=" + cls + " weixin=" + IsWeixinPid(pid));
						}
						if (pid != 0 && IsWeixinPid(pid))
						{
							Store.Trace("WEIXIN shellflash → badge on (pid=" + pid + ")");
							DispatchBadge(true, true);
						}
					}
					catch (Exception ex)
					{
						Store.Trace("WEIXIN shellflash error " + ex.Message);
					}
				}
			}
			return IntPtr.Zero;
		}

		private static bool Throttle(IntPtr key, int windowMs)
		{
			int now = Environment.TickCount;
			lock (gate)
			{
				int last;
				if (diagTick.TryGetValue(key, out last) && now - last < windowMs)
				{
					return false;
				}
				if (diagTick.Count > 400)
				{
					diagTick.Clear();
				}
				diagTick[key] = now;
				return true;
			}
		}

		private static void OnIdleTick(object state)
		{
			try
			{
				bool lit;
				IntPtr popup;
				lock (gate)
				{
					lit = badgeLitByUs;
					popup = activePopup;
				}
				if (!lit && popup == IntPtr.Zero)
				{
					return;
				}
				RefreshPids(false);
				lock (gate)
				{
					if (popup != IntPtr.Zero)
					{
						RECT r;
						if (!IsWindow(popup) || !IsWindowVisible(popup) || !GetWindowRect(popup, out r)
							|| PopupRejectReason(popup, r).Length > 0)
						{
							// 气泡窗口已消失、隐藏或移出气泡位置（复用窗口路径）但没有收到
							// HIDE 事件：按超时处理
							activePopup = IntPtr.Zero;
						}
					}
					if (badgeLitByUs && weixinPids.Count == 0)
					{
						badgeLitByUs = false;
						activePopup = IntPtr.Zero;
						Store.Trace("WEIXIN process gone → badge off");
						DispatchBadge(false, false);
					}
				}
			}
			catch (Exception ex)
			{
				Store.Trace("WEIXIN idle error " + ex.Message);
			}
		}

		private static void RefreshPids(bool force)
		{
			int now = Environment.TickCount;
			lock (gate)
			{
				if (!force && now - lastPidRefresh < 30000)
				{
					return;
				}
				lastPidRefresh = now;
			}
			HashSet<uint> found = new HashSet<uint>();
			try
			{
				foreach (Process p in Process.GetProcessesByName("Weixin"))
				{
					found.Add((uint)p.Id);
				}
				foreach (Process p in Process.GetProcessesByName("WeChat"))
				{
					found.Add((uint)p.Id);
				}
			}
			catch
			{
			}
			bool changed;
			lock (gate)
			{
				changed = !weixinPids.SetEquals(found);
				weixinPids = found;
			}
			if (changed)
			{
				Store.Trace("WEIXIN pids refreshed n=" + found.Count);
			}
		}

		private static bool IsWeixinPid(uint pid)
		{
			lock (gate)
			{
				if (weixinPids.Contains(pid))
				{
					return true;
				}
			}
			// 不在集合里：可能刚启动，节流刷新一次再判
			RefreshPids(false);
			lock (gate)
			{
				return weixinPids.Contains(pid);
			}
		}

		private static void CountRaw(int kind)
		{
			// kind: 0=showhide 1=fg 2=loc
			lock (gate)
			{
				if (kind == 0)
				{
					rawShowHideCount++;
				}
				else if (kind == 1)
				{
					rawFgCount++;
				}
				else
				{
					rawLocCount++;
				}
				int now = Environment.TickCount;
				if (now - lastHeartbeatTick > 60000)
				{
					lastHeartbeatTick = now;
					Store.Trace("WEIXIN raw totals showHide=" + rawShowHideCount + " fg=" + rawFgCount
						+ " loc=" + rawLocCount + " weixinEvts=" + weixinEventCount + " flashes=" + shellFlashCount);
				}
			}
		}

		private static void SampleRaw(uint evt, IntPtr hwnd, int idObject, uint pid, string cls)
		{
			// 阶段 2：原始事件抽样（前 20 条 + 每 250 条 1 条），证明回调在跑
			int seq;
			lock (gate)
			{
				rawSampleSeq++;
				seq = rawSampleSeq;
			}
			if (seq <= 20 || seq % 250 == 0)
			{
				Store.Trace("WEIXIN raw sample #" + seq + " evt=0x" + evt.ToString("X") + " hwnd=" + hwnd
					+ " idObject=" + idObject + " pid=" + pid + " class=" + cls);
			}
		}

		private static void OnShowHideEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
		{
			try
			{
				if (hwnd == IntPtr.Zero)
				{
					return;
				}
				uint pid;
				GetWindowThreadProcessId(hwnd, out pid);
				CountRaw(0);
				StringBuilder cls = new StringBuilder(128);
				GetClassName(hwnd, cls, 128);
				SampleRaw(evt, hwnd, idObject, pid, cls.ToString());
				if (idObject != OBJID_WINDOW || pid == 0 || !IsWeixinPid(pid))
				{
					return;
				}
				lock (gate)
				{
					weixinEventCount++;
				}
				if (evt == EVENT_OBJECT_SHOW)
				{
					OnWeixinShow(hwnd, pid);
				}
				else if (evt == EVENT_OBJECT_HIDE || evt == EVENT_OBJECT_DESTROY)
				{
					OnWeixinHide(hwnd);
				}
			}
			catch (Exception ex)
			{
				Store.Trace("WEIXIN event error " + ex.Message);
			}
		}

		private static void OnLocationEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
		{
			try
			{
				if (hwnd == IntPtr.Zero)
				{
					return;
				}
				uint pid;
				GetWindowThreadProcessId(hwnd, out pid);
				CountRaw(2);
				if (idObject != OBJID_WINDOW || pid == 0 || !IsWeixinPid(pid))
				{
					return;
				}
				IntPtr popup;
				lock (gate)
				{
					popup = activePopup;
				}
				RECT r;
				if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out r))
				{
					return;
				}
				string reason = PopupRejectReason(hwnd, r);
				if (reason.Length == 0)
				{
					if (popup != IntPtr.Zero && popup == hwnd)
					{
						return; // 已是活动气泡，位置更新不重复触发
					}
					// 复用窗口路径：没有 SHOW 事件、窗口移动到气泡位置
					StringBuilder cls = new StringBuilder(128);
					GetClassName(hwnd, cls, 128);
					Store.Trace("WEIXIN popup moved-in class=" + cls + " rect=" + r.L + "," + r.T + "," + r.R + "," + r.B);
					lock (gate)
					{
						activePopup = hwnd;
						popupShownTick = Environment.TickCount;
					}
					DispatchBadge(true, true);
					return;
				}
				if (popup != IntPtr.Zero && popup == hwnd)
				{
					// 活动气泡移出气泡位置（复用窗口收起）：按隐藏处理
					lock (gate)
					{
						activePopup = IntPtr.Zero;
					}
					Store.Trace("WEIXIN popup moved-out reason=" + reason);
				}
			}
			catch (Exception ex)
			{
				Store.Trace("WEIXIN loc error " + ex.Message);
			}
		}

		private static void OnForegroundEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
		{
			try
			{
				if (hwnd == IntPtr.Zero)
				{
					return;
				}
				uint pid;
				GetWindowThreadProcessId(hwnd, out pid);
				CountRaw(1);
				StringBuilder cls = new StringBuilder(128);
				GetClassName(hwnd, cls, 128);
				SampleRaw(evt, hwnd, idObject, pid, cls.ToString());
				if (pid == 0 || !IsWeixinPid(pid))
				{
					return;
				}
				lock (gate)
				{
					weixinEventCount++;
				}
				IntPtr popup;
				lock (gate)
				{
					popup = activePopup;
				}
				if (popup != IntPtr.Zero && hwnd == popup)
				{
					// 气泡自身获得前台：等待后续主窗口切换
					return;
				}
				// 微信主窗口进入前台：持续停留 1.5 秒视为“已处理”，熄灭红点；
				// 瞬时激活（气泡点击路过、闪烁等）不会误灭
				lock (gate)
				{
					foregroundSince = Environment.TickCount;
				}
				ThreadPool.QueueUserWorkItem(_ =>
				{
					Thread.Sleep(1500);
					IntPtr fg = GetForegroundWindow();
					uint fgPid;
					GetWindowThreadProcessId(fg, out fgPid);
					lock (gate)
					{
						if (!badgeLitByUs || activePopup != IntPtr.Zero || !weixinPids.Contains(fgPid))
						{
							return;
						}
						badgeLitByUs = false;
					}
					Store.Trace("WEIXIN foreground 1.5s → badge off");
					DispatchBadge(false, false);
				});
			}
			catch (Exception ex)
			{
				Store.Trace("WEIXIN fg error " + ex.Message);
			}
		}

		private static void OnWeixinShow(IntPtr hwnd, uint pid)
		{
			RECT r;
			if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out r))
			{
				return;
			}
			int width = r.R - r.L;
			int height = r.B - r.T;
			if (width <= 0 || height <= 0)
			{
				return;
			}
			StringBuilder cls = new StringBuilder(128);
			GetClassName(hwnd, cls, 128);
			string reason = PopupRejectReason(hwnd, r);
			IntPtr owner = GetWindow(hwnd, GW_OWNER);
			if (Throttle(hwnd, 3000))
			{
				// 阶段 3/4：微信进程内每个可见窗口的 SHOW 都记关联信息与拒绝原因
				Store.Trace("WEIXIN evt SHOW class=" + cls + " rect=" + r.L + "," + r.T + "," + r.R + "," + r.B
					+ " owner=" + (owner == IntPtr.Zero ? "0" : owner.ToString()) + " reason=" + (reason.Length == 0 ? "HIT" : reason));
			}
			if (reason.Length > 0)
			{
				return;
			}
			lock (gate)
			{
				activePopup = hwnd;
				popupShownTick = Environment.TickCount;
			}
			Store.Trace("WEIXIN popup show class=" + cls + " rect=" + r.L + "," + r.T + "," + r.R + "," + r.B);
			DispatchBadge(true, true);
		}

		private static void OnWeixinHide(IntPtr hwnd)
		{
			IntPtr popup;
			lock (gate)
			{
				popup = activePopup;
			}
			if (popup == IntPtr.Zero || popup != hwnd)
			{
				return;
			}
			lock (gate)
			{
				activePopup = IntPtr.Zero;
			}
			long shown = 0;
			lock (gate)
			{
				shown = popupShownTick;
			}
			int visibleMs = Environment.TickCount - (int)shown;
			Store.Trace("WEIXIN popup hide visibleMs=" + visibleMs);
			// 气泡消失后短时间若微信进入前台=用户点击了气泡（已处理）；否则是超时消失，保留红点。
			// 前台路径会自行在 1.5 秒后熄灭，这里无需额外动作。
		}

		private static bool IsChildStyle(IntPtr hwnd)
		{
			long style = GetWindowLong(hwnd, GWL_STYLE);
			return (style & WS_CHILD) != 0;
		}

		// 返回拒绝原因（"" 表示命中气泡特征）
		private static string PopupRejectReason(IntPtr hwnd, RECT r)
		{
			IntPtr owner = GetWindow(hwnd, GW_OWNER);
			if (owner != IntPtr.Zero)
			{
				return "owner";
			}
			long style = GetWindowLong(hwnd, GWL_STYLE);
			if ((style & WS_CHILD) != 0)
			{
				return "child";
			}
			if ((style & WS_CAPTION) != 0)
			{
				return "caption";
			}
			int width = r.R - r.L;
			int height = r.B - r.T;
			if (width < 160 || width > 600 || height < 50 || height > 340)
			{
				return "size";
			}
			RECT wa = WorkArea();
			if (r.R < wa.R - 120 || r.T > wa.T + 200)
			{
				return "pos";
			}
			return "";
		}

		private static RECT WorkArea()
		{
			lock (gate)
			{
				int now = Environment.TickCount;
				if (now - workAreaTick < 300000 && (workArea.R > workArea.L))
				{
					return workArea;
				}
				workAreaTick = now;
			}
			RECT wa = new RECT();
			try
			{
				SystemParametersInfo(SPI_GETWORKAREA, 0, ref wa, 0);
			}
			catch
			{
			}
			lock (gate)
			{
				if (wa.R > wa.L)
				{
					workArea = wa;
				}
				return workArea;
			}
		}

		// 在 Dock 入口快照里找微信入口（目标 exe 为 Weixin.exe 的快捷方式，或名为微信/Weixin 的入口）
		private static string ResolveWeChatPath()
		{
			lock (gate)
			{
				int now = Environment.TickCount;
				if (weChatPath.Length > 0 && now - weChatPathTick < 60000)
				{
					return weChatPath;
				}
				weChatPathTick = now;
			}
			string path = "";
			try
			{
				foreach (Entry entry in DockOrderRules.Ordered(controller.Prefs))
				{
					if (entry.IsFolder || string.IsNullOrEmpty(entry.Path))
					{
						continue;
					}
					string p = entry.Path;
					if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
					{
						string target = "";
						try
						{
							target = Native.ShortcutTarget(p) ?? "";
						}
						catch
						{
						}
						if (target.IndexOf("Weixin.exe", StringComparison.OrdinalIgnoreCase) >= 0
							|| target.IndexOf("WeChat.exe", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							path = p;
							break;
						}
					}
					else if (p.IndexOf("Weixin.exe", StringComparison.OrdinalIgnoreCase) >= 0
						|| p.IndexOf("WeChat.exe", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						path = p;
						break;
					}
				}
			}
			catch
			{
			}
			lock (gate)
			{
				weChatPath = path;
			}
			return path;
		}

		private static void DispatchBadge(bool on, bool pulse)
		{
			string path = ResolveWeChatPath();
			if (path.Length == 0)
			{
				Store.Trace("WEIXIN no dock entry for Weixin (badge skipped)");
				return;
			}
			if (on)
			{
				int now = Environment.TickCount;
				long last;
				lock (gate)
				{
					last = lastPulseTick;
					badgeLitByUs = true;
				}
				if (now - (int)last < 2000)
				{
					Store.Trace("WEIXIN debounce skip (2s) path=" + path);
					pulse = false; // 2 秒防抖：连续消息只亮一次动画
				}
				else
				{
					lock (gate)
					{
						lastPulseTick = now;
					}
				}
			}
			Store.Trace("WEIXIN badge request on=" + on + " pulse=" + pulse + " path=" + path);
			Controller c = controller;
			if (c != null && c.Dock != null && c.Dock.Dispatcher != null && !c.Dock.Dispatcher.HasShutdownStarted)
			{
				bool p = pulse;
				c.Dock.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
				{
					try
					{
						c.Dock.NotifyBadge(path, on, p);
						Store.Trace("WEIXIN badge delivered on=" + on);
					}
					catch (Exception ex)
					{
						Store.Trace("WEIXIN badge deliver error " + ex.Message);
					}
				}));
			}
		}

		// 调试用：托盘菜单触发，验证「状态管理器→Dock 图标→红点+动画」链路
		public static void TestBadge()
		{
			Store.Trace("WEIXIN test badge requested");
			DispatchBadge(true, true);
		}

		public static void Stop()
		{
			try
			{
				if (showHideHook != IntPtr.Zero)
				{
					UnhookWinEvent(showHideHook);
				}
				if (foregroundHook != IntPtr.Zero)
				{
					UnhookWinEvent(foregroundHook);
				}
				if (locationHook != IntPtr.Zero)
				{
					UnhookWinEvent(locationHook);
				}
				if (shellHookRegistered && shellSource != null)
				{
					DeregisterShellHookWindow(shellSource.Handle);
				}
			}
			catch
			{
			}
			showHideHook = IntPtr.Zero;
			foregroundHook = IntPtr.Zero;
			locationHook = IntPtr.Zero;
			shellHookRegistered = false;
		}
	}
}
