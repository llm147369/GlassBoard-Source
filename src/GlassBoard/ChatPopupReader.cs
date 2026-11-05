using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Interop.UIAutomationClient;

namespace GlassBoard
{
	// 聊天软件消息弹窗实时读取。
	//
	// 原理：QQ/钉钉/飞书/Telegram 等聊天软件收到消息时，会在屏幕角落弹出一个
	// 短暂的消息气泡（无标题栏小窗，几秒后自动消失）。这里用 WinEvent Hook 事件驱动地
	// 观察这类窗口的显示（不轮询、不注入进程），在后台 STA 线程上用 UI Automation
	// 读取气泡里的文字（联系人 + 正文），推送进灵动岛通知列表。
	//
	// 微信（Weixin 4.x）从不弹气泡、只做任务栏闪烁，由 WeChatNotifier 的闪烁通道
	// 单独处理（只显示“你有一条微信通知”，不读取主窗口会话列表——读到的可能是
	// 用户选中的聊天对象而不是真正的发送人，宁缺毋滥）。
	//
	// 隐私：文字只进内存里的通知列表（上限 50 条），绝不写日志；日志只记进程名、
	// 窗口类名与成功/失败计数。读取不到文字时只显示来源应用 + “有新消息”，不猜测内容。
	//
	// 覆盖不到的聊天软件仍由 NotificationCenter 的系统 toast 通道兜底。
	public static class ChatPopupReader
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
		private static extern bool IsWindow(IntPtr hwnd);
		[DllImport("user32.dll")]
		private static extern bool SystemParametersInfo(uint action, uint param, ref RECT v, uint winIni);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int n);
		[DllImport("user32.dll")]
		private static extern int GetWindowLong(IntPtr hwnd, int index);

		private const uint EVENT_OBJECT_SHOW = 0x8002;
		private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
		private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
		private const uint OBJID_WINDOW = 0;
		private const uint SPI_GETWORKAREA = 0x0030;
		private const int GWL_STYLE = -16;
		private const long WS_CAPTION = 0x00C00000;
		private const int UIA_ControlTypePropertyId = 30003;
		private const int UIA_TextControlTypeId = 50020;

		private static WinEventDelegate callback;
		private static IntPtr hook;
		private static Thread worker;
		private static readonly AutoResetEvent signal = new AutoResetEvent(false);
		private static readonly object gate = new object();
		private static readonly Queue<IntPtr> pending = new Queue<IntPtr>();
		private static readonly Dictionary<IntPtr, int> throttleTick = new Dictionary<IntPtr, int>();
		private static bool running;
		private static long pushedCount;
		private static long readOkCount;
		private static long readEmptyCount;

		// 进程名（不含扩展名，小写）→ 显示名
		private static readonly Dictionary<string, string> appMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "weixin", "微信" },
			{ "wechat", "微信" },
			{ "qq", "QQ" },
			{ "tim", "TIM" },
			{ "dingtalk", "钉钉" },
			{ "telegram", "Telegram" },
			{ "whatsapp", "WhatsApp" },
			{ "lark", "飞书" },
			{ "feishu", "飞书" },
			{ "wecom", "企业微信" },
			{ "wxwork", "企业微信" }
		};

		public static void Start()
		{
			lock (gate)
			{
				if (running)
				{
					return;
				}
				running = true;
			}
			try
			{
				callback = OnShow;
				hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, callback, 0, 0,
					WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
				Store.Trace("CHAT start hook ret=" + (hook != IntPtr.Zero));
			}
			catch (Exception ex)
			{
				Store.Trace("CHAT start failed " + ex.Message);
			}
			worker = new Thread(WorkerLoop)
			{
				IsBackground = true,
				Name = "ChatPopupReader"
			};
			worker.SetApartmentState(ApartmentState.STA);
			worker.Start();
		}

		public static void Stop()
		{
			lock (gate)
			{
				running = false;
			}
			try
			{
				if (hook != IntPtr.Zero)
				{
					UnhookWinEvent(hook);
				}
			}
			catch
			{
			}
			hook = IntPtr.Zero;
			signal.Set();
		}

		public static string Status()
		{
			lock (gate)
			{
				if (!running)
				{
					return "聊天弹窗监听：未启动。";
				}
				return "聊天弹窗监听：运行中；已推送 " + pushedCount + " 条，成功读取 " + readOkCount
					+ " 条，读不到内容 " + readEmptyCount + " 条。";
			}
		}

		private static void OnShow(IntPtr h, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
		{
			try
			{
				if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW)
				{
					return;
				}
				uint pid;
				GetWindowThreadProcessId(hwnd, out pid);
				if (pid == 0 || pid == (uint)Process.GetCurrentProcess().Id)
				{
					return;
				}
				string proc;
				try
				{
					proc = Process.GetProcessById((int)pid).ProcessName;
				}
				catch
				{
					return;
				}
				string app;
				if (!appMap.TryGetValue(proc, out app))
				{
					return; // 非聊天软件：交给系统 toast 通道
				}
				RECT r;
				if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out r))
				{
					return;
				}
				int w = r.R - r.L;
				int hgt = r.B - r.T;
				if (w < 120 || w > 720 || hgt < 40 || hgt > 520)
				{
					return;
				}
				long style = GetWindowLong(hwnd, GWL_STYLE);
				if ((style & WS_CAPTION) != 0)
				{
					return; // 有标题栏：主窗口/对话框，不是消息气泡
				}
				RECT wa = WorkArea();
				if (r.R < wa.R - 100 || (r.T > wa.T + 250 && r.B < wa.B - 250))
				{
					return; // 不在角落（右上角或右下角的短命气泡才是消息提示）
				}
				int now = Environment.TickCount;
				lock (gate)
				{
					int last;
					if (throttleTick.TryGetValue(hwnd, out last) && now - last < 3000)
					{
						return;
					}
					if (throttleTick.Count > 400)
					{
						throttleTick.Clear();
					}
					throttleTick[hwnd] = now;
					pending.Enqueue(hwnd);
				}
				signal.Set();
			}
			catch (Exception ex)
			{
				Store.Trace("CHAT event error " + ex.Message);
			}
		}

		private static void WorkerLoop()
		{
			IUIAutomation automation = null;
			try
			{
				automation = new CUIAutomation();
			}
			catch (Exception ex)
			{
				Store.Trace("CHAT uia create failed " + ex.Message);
				automation = null;
			}
			while (true)
			{
				IntPtr hwnd = IntPtr.Zero;
				lock (gate)
				{
					if (!running && pending.Count == 0)
					{
						return;
					}
					if (pending.Count > 0)
					{
						hwnd = pending.Dequeue();
					}
				}
				if (hwnd == IntPtr.Zero)
				{
					signal.WaitOne(500);
					continue;
				}
				// 等气泡内容渲染完成再读（300ms），窗口此时可能已被点击/超时关闭
				Thread.Sleep(300);
				try
				{
					if (!IsWindow(hwnd) || !IsWindowVisible(hwnd))
					{
						continue;
					}
					string proc = "";
					string app = "";
					try
					{
						uint pid;
						GetWindowThreadProcessId(hwnd, out pid);
						proc = Process.GetProcessById((int)pid).ProcessName;
						if (!appMap.TryGetValue(proc, out app))
						{
							continue;
						}
					}
					catch
					{
						continue;
					}
					ReadPopup(automation, hwnd, proc, app);
				}
				catch (Exception ex)
				{
					Store.Trace("CHAT read error " + ex.Message);
				}
			}
		}

		private static void ReadPopup(IUIAutomation automation, IntPtr hwnd, string proc, string app)
		{
			List<string> lines = new List<string>();
			// 1) 窗口标题（很多聊天软件把联系人名字放在标题上）
			try
			{
				StringBuilder t = new StringBuilder(256);
				if (GetWindowText(hwnd, t, 256) > 0)
				{
					string titleText = t.ToString().Trim();
					if (titleText.Length > 0)
					{
						lines.Add(titleText);
					}
				}
			}
			catch
			{
			}
			// 2) UI Automation 读气泡正文
			if (automation != null)
			{
				try
				{
					IUIAutomationElement root = automation.ElementFromHandle(hwnd);
					if (root != null)
					{
						IUIAutomationCondition cond = automation.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_TextControlTypeId);
						IUIAutomationElementArray arr = root.FindAll(TreeScope.TreeScope_Descendants, cond);
						HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
						for (int i = 0; i < arr.Length; i++)
						{
							IUIAutomationElement el = null;
							try
							{
								el = arr.GetElement(i);
								string name = (el != null) ? (el.CurrentName ?? "").Trim() : "";
								if (name.Length > 0 && !seen.Contains(name))
								{
									seen.Add(name);
									lines.Add(name.Length > 300 ? name.Substring(0, 300) : name);
								}
							}
							catch
							{
							}
							finally
							{
								if (el != null)
								{
									Marshal.ReleaseComObject(el);
								}
							}
							if (lines.Count >= 40)
							{
								break;
							}
						}
						Marshal.ReleaseComObject(arr);
						Marshal.ReleaseComObject(root);
					}
				}
				catch (Exception ex)
				{
					Store.Trace("CHAT uia read failed class=" + ClassName(hwnd) + " err=" + ex.Message);
				}
			}
			// 3) 组装：第一行=联系人/标题，其余=正文；完全读不到只提示“有新消息”
			string title = "";
			string body = "";
			List<string> use = new List<string>();
			foreach (string s in lines)
			{
				string v = s.Trim();
				if (v.Length == 0 || v == app)
				{
					continue;
				}
				if (!use.Contains(v))
				{
					use.Add(v);
				}
			}
			if (use.Count > 0)
			{
				title = use[0];
				if (title.Length > 40)
				{
					title = title.Substring(0, 40);
				}
				if (use.Count > 1)
				{
					StringBuilder sb = new StringBuilder();
					for (int i = 1; i < use.Count; i++)
					{
						if (sb.Length > 0)
						{
							sb.Append(' ');
						}
						sb.Append(use[i]);
						if (sb.Length > 512)
						{
							break;
						}
					}
					body = sb.Length > 512 ? sb.ToString(0, 512) : sb.ToString();
				}
				else
				{
					body = "有新消息";
				}
				lock (gate)
				{
					readOkCount++;
				}
			}
			else
			{
				body = "有新消息";
				lock (gate)
				{
					readEmptyCount++;
				}
			}
			IslandNotifications.Push(new IslandNotification
			{
				AppName = app,
				Aumid = "popup:" + proc.ToLowerInvariant(),
				Title = title,
				Body = body,
				Stamp = DateTime.Now
			});
			lock (gate)
			{
				pushedCount++;
			}
		}

		private static string ClassName(IntPtr hwnd)
		{
			StringBuilder cls = new StringBuilder(128);
			try
			{
				GetClassName(hwnd, cls, 128);
			}
			catch
			{
			}
			return cls.ToString();
		}

		private static RECT WorkArea()
		{
			RECT wa = new RECT();
			try
			{
				SystemParametersInfo(SPI_GETWORKAREA, 0, ref wa, 0);
			}
			catch
			{
			}
			return wa;
		}
	}
}
