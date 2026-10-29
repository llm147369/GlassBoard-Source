using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace GlassBoard;

/// <summary>
/// 健康看门狗：自动识别崩溃、界面卡顿、异常进程与资源异常，
/// 并写入配置文件夹（%LocalAppData%\GlassBoard）的 failures.log。
/// 日志超过 1MB 自动转存为 failures.old.log。
/// </summary>
public static class HealthMonitor
{
	private static readonly object Gate = new object();
	private static readonly Dictionary<string, DateTime> LastReport = new Dictionary<string, DateTime>();
	private static Timer timer;
	private static Dispatcher ui;
	private static bool started;
	private static int hangTicks;
	private static int markerTicks;
	private static long lastWorkingSet = -1L;
	private static string markerPath;

	public static string FileName => Path.Combine(Store.Folder, "failures.log");

	/// <summary>必须在 UI 线程启动。</summary>
	public static void Start()
	{
		if (started)
		{
			return;
		}
		started = true;
		ui = Dispatcher.CurrentDispatcher;
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += OnTaskException;
		CheckPreviousRun();
		timer = new Timer(Tick, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5));
	}

	/// <summary>程序内手动上报（如 DispatcherUnhandledException）。</summary>
	public static void Report(string category, string detail)
	{
		Write(category, detail, 60.0);
	}

	/// <summary>正常退出时调用：清除存活标记，避免下次误报“异常退出”。</summary>
	public static void Shutdown()
	{
		try
		{
			if (markerPath != null && File.Exists(markerPath))
			{
				File.Delete(markerPath);
			}
		}
		catch
		{
		}
	}

	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		object ex = e.ExceptionObject;
		Write("进程崩溃", (ex as Exception)?.ToString() ?? (ex?.ToString() ?? "未知异常"), 0.0);
	}

	private static void OnTaskException(object sender, UnobservedTaskExceptionEventArgs e)
	{
		Write("后台任务异常", e.Exception?.ToString() ?? "未知", 60.0);
		e.SetObserved();
	}

	private static void Tick(object state)
	{
		try
		{
			CheckUiHeartbeat();
			CheckOwnResources();
			CheckHungWindows();
			CheckBridgeProcesses();
			// 存活标记每 60 秒刷新一次，避免频繁写盘
			if (++markerTicks % 12 == 0)
			{
				TouchMarker();
			}
		}
		catch
		{
		}
	}

	/// <summary>
	/// 界面心跳：向 UI 线程投递一个空操作并等待 8 秒；
	/// 超时说明界面线程停滞（长任务阻塞或死锁）。
	/// </summary>
	private static void CheckUiHeartbeat()
	{
		if (ui == null)
		{
			return;
		}
		DispatcherOperation op;
		try
		{
			op = ui.InvokeAsync((Action)(() => { }), DispatcherPriority.Send);
		}
		catch
		{
			return;
		}
		if (op == null || op.Task.Wait(8000))
		{
			if (hangTicks > 0)
			{
				Write("卡顿", "界面恢复响应（此前停滞约 " + hangTicks * 5 + " 秒）", 0.0);
			}
			hangTicks = 0;
			return;
		}
		hangTicks++;
		if (hangTicks == 1 || hangTicks % 6 == 0)
		{
			Write("卡顿", "界面线程已停滞约 " + hangTicks * 5 + " 秒，疑似卡死或长任务阻塞", 0.0);
		}
	}

	private static void CheckOwnResources()
	{
		using (Process self = Process.GetCurrentProcess())
		{
			self.Refresh();
			long ws = self.WorkingSet64;
			int handles = self.HandleCount;
			uint gdi = GetGuiResources(self.Handle, 0u);
			uint user = GetGuiResources(self.Handle, 1u);
			if (ws > 1610612736L)
			{
				Write("内存异常", "进程内存超过 1.5 GB：" + FormatBytes(ws) + "（句柄 " + handles + "，GDI " + gdi + "，USER " + user + "）", 600.0);
			}
			if (handles > 20000)
			{
				Write("资源异常", "句柄数过高：" + handles, 600.0);
			}
			if (gdi > 10000 || user > 10000)
			{
				Write("资源异常", "GUI 资源过高：GDI " + gdi + "，USER " + user, 600.0);
			}
			if (lastWorkingSet >= 0L && ws > lastWorkingSet + 536870912L)
			{
				Write("内存异常", "内存骤增：+" + FormatBytes(ws - lastWorkingSet) + "（当前 " + FormatBytes(ws) + "）", 600.0);
			}
			lastWorkingSet = ws;
		}
	}

	/// <summary>枚举可见顶层窗口，上报其他进程的“未响应”窗口（每进程 30 分钟一次）。</summary>
	private static void CheckHungWindows()
	{
		int selfPid = Process.GetCurrentProcess().Id;
		EnumWindows(delegate (IntPtr h, IntPtr l)
		{
			try
			{
				if (!IsWindowVisible(h) || !IsHungAppWindow(h))
				{
					return true;
				}
				GetWindowThreadProcessId(h, out uint pid);
				if (pid == 0 || pid == selfPid)
				{
					return true;
				}
				Write("异常进程", "检测到未响应窗口：pid " + pid + "（" + ProcessName((int)pid) + "），可能影响系统流畅度", 1800.0);
			}
			catch
			{
			}
			return true;
		}, IntPtr.Zero);
	}

	private static void CheckBridgeProcesses()
	{
		try
		{
			// 本程序正常应有 1 个主进程 + 最多 1 个网易云桥接子进程（--netease-ui-bridge）
			Process[] selfs = Process.GetProcessesByName("GlassBoard");
			int selfCount = 0;
			foreach (Process p in selfs)
			{
				try
				{
					using (p)
					{
						selfCount++;
					}
				}
				catch
				{
					selfCount++;
				}
			}
			if (selfCount > 3)
			{
				Write("异常进程", "检测到 " + selfCount + " 个 GlassBoard 进程，桥接进程可能泄漏", 1800.0);
			}
			// 音乐桥接最多 2 个常驻 powershell，系统残留一般 ≤2；超过 6 个视为异常
			Process[] pss = Process.GetProcessesByName("powershell");
			if (pss.Length > 6)
			{
				Write("异常进程", "powershell 进程数异常：" + pss.Length + " 个（音乐桥接可能反复重启堆积）", 1800.0);
			}
		}
		catch
		{
		}
	}

	/// <summary>上次运行异常退出检测：启动时读存活标记，标记对应进程已死则上报。</summary>
	private static void CheckPreviousRun()
	{
		try
		{
			markerPath = Path.Combine(Store.Folder, "watchdog.alive");
			Directory.CreateDirectory(Store.Folder);
			if (File.Exists(markerPath))
			{
				string text = File.ReadAllText(markerPath).Trim();
				string[] parts = text.Split(' ');
				int oldPid = 0;
				int.TryParse(parts[0], out oldPid);
				bool alive = false;
				if (oldPid > 0)
				{
					try
					{
						using (Process.GetProcessById(oldPid))
						{
							alive = true;
						}
					}
					catch
					{
					}
				}
				if (!alive)
				{
					Write("异常退出", "上次运行未正常退出（崩溃或被强制结束），最后心跳：" + (parts.Length > 1 ? parts[1] : "未知"), 0.0);
				}
			}
			TouchMarker();
		}
		catch
		{
		}
	}

	private static void TouchMarker()
	{
		try
		{
			if (markerPath == null)
			{
				return;
			}
			File.WriteAllText(markerPath, Process.GetCurrentProcess().Id + " " + DateTime.Now.ToString("s"));
		}
		catch
		{
		}
	}

	private static void Write(string category, string detail, double throttleSeconds)
	{
		try
		{
			lock (Gate)
			{
				if (throttleSeconds > 0.0)
				{
					string key = category + "|" + detail;
					DateTime now = DateTime.UtcNow;
					if (LastReport.TryGetValue(key, out DateTime last) && (now - last).TotalSeconds < throttleSeconds)
					{
						return;
					}
					LastReport[key] = now;
					if (LastReport.Count > 200)
					{
						LastReport.Clear();
					}
				}
				string file = FileName;
				if (File.Exists(file) && new FileInfo(file).Length > 1048576L)
				{
					File.Copy(file, Path.Combine(Store.Folder, "failures.old.log"), overwrite: true);
					File.WriteAllText(file, "");
				}
				File.AppendAllText(file, DateTime.Now.ToString("s") + " [" + typeof(HealthMonitor).Assembly.GetName().Version + "] " + category + " " + detail + Environment.NewLine);
			}
		}
		catch
		{
		}
	}

	private static string ProcessName(int pid)
	{
		try
		{
			using (Process p = Process.GetProcessById(pid))
			{
				return p.ProcessName;
			}
		}
		catch
		{
			return "?";
		}
	}

	private static string FormatBytes(long bytes)
	{
		return ((double)bytes / 1048576.0).ToString("0.#") + " MB";
	}

	private delegate bool EnumProc(IntPtr h, IntPtr l);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool IsHungAppWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

	[DllImport("user32.dll")]
	private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
}
