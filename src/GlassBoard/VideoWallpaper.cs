using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassBoard;

// 自定义视频动态壁纸：
// HwndSource 原生 WS_CHILD 窗口（跨进程挂到桌面壁纸 WorkerW 层：图标之下、壁纸之上），
// RootVisual 内嵌 MediaElement 循环播放，与系统静态壁纸互不干扰。
// 说明：WPF 顶层窗口 SetParent 到跨进程 WorkerW 后会被 WPF 自动拉回顶层（GetParent 归零，
// 引发每 2 秒 reattach 死循环）；WinForms Form 宿主则因无 WinForms 消息泵导致 WMP 内核
// 卡在 Opening（MediaOpened 永不触发、白屏）。HwndSource 是 WPF 体系内唯一能原生创建
// WS_CHILD 的宿主：消息由 WPF Dispatcher 派送，MediaElement 正常播放、跨进程挂载稳定。
// 玻璃捕获（Dock/灵动岛）经 GlassCapture 直接打印本窗口拿视频帧，无需特殊适配。
public static class VideoWallpaper
{
	private static VideoWindow window;

	private static bool attached;

	private static double currentVolume = 100.0;

	private static bool currentMuted;

	private static System.Windows.Threading.DispatcherTimer watchdog;
	private static int kickedCount;
	private static Action<string> lastStatus;

	public static bool IsActive
	{
		get
		{
			try
			{
				return attached && window != null && !window.IsDisposed;
			}
			catch
			{
				return false;
			}
		}
	}

	public static string CurrentPath { get; private set; }

	// 启动/切换视频壁纸。status 回调（播放器线程经 Dispatcher 回 UI 线程）用于回报加载结果。
	public static void Start(string path, double volume, bool muted, Action<string> status = null)
	{
		Stop();
		lastStatus = status;
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
		{
			return;
		}
		if (IsWallpaperEngineRunning())
		{
			// Wallpaper Engine 持续清理壁纸 WorkerW 内的外来窗口（每帧移出/销毁），
			// 同时运行会导致视频壁纸无法稳定显示——互斥并给出明确提示。
			Store.Trace("VIDEOWALL blocked by Wallpaper Engine");
			PostStatus(status, "检测到 Wallpaper Engine 正在运行：两者会争夺壁纸层，请先在 WPE 中退出，再开启视频壁纸。");
			return;
		}
		// Win10/精简系统常见：未装 WebView2 运行时。提前探测并给出明确指引，
		// 避免只报"启动失败、查看日志"而日志里又找不到对应条目。
		try
		{
			Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
		}
		catch (Exception rtx)
		{
			Store.Trace("VIDEOWALL runtime missing: " + rtx.GetType().Name + " " + rtx.Message);
			Store.LogError("VIDEOWALL runtime missing: " + rtx.GetType().Name + " " + rtx.Message);
			PostStatus(status, "未检测到 WebView2 运行时（Win10 未安装时为常见原因）：请到 https://developer.microsoft.com/microsoft-edge/webview2/ 下载安装后重启应用。");
			return;
		}
		try
		{
			if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
			{
				return;
			}
			window = new VideoWindow();
			CurrentPath = path;
			currentVolume = Math.Max(0.0, Math.Min(100.0, volume));
			currentMuted = muted;
			window.Load(path, currentVolume, currentMuted, FindWallWorkerW(), status);
			// Load 内部失败会调用 Stop() 把 window 置空（异步 void 的同步段直接抛），
			// 此处必须复查，否则继续解引用 null 产生 NullReferenceException。
			if (window == null || window.IsDisposed || !window.IsHandleCreated)
			{
				return;
			}
			AttachToDesktop(window);
			attached = true;
			EnsureWatchdog();
		}
		catch (Exception ex)
		{
			Store.Trace("VIDEOWALL start failed: " + ex.GetType().FullName + " " + ex.Message);
			Store.LogError("VIDEOWALL start failed: " + ex.GetType().FullName + " " + ex.Message);
			try
			{
				if (window != null)
				{
					window.Close();
				}
			}
			catch
			{
			}
			window = null;
			CurrentPath = null;
			PostStatus(status, "动态壁纸启动失败，详见配置目录 session.log 与 error.log。");
		}
	}

	public static void Stop()
	{
		attached = false;
		lastStatus = null;
		if (window != null)
		{
			try
			{
				window.Close();
			}
			catch
			{
			}
			window = null;
		}
		CurrentPath = null;
	}

	// 其他壁纸软件（Folia 等）接管壁纸层时主动让位：发状态消息后停止播放。
	private static void YieldToExternal(string message)
	{
		try
		{
			if (lastStatus != null)
			{
				Action<string> cb = lastStatus;
				PostStatus(cb, message);
			}
		}
		catch
		{
		}
		Stop();
	}

	public static void SetVolume(double volume, bool muted)
	{
		if (window != null)
		{
			window.SetVolume(Math.Max(0.0, Math.Min(100.0, volume)), muted);
		}
	}

	// 玻璃捕获用：返回视频壁纸窗口句柄（未激活时 IntPtr.Zero）
	internal static IntPtr CaptureHandle()
	{
		try
		{
			if (!attached || window == null || window.IsDisposed || !window.IsHandleCreated)
			{
				return IntPtr.Zero;
			}
			return window.Handle;
		}
		catch
		{
			return IntPtr.Zero;
		}
	}

	private static void PostStatus(Action<string> status, string msg)
	{
		if (status == null)
		{
			return;
		}
		try
		{
			if (System.Windows.Application.Current != null)
			{
				System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(delegate { status(msg); }));
			}
			else
			{
				status(msg);
			}
		}
		catch
		{
		}
	}

	private static bool IsWallpaperEngineRunning()
	{
		try
		{
			foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
			{
				string name = p.ProcessName;
				if (string.Equals(name, "wallpaper64", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(name, "wallpaper32", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(name, "wallpaperservice64", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(name, "wallpaperservice32", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	// 保活看护：Wallpaper Engine 等动态壁纸软件启动/重启时会清理 WorkerW 内的外来窗口
	// （SetParent 移出或直接销毁）。每 2 秒校验窗口句柄与挂载层级：被移出则重挂并置顶，
	// 被销毁则按原路径/音量/静音重建；Wallpaper Engine 启动后主动让位（互斥）。
	private static void EnsureWatchdog()
	{
		if (watchdog != null)
		{
			return;
		}
		watchdog = new System.Windows.Threading.DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(2.0)
		};
		watchdog.Tick += delegate
		{
			try
			{
				if (window == null)
				{
					return;
				}
				if (IsWallpaperEngineRunning())
				{
					Store.Trace("VIDEOWALL yield to Wallpaper Engine");
					YieldToExternal("检测到 Wallpaper Engine 正在运行，动态壁纸已暂停。");
					Stop();
					return;
				}
				IntPtr hwnd = window.IsHandleCreated ? window.Handle : IntPtr.Zero;
				if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
				{
					// 窗口被外部销毁（其他壁纸软件接管壁纸层时常见）：连续多次则主动让位
					kickedCount++;
					if (kickedCount >= 3)
					{
						kickedCount = 0;
						Store.Trace("VIDEOWALL yield to external wallpaper host");
						YieldToExternal("检测到其他壁纸软件接管了壁纸层，动态壁纸已暂停。");
						Stop();
						return;
					}
					string path = CurrentPath;
					double vol = currentVolume;
					bool muted = currentMuted;
					Stop();
					if (!string.IsNullOrEmpty(path) && File.Exists(path))
					{
						Store.Trace("VIDEOWALL watchdog rebuild");
						Start(path, vol, muted);
					}
					return;
				}
				IntPtr anchor = FindWallWorkerW();
				if (anchor == IntPtr.Zero)
				{
					return;
				}
				if (GetParent(hwnd) != anchor)
				{
					kickedCount++;
					Store.Trace("VIDEOWALL watchdog reattach");
					AttachToWorkerW(hwnd);
				}
				else
				{
					if (kickedCount > 0)
					{
						kickedCount--;
					}
					if (!IsWindowVisible(hwnd))
					{
						ShowWindow(hwnd, 8);
					}
				}
			}
			catch (Exception ex)
			{
				Store.Trace("VIDEOWALL watchdog error: " + ex.GetType().Name);
				// 句柄访问异常通常意味着窗口已被外部销毁：按原配置重建
				try
				{
					if (window != null)
					{
						string path = CurrentPath;
						double vol = currentVolume;
						bool muted = currentMuted;
						Stop();
						if (!string.IsNullOrEmpty(path) && File.Exists(path))
						{
							Start(path, vol, muted);
						}
					}
				}
				catch
				{
				}
			}
		};
		watchdog.Start();
	}

	private static IntPtr FindWallWorkerW()
	{
		IntPtr progman = FindWindow("Progman", null);
		if (progman == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		IntPtr wall = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
		if (wall == IntPtr.Zero)
		{
			// 无 WorkerW 时向 Progman 发送 0x052C 让 explorer 创建（幂等安全）
			SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out IntPtr _);
			wall = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
		}
		return wall;
	}

	// 挂载到壁纸 WorkerW 层（图标之下、壁纸之上）：
	// 关键：WS_POPUP 样式的窗口被 SetParent 到 WorkerW 后，Win10/11 桌面窗口树会
	// 在数百毫秒内把它拉回顶层（GetParent 归零），导致每 2 秒 reattach 死循环。
	// 必须同时把样式转为 WS_CHILD（去掉 WS_POPUP）才能稳定挂载。
	private static void AttachToWorkerW(IntPtr hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		IntPtr wall = FindWallWorkerW();
		bool bottom = false;
		if (wall == IntPtr.Zero)
		{
			// Win10 精简/自定义 shell 可能没有 WorkerW（或 0x052C 创建失败）。
			// 兜底挂到 Progman 本身并排到 DefView（图标层）之下：视频在壁纸上、
			// 图标之下，效果等效；不可再创建 WS_CHILD 顶层窗口（CreateWindowEx 会
			// 直接失败报「无法创建最上层子窗口」）。
			wall = FindWindow("Progman", null);
			bottom = true;
		}
		if (wall == IntPtr.Zero)
		{
			return;
		}
		SetParent(hwnd, wall);
		SetWindowLong(hwnd, -16, (GetWindowLong(hwnd, -16) & 0x7FFFFFFF) | 0x40000000); // 去 WS_POPUP 加 WS_CHILD
		ShowWindow(hwnd, 8);
		// 置顶：盖过 WorkerW 内其它动态壁纸窗口（Folia 等）；Progman 兜底时排到
		// DefView（图标层）之下、壁纸之上（HWND_BOTTOM=1，不屏蔽 z 序插入）。
		SetWindowPos(hwnd, bottom ? (IntPtr)1 : IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | (bottom ? 0u : 0x0004u));
	}

	private static void AttachToDesktop(VideoWindow w)
	{
		IntPtr hwnd = w.Handle;
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		AttachToWorkerW(hwnd);
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	internal static extern IntPtr FindWindow(string cls, string title);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

	[DllImport("user32.dll")]
	internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);

	[DllImport("user32.dll", SetLastError = true)]
	internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int cx, int cy, bool repaint);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

	[DllImport("user32.dll")]
	private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(IntPtr h);

	[DllImport("user32.dll")]
	private static extern IntPtr GetParent(IntPtr h);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr h, int cmd);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr h);

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(IntPtr h, int index);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(IntPtr h, int index, int value);

	[DllImport("user32.dll")]
	private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lp);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);

	private delegate bool EnumChildProc(IntPtr h, IntPtr lp);

	// 点击壁纸层窗口时，WM_MOUSEACTIVATE 会被 WorkerW 转发到被点击的最深子窗口
	// （Chrome_RenderWidgetHostHWND 等，跨进程、不经我们的 HwndSource hook），这些窗口
	// 默认返回 MA_ACTIVATE → WorkerW 被激活 → 系统为壁纸链 SetFocus 失败 → 默认提示音。
	// 给链上所有 Chromium/宿主窗口补 WS_EX_NOACTIVATE：激活在源头被拒绝，无焦点转移无声音。
	internal static void SilenceChromiumChain(IntPtr root)
	{
		try
		{
			WalkSilence(root, 0);
		}
		catch
		{
		}
	}

	private static void WalkSilence(IntPtr parent, int depth)
	{
		if (depth > 8)
		{
			return;
		}
		EnumChildWindows(parent, delegate (IntPtr h, IntPtr lp)
		{
			var cn = new System.Text.StringBuilder(64);
			GetClassName(h, cn, 64);
			string c = cn.ToString();
			if (c.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) || c.StartsWith("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal) || c.StartsWith("WindowsForms10", StringComparison.Ordinal) || c.StartsWith("Intermediate D3D", StringComparison.Ordinal))
			{
				int ex = GetWindowLong(h, -20);
				if ((ex & 0x08000000) == 0)
				{
					SetWindowLong(h, -20, ex | 0x08000000);
				}
			}
			WalkSilence(h, depth + 1);
			return true;
		}, IntPtr.Zero);
	}
}

// 视频壁纸窗口：WinForms 无边框全屏、不抢焦点、不占任务栏，
// ElementHost 内嵌 MediaElement（UniformToFill）循环播放。
// 视频壁纸窗口：HwndSource 原生子窗口（WS_CHILD 直接创建，跨进程 WorkerW 稳定挂载），
// RootVisual 内嵌 MediaElement（UniformToFill）循环播放。
// 说明：WPF 顶层 Window 与 WinForms Form 挂载 WorkerW 均有缺陷（WPF 拉回顶层 /
// WinForms 无消息泵导致 WMP 内核卡 Opening），HwndSource 是 WPF 体系内唯一
// 能原生创建 WS_CHILD 的宿主——消息由 WPF Dispatcher 派送，MediaElement 正常播放。
// 视频壁纸窗口：HwndSource 原生子窗口（WS_CHILD 直接创建，跨进程 WorkerW 稳定挂载）。
// 播放内核用 WebView2（Chromium 硬解）而非 MediaElement——本机 WPF MediaElement 全链路
// 卡 Opening（连 WAV 都不触发 MediaOpened，普通窗口/PS/net10 环境均复现，系统级故障），
// WMP COM 内核却正常；WebView2 走独立渲染进程，绕开 WPF 媒体管线。
public class VideoWindow
{
	private HwndSource source;

	private Microsoft.Web.WebView2.WinForms.WebView2 web;

	private Action<string> status;

	private bool stopped;

	private double volume = 1.0;

	private bool muted;

	public bool IsDisposed
	{
		get
		{
			try
			{
				return source == null || source.IsDisposed;
			}
			catch
			{
				return true;
			}
		}
	}

	public bool IsHandleCreated
	{
		get
		{
			try
			{
				return source != null && source.Handle != IntPtr.Zero;
			}
			catch
			{
				return false;
			}
		}
	}

	public IntPtr Handle
	{
		get
		{
			try
			{
				if (source == null || source.IsDisposed)
				{
					return IntPtr.Zero;
				}
				return source.Handle;
			}
			catch
			{
				return IntPtr.Zero;
			}
		}
	}

	public async void Load(string path, double volumePct, bool mutedFlag, IntPtr wall, Action<string> status)
	{
		this.status = status;
		this.volume = Math.Max(0.0, Math.Min(1.0, volumePct / 100.0));
		this.muted = mutedFlag;
		try
		{
			// WM_MOUSEACTIVATE 由 WorkerW 逐层转发给被点击的子窗口；返回 MA_NOACTIVATE
			// 阻止整条链（含 WorkerW）激活——点击视频壁纸不抢焦点、无系统提示音。
			IntPtr MouseActivateHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
			{
				if (msg == 0x0021)
				{
					handled = true;
					return (IntPtr)3;
				}
				return IntPtr.Zero;
			}

			Rect vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
			IntPtr parent = wall;
			if (parent == IntPtr.Zero)
			{
				// Win10 精简/自定义 shell 无 WorkerW：直接挂 Progman（AttachToWorkerW 会排到图标层之下）。
				parent = VideoWallpaper.FindWindow("Progman", null);
			}
			if (parent == IntPtr.Zero)
			{
				Store.Trace("VIDEOWALL no desktop host window");
				DispatcherSafe(status, "未找到桌面壁纸层（Progman/WorkerW 缺失），无法创建视频壁纸窗口。");
				VideoWallpaper.Stop();
				return;
			}
			HwndSourceParameters p = new HwndSourceParameters("GlassBoardVideoWallpaper")
			{
				PositionX = (int)vs.Left,
				PositionY = (int)vs.Top,
				Width = (int)vs.Width,
				Height = (int)vs.Height,
				ParentWindow = parent,
				WindowStyle = 0x40000000 | 0x10000000 | 0x04000000 | 0x02000000,
				// WS_EX_NOACTIVATE：点击壁纸层窗口时拦截 WM_MOUSEACTIVATE 冒泡，防止
				// WorkerW 被激活后 SetFocus 找不到焦点窗口而触发系统提示音。
				ExtendedWindowStyle = 0x08000000
			};
			source = new HwndSource(p);
			source.AddHook(MouseActivateHook);

			// WebView2 控件不经过 WPF 视觉树（airspace 合成在 WS_CHILD WorkerW 链下会黑屏），
			// 直接 CreateControl 后把控件句柄挂到裸窗口句柄下，纯 Win32 父子链。
			web = new Microsoft.Web.WebView2.WinForms.WebView2
			{
				DefaultBackgroundColor = System.Drawing.Color.Black
			};
			web.CreateControl();
			VideoWallpaper.SetParent(web.Handle, source.Handle);
			VideoWallpaper.MoveWindow(web.Handle, 0, 0, (int)vs.Width, (int)vs.Height, true);
			web.Visible = true;

			// 挂 WorkerW 的窗口链处于桌面 z 序底层，Chromium 的遮挡检测会把渲染进程当后台
			// 处理暂停合成（黑屏）。禁用原生遮挡计算/后台化/节流；保留 GPU 合成——
			// 软件渲染会把每帧位图 blit 到 UI 线程（2560×1440 全屏），持续占用 Dispatcher，
			// 导致全局低级鼠标钩子超时被系统摘除（大面板点击外部收不起来的根因）。
			try
			{
				System.Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling");
			}
			catch
			{
			}

			Store.Trace("VIDEOWALL env init...");
			System.Threading.Tasks.Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> envTask =
				Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, System.IO.Path.Combine(Store.Folder, "WebView2"));
			if (await System.Threading.Tasks.Task.WhenAny(envTask, System.Threading.Tasks.Task.Delay(20000)) != envTask)
			{
				Store.Trace("VIDEOWALL env init timeout");
				DispatcherSafe(status, "WebView2 初始化超时：运行时可能刚安装未重启电脑，请重启电脑后再开启动态壁纸。");
				VideoWallpaper.Stop();
				return;
			}
			var env = await envTask;
			if (stopped)
			{
				return;
			}
			Store.Trace("VIDEOWALL env ok");
			System.Threading.Tasks.Task ensureTask = web.EnsureCoreWebView2Async(env);
			if (await System.Threading.Tasks.Task.WhenAny(ensureTask, System.Threading.Tasks.Task.Delay(20000)) != ensureTask)
			{
				Store.Trace("VIDEOWALL ensure timeout");
				DispatcherSafe(status, "WebView2 初始化超时：运行时可能刚安装未重启电脑，请重启电脑后再开启动态壁纸。");
				VideoWallpaper.Stop();
				return;
			}
			await ensureTask;
			if (stopped)
			{
				return;
			}
			Store.Trace("VIDEOWALL ensure ok");
			web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
			web.CoreWebView2.Settings.IsStatusBarEnabled = false;
			web.CoreWebView2.Settings.IsZoomControlEnabled = false;
			web.CoreWebView2.WebMessageReceived += delegate (object s, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
			{
				if (stopped)
				{
					return;
				}
				string msg = null;
				try
				{
					msg = e.TryGetWebMessageAsString();
				}
				catch
				{
					msg = e.WebMessageAsJson;
				}
				if (string.IsNullOrEmpty(msg))
				{
					return;
				}
				if (msg.StartsWith("error", StringComparison.Ordinal))
				{
					Store.Trace("VIDEOWALL decode error: " + msg);
					if (status != null)
					{
						Action<string> cb = status;
						DispatcherSafe(cb, "视频无法解码：请换一个 MP4/H.264 编码的视频文件。");
					}
					VideoWallpaper.Stop();
				}
				else if (msg == "ready")
				{
					Store.Trace("VIDEOWALL opened ok(webview2): " + path);
					ApplyVolume();
				}
				else if (msg == "playing" || msg.StartsWith("playfail", StringComparison.Ordinal))
				{
					Store.Trace("VIDEOWALL playback state: " + msg);
				}
			};
			string dir = System.IO.Path.GetDirectoryName(path);
			string file = System.IO.Path.GetFileName(path);
			web.CoreWebView2.SetVirtualHostNameToFolderMapping("vw.local", dir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
			string videoUrl = "https://vw.local/" + Uri.EscapeDataString(file);
			string html = "<html style='margin:0;background:#000'><body style='margin:0;overflow:hidden'>" +
				"<video id='v' src='" + videoUrl + "' playsinline preload='auto' style='width:100vw;height:100vh;object-fit:cover'></video>" +
				"<script>var v=document.getElementById('v');v.muted=true;v.onerror=function(){window.chrome.webview.postMessage('error:'+(v.error?(v.error.code||0):0));};v.onloadeddata=function(){window.chrome.webview.postMessage('ready');};v.onplaying=function(){window.chrome.webview.postMessage('playing');};v.addEventListener('timeupdate',function(){try{if(v.duration-v.currentTime<=0.25){v.currentTime=0;}}catch(e){}});v.addEventListener('ended',function(){try{v.currentTime=0;v.play();}catch(e){}});var p=v.play();if(p&&p.catch){p.catch(function(e){window.chrome.webview.postMessage('playfail:'+e.name);});}</script></body></html>";
			Store.Trace("VIDEOWALL loading(webview2): " + path);
			web.CoreWebView2.NavigateToString(html);
			// 渲染进程窗口（Chrome_RenderWidgetHostHWND）异步创建，先静音一次，之后
			// 每 2 秒补扫直到窗口关闭，防止 Chromium 重建窗口后样式回退。
			VideoWallpaper.SilenceChromiumChain(source.Handle);
			System.Windows.Threading.DispatcherTimer silencer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
			silencer.Tick += delegate
			{
				if (stopped || source == null)
				{
					silencer.Stop();
					return;
				}
				VideoWallpaper.SilenceChromiumChain(source.Handle);
			};
			silencer.Start();
		}
		catch (Exception ex)
		{
			Store.Trace("VIDEOWALL load failed: " + ex.GetType().FullName + " " + ex.Message);
			Store.LogError("VIDEOWALL load failed: " + ex.GetType().FullName + " " + ex.Message);
			if (status != null)
			{
				Action<string> cb = status;
				DispatcherSafe(cb, "动态壁纸启动失败，详见配置目录 session.log 与 error.log。");
			}
			VideoWallpaper.Stop();
		}
	}

	private void DispatcherSafe(Action<string> cb, string msg)
	{
		try
		{
			if (System.Windows.Application.Current != null)
			{
				System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(delegate { cb(msg); }));
			}
			else
			{
				cb(msg);
			}
		}
		catch
		{
		}
	}

	public void Close()
	{
		stopped = true;
		try
		{
			if (web != null)
			{
				web.Dispose();
			}
		}
		catch
		{
		}
		try
		{
			if (source != null)
			{
				source.Dispose();
			}
		}
		catch
		{
		}
		source = null;
		web = null;
	}

	public void SetVolume(double volumePct, bool mutedFlag)
	{
		volume = Math.Max(0.0, Math.Min(1.0, volumePct / 100.0));
		muted = mutedFlag;
		ApplyVolume();
	}

	private void ApplyVolume()
	{
		try
		{
			if (web == null || web.CoreWebView2 == null || stopped)
			{
				return;
			}
			string vol = volume.ToString(System.Globalization.CultureInfo.InvariantCulture);
			web.CoreWebView2.ExecuteScriptAsync("try{var v=document.getElementById('v');if(v){v.volume=" + vol + ";v.muted=" + (muted ? "true" : "false") + ";v.play();}}catch(e){}");
		}
		catch
		{
		}
	}
}