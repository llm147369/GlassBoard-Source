using System;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBoard;

// 灵动岛协调器：连接 Controller 与各子系统。
// 职责：窗口生命周期、事件订阅与线程切换、样式变更分发、清理。
// 所有事件订阅都在 UI 线程上回调（NotificationCenter/IslandNotifications 已在
// UI 线程推送；BatteryCore/IslandClipboard 内部自行 Dispatcher 切换）。
public sealed class IslandHub : IDisposable
{
	private readonly Controller C;
	private IslandWindow window;
	private bool disposed;
	private bool subsystemsStarted;
	private DispatcherTimer watchdog;

	public IslandWindow Window => window;

	public IslandHub(Controller c)
	{
		C = c;
	}

	// Controller.CompleteDeferredStartup 中调用；之后设置页通过 RefreshStyle/ApplyLive 驱动外观。
	public void Start()
	{
		if (disposed)
		{
			return;
		}
		if (window != null && !window.IsClosedWindow)
		{
			// 已有一个活着的窗口：直接对齐设置即可，避免重复订阅事件。
			OnPrefsChanged();
			return;
		}
		IslandOptions o = C.Prefs.Island ?? new IslandOptions();
		o.Normalize();
		C.Prefs.Island = o;
		IslandSettings.Set(o);
		window = new IslandWindow(C);
		window.ApplyStyle(o);
		IslandNotifications.Arrived -= OnNotificationArrived;
		IslandNotifications.Arrived += OnNotificationArrived;
		IslandNotifications.Cleared -= OnNotificationsCleared;
		IslandNotifications.Cleared += OnNotificationsCleared;
		BatteryCore.Changed -= OnBatteryChanged;
		BatteryCore.Changed += OnBatteryChanged;
		IslandClipboard.Changed -= OnClipboardChanged;
		IslandClipboard.Changed += OnClipboardChanged;
		IslandClipboard.PrivacyChanged -= OnPrivacyChanged;
		IslandClipboard.PrivacyChanged += OnPrivacyChanged;
		IslandTheme.Changed -= OnThemeChanged;
		IslandTheme.Changed += OnThemeChanged;
		WeatherEngine we = C.Weather;
		if (we != null)
		{
			we.Changed -= OnWeatherChanged;
			we.Changed += OnWeatherChanged;
		}
		if (C.Music != null)
		{
			C.Music.Changed -= OnMusicChanged;
			C.Music.Changed += OnMusicChanged;
		}
		if (!subsystemsStarted)
		{
			subsystemsStarted = true;
			BatteryCore.Start(window.Dispatcher);
			IslandClipboard.Start();
			IslandTheme.Start();
		}
		StartWatchdog();
		Store.Trace("ISLAND start enabled=" + o.Enabled + " monitor=" + o.MonitorIndex + " size=" + o.CollapsedWidth + "x" + o.CollapsedHeight + " renderTier=" + (RenderCapability.Tier >> 16) + " gpuAnims=" + (RenderCapability.IsPixelShaderVersionSupported(2, 0) ? 1 : 0));
		if (o.Enabled)
		{
			// 先在窗口不可见时摆好最终位置并读取壁纸背景（原地完成、不移窗不闪烁），
			// 再执行入场动画。
			window.PrepareStartup();
			window.ShowCollapsed();
		}
	}

	// 自愈看护：每 5 秒检查窗口是否意外消失（被关闭/隐藏）。全屏策略的主动隐藏不干预。
	private void StartWatchdog()
	{
		if (watchdog != null)
		{
			return;
		}
		watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
		watchdog.Tick += delegate
		{
			if (disposed)
			{
				return;
			}
			try
			{
				IslandOptions o = C.Prefs.Island ?? new IslandOptions();
				if (!o.Enabled)
				{
					return;
				}
				if (window == null || window.IsClosedWindow)
				{
					Store.Trace("ISLAND watchdog restart window");
					Start();
				}
				else if (!window.IsVisible && !window.HiddenByPolicy)
				{
					Store.Trace("ISLAND watchdog re-show");
					window.ShowCollapsed();
				}
			}
			catch
			{
			}
		};
		watchdog.Start();
	}

	// 音乐引擎可能在启动后才被 WidgetManager 创建；由 Controller 在创建后调用。
	// WidgetManager 重建/销毁引擎（桌面组件开关联动）时也会调用，因此支持重绑：
	// 先解绑旧实例再绑定新实例，避免灵动岛永远挂在已销毁的引擎上。
	private MusicEngine attachedMusic;

	public void AttachMusic(MusicEngine music)
	{
		if (disposed)
		{
			return;
		}
		if (attachedMusic != null)
		{
			attachedMusic.Changed -= OnMusicChanged;
		}
		attachedMusic = music;
		if (music != null)
		{
			music.Changed += OnMusicChanged;
		}
		OnMusicChanged();
	}

	public void Toggle()
	{
		if (window == null)
		{
			return;
		}
		IslandOptions o = C.Prefs.Island ?? new IslandOptions();
		if (!o.Enabled)
		{
			return;
		}
		if (window.Expanded)
		{
			window.Collapse();
		}
		else
		{
			window.ShowCollapsed();
		}
	}

	// 设置页改动后调用：立即把新样式应用到窗口。
	public void ApplyLive(IslandOptions o)
	{
		if (window == null || o == null)
		{
			return;
		}
		window.ApplyStyle(o);
		IslandSettings.Set(o);
	}

	// 设置保存后调用（Controller.Save 前由设置页负责写 Prefs.Island）。
	public void OnPrefsChanged()
	{
		IslandOptions o = C.Prefs.Island ?? new IslandOptions();
		o.Normalize();
		Store.Trace("ISLAND prefs changed enabled=" + o.Enabled + " window=" + (window != null));
		window?.ApplyStyle(o);
		IslandSettings.Set(o);
		if (window != null)
		{
			if (o.Enabled && window.Visibility != System.Windows.Visibility.Visible)
			{
				window.ShowCollapsed();
			}
			else if (!o.Enabled)
			{
				Store.Trace("ISLAND close by prefs");
				window.Close();
			}
		}
		else if (o.Enabled)
		{
			Start();
		}
	}

	private void OnNotificationArrived(IslandNotification n)
	{
		window?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
		{
			if (disposed)
			{
				return;
			}
			window.NotifyArrived(n);
		}));
	}

	private void OnNotificationsCleared()
	{
		window?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
		{
			if (!disposed)
			{
				window.NotifyCleared();
			}
		}));
	}

	private void OnBatteryChanged()
	{
		window?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
		{
			if (!disposed)
			{
				window.RefreshBattery();
			}
		}));
	}

	private void OnClipboardChanged()
	{
		window?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
		{
			if (!disposed)
			{
				window.RefreshClipboardPage();
			}
		}));
	}

	private void OnPrivacyChanged()
	{
		window?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
		{
			if (!disposed)
			{
				window.RefreshClipboardPage();
				window.RefreshNotificationsPage();
			}
		}));
	}

	private void OnThemeChanged()
	{
		window?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
		{
			if (disposed)
			{
				return;
			}
			IslandOptions o = C.Prefs.Island ?? new IslandOptions();
			if (o.FollowSystemTheme)
			{
				window.ApplyStyle(o);
				IslandSettings.Set(o);
			}
			else
			{
				// 即使不跟随系统主题，文字/图标颜色也跟随系统明暗
				// （用户反馈：深色模式下展开文字仍是黑色）。
				window.RefreshTextTheme();
			}
		}));
	}

	private void OnMusicChanged()
	{
		window?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
		{
			if (!disposed)
			{
				window.RefreshMusic();
			}
		}));
	}

	private void OnWeatherChanged()
	{
		window?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
		{
			if (!disposed)
			{
				window.RefreshWeather();
			}
		}));
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}
		disposed = true;
		if (watchdog != null)
		{
			watchdog.Stop();
			watchdog = null;
		}
		IslandNotifications.Arrived -= OnNotificationArrived;
		IslandNotifications.Cleared -= OnNotificationsCleared;
		BatteryCore.Changed -= OnBatteryChanged;
		IslandClipboard.Changed -= OnClipboardChanged;
		IslandClipboard.PrivacyChanged -= OnPrivacyChanged;
		IslandTheme.Changed -= OnThemeChanged;
		WeatherEngine we2 = C.Weather;
		if (we2 != null)
		{
			we2.Changed -= OnWeatherChanged;
		}
		if (C.Music != null)
		{
			C.Music.Changed -= OnMusicChanged;
		}
		BatteryCore.Stop();
		IslandClipboard.Stop();
		IslandTheme.Stop();
		try
		{
			window?.DisposeBridge();
		}
		catch
		{
		}
		try
		{
			window?.Close();
		}
		catch
		{
		}
		window = null;
	}
}
