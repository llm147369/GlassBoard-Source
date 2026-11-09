using System;
using System.Collections.Generic;
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
		// 创建新窗口前先清理任何残留旧实例（正在销毁中/已失效的引用），
		// 并在 Closed 后自动置空引用——杜绝新旧两个灵动岛并存。
		IslandWindow oldWindow = window;
		try
		{
			window = new IslandWindow(C);
		}
		catch (Exception ex)
		{
			// 构造失败（如 NAudio 等第三方依赖缺失）时不能让窗口引用停留在半成品状态：
			// 置空引用并清理可能已经创建的孤儿窗口，保证设置里的开关仍能即时生效。
			Store.Log(ex);
			window = null;
			CloseOrphanIslands();
			return;
		}
		IslandWindow created = window;
		window.Closed += delegate
		{
			// 该窗口已关闭：若当前引用仍指向它则作废，后续 OnPrefsChanged/Start 才能正确重建
			if (ReferenceEquals(window, created))
			{
				window = null;
			}
		};
		try
		{
			if (oldWindow != null)
			{
				oldWindow.DisposeBridge();
				oldWindow.Close();
			}
		}
		catch
		{
		}
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
					try
					{
						window.ShowCollapsed();
					}
					catch
					{
						// 已关闭/失效的窗口引用：丢弃并重建，杜绝新旧两个岛并存
						window = null;
						Start();
					}
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

	// 全局玻璃材质变化时调用：只刷新灵动岛的材质/模糊背景，不重建收起态与页面。
	public void RefreshMaterial()
	{
		if (window == null || window.IsClosedWindow)
		{
			return;
		}
		window.RefreshMaterialOnly();
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
		if (window == null || window.IsClosedWindow)
		{
			// 窗口引用已失效：清空后按需重建。绝不能对已关闭窗口访问 Visibility/Show，
			// 否则 WPF 抛 InvalidOperationException 且旧引用残留（“再点开多一个岛”的根因）。
			window = null;
			if (o.Enabled)
			{
				Start();
			}
			else
			{
				// 构造失败或引用丢失后残留的孤儿窗口也要关掉，
				// 否则设置里关闭灵动岛后屏幕上仍有那条栏，只能重启才消失。
				CloseOrphanIslands();
			}
			return;
		}
		window.ApplyStyle(o);
		IslandSettings.Set(o);
		if (o.Enabled)
		{
			if (window.Visibility != System.Windows.Visibility.Visible)
			{
				window.ShowCollapsed();
			}
		}
		else
		{
			Store.Trace("ISLAND close by prefs");
			IslandWindow closing = window;
			window = null;
			try
			{
				closing.DisposeBridge();
				closing.Close();
			}
			catch
			{
				// 关闭失败也不复活引用：watchdog 看到 Enabled=false 不会重建，
				// 若窗口对象仍有残留，下次启用时 Start() 会先清理再新建。
			}
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

	// 关闭没有 hub 引用的残留灵动岛窗口。
	// 构造异常或引用丢失时，窗口对象可能仍活在 Application.Windows 里；
	// 不清理就会出现“设置里关了灵动岛、屏幕上还挂着”的假象。
	private void CloseOrphanIslands()
	{
		try
		{
			System.Windows.Application app = System.Windows.Application.Current;
			if (app == null)
			{
				return;
			}
			Action close = delegate
			{
				List<System.Windows.Window> list = new List<System.Windows.Window>();
				foreach (System.Windows.Window w in app.Windows)
				{
					list.Add(w);
				}
				foreach (System.Windows.Window w2 in list)
				{
					IslandWindow iw = w2 as IslandWindow;
					if (iw != null)
					{
						try
						{
							iw.DisposeBridge();
							iw.Close();
						}
						catch
						{
						}
					}
				}
			};
			if (app.Dispatcher.CheckAccess())
			{
				close();
			}
			else
			{
				app.Dispatcher.BeginInvoke(close);
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
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
