using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GlassBoard;

public class DockWindow : GlassWindow
{
	private sealed class TaskGroup
	{
		public string Key;

		public string Name;

		public List<RunningWindow> Windows;
	}

	private Controller C;

	// —— 悬浮窗口预览（任务栏式缩略图）——
	private System.Windows.Controls.Primitives.Popup previewPopup;
	private System.Windows.Controls.Primitives.Popup runningListPopup;
	private System.Windows.Threading.DispatcherTimer previewTimer;
	private System.Windows.Threading.DispatcherTimer previewCloseTimer;
	private FrameworkElement previewAnchor;
	private object previewOwner;
	private static readonly Dictionary<long, BitmapSource> previewCache = new Dictionary<long, BitmapSource>();
	private System.Collections.Generic.List<System.Windows.Interop.HwndSource> previewHosts;

	private System.Collections.Generic.List<IntPtr> previewThumbs;
	private System.Collections.Generic.List<System.Windows.Controls.Image> previewSlots;
	private System.Collections.Generic.List<IntPtr> previewSlotWindows;
	private int previewSetupTries;
	// 悬浮弹层看门狗：所有打开的悬浮弹层登记在此；图标与弹层都不在鼠标下超过 1 秒即强制关闭，
	// Dock 隐藏时全部立即关闭——杜绝弹层在切换窗口、全屏、Dock 收隐后变成屏幕上的孤儿（"标题常驻"）。
	private readonly System.Collections.Generic.List<System.Windows.Controls.Primitives.Popup> watchPopups = new System.Collections.Generic.List<System.Windows.Controls.Primitives.Popup>();
	private readonly Dictionary<System.Windows.Controls.Primitives.Popup, int> watchAway = new Dictionary<System.Windows.Controls.Primitives.Popup, int>();
	private System.Windows.Threading.DispatcherTimer watchTimer;
	private int hookMouseMoveTrace;
	private int hookNcTrace;
	private int diagTrace;

	// —— Dock 右键菜单管理（点任意地方即关闭）——
	private System.Windows.Controls.ContextMenu dockOpenMenu;
	private bool dockMenuJustClosed;

	private StackPanel Items = new StackPanel
	{
		Orientation = System.Windows.Controls.Orientation.Horizontal,
		VerticalAlignment = VerticalAlignment.Center,
		HorizontalAlignment = System.Windows.HorizontalAlignment.Center
	};

	private HwndSource source;

	private HotkeyManager hotkeys;

	private DispatcherTimer autoTimer = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(50.0)
	};

	private DispatcherTimer runningTimer = new DispatcherTimer
	{
		// 1 秒一拍：运行点/活动胶囊跟随前台窗口即时切换；进程扫描仍按下方 3 秒节流。
		Interval = TimeSpan.FromSeconds(1.0)
	};

	private DateTime lastRunningScan = DateTime.MinValue;

	// —— 运行状态指示器（macOS 风格）：尺寸/圆角集中管理，颜色跟随 Dock 强调色 ——
	// 运行中 = 图标下方小圆点；活动窗口 = 稍长的胶囊形指示条（覆盖圆点显示）。
	private const double RunDotWidth = 5.0;
	private const double RunDotHeight = 4.0;
	private const double RunDotRadius = 2.0;
	private const double RunActiveWidth = 14.0;
	private const double RunActiveHeight = 4.0;
	private const double RunActiveRadius = 2.0;

	private AutoHidePolicy hidePolicy = new AutoHidePolicy();

	private bool renderedHidden;

	private double shownTop;

	// 运行期记忆的"贴底"位置：任务栏隐藏/显示会改变 WorkingArea 高度，
	// 默认贴底的 Dock 每次 Refresh 都会跟着漂移；首次定位后固定住，
	// 只有显示设置变化或用户主动"重置位置/改 DockGap"才重新贴底。
	private double? settledTop;

	private Rect dockWorkingArea;

	private Rect dockScreenArea;

	private Thumb moveGrip;

	private bool dragging;

	private int revealSerial;

	private int layoutSerial;

	private readonly DockWheelState taskWheel = new DockWheelState();

	private readonly DispatcherTimer taskHintTimer = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(1800.0)
	};

	private List<TaskGroup> taskGroups = new List<TaskGroup>();

	private string taskSignature = "";

	private bool externalDockDrag;

	private bool boardPinDragging;

	private TextBlock taskHint;

	private static readonly DependencyProperty LayoutKey = DependencyProperty.RegisterAttached("DockLayoutKey", typeof(string), typeof(DockWindow), new PropertyMetadata(null));

	private static readonly DependencyProperty DragSuppressed = DependencyProperty.RegisterAttached("DockDragSuppressed", typeof(bool), typeof(DockWindow), new PropertyMetadata(false));

	private Action dockPointerClick;

	private bool dockIconDragging;

	// 图标按下（点击/拖拽判定）期间冻结悬停动画：Mouse.Capture 会让按钮收到 MouseLeave，
	// 松手释放捕获又触发 MouseEnter——若不冻结，每次点击图标都会先落下再弹起（“跳动”）。
	private bool dockPressActive;

	// 应用通知红点：Dock 每次 Refresh 重建按钮后，通知中心按 path 恢复徽标状态；
	// notifyIcons 存图标元素本体，弹跳/发光动画只作用在它上面（grid2 不接收命中，点击区不变）。
	private readonly Dictionary<string, Border> notifyBadges = new Dictionary<string, Border>();
	private readonly Dictionary<string, FrameworkElement> notifyIcons = new Dictionary<string, FrameworkElement>();
	private readonly Dictionary<string, bool> notifyOn = new Dictionary<string, bool>();

	// 运行指示器诊断：每个入口最近一次指示状态（0=无 1=圆点 2=活动胶囊），变化时打日志
	private readonly Dictionary<string, int> runState = new Dictionary<string, int>();

	private bool dockPointerPreviousBusy;

	private DockPointerGesture dockPointer;

	private FrameworkElement dockPointerOwner;

	private bool dockPointerFromMenu;

	private readonly DispatcherTimer dockPointerWatch = new DispatcherTimer(DispatcherPriority.Input)
	{
		Interval = TimeSpan.FromMilliseconds(33.0)
	};

	private Border dockPointerPreview;

	private TextBlock dockPointerLabel;

	public double RestingTop => shownTop;

	private IEnumerable<string> TaskKeys => taskGroups.Select((TaskGroup g) => "running:" + g.Key);

	private List<string> LayoutKeys
	{
		get
		{
			if (!taskWheel.Running)
			{
				return DockLayoutRules.Common(C.Prefs);
			}
			return DockLayoutRules.Running(C.Prefs, TaskKeys);
		}
	}

	private bool DockInputBusy
	{
		get
		{
			if (dockPointer == null)
			{
				return dockIconDragging;
			}
			return true;
		}
	}

	public void GamePolling(bool enabled)
	{
		TimeSpan timeSpan = TimeSpan.FromMilliseconds(enabled ? 350 : 3000);
		if (runningTimer.Interval != timeSpan)
		{
			runningTimer.Interval = timeSpan;
		}
	}

	public DockWindow(Controller c)
		: base(29.0)
	{
		C = c;
		Title = "GlassBoard Dock";
		Height = 112.0;
		ShowActivated = false;
		Topmost = false;
		ContentGrid.Children.Add(Items);
		InitDockPointer();
		InitializeBackgroundMove();
		InitTaskDock();
		RunningWindows.InitForegroundTracking();
		RunningWindows.StartMaintenance();
		moveGrip = new Thumb
		{
			Width = 42.0,
			Height = 6.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0.0, 7.0, 0.0, 0.0),
			Cursor = System.Windows.Input.Cursors.SizeAll,
			ToolTip = "拖动 Dock；设置中可锁定位置"
		};
		Root.Children.Add(moveGrip);
		moveGrip.Template = GlassWindow.GripTemplate();
		Closing += (object s, CancelEventArgs e) =>
		{
			if (!C.ShuttingDown)
			{
				e.Cancel = true;
				Dispatcher.BeginInvoke((Action)(() =>
				{
					C.Quit();
				}));
			}
		};
		moveGrip.DragStarted += (object s, DragStartedEventArgs e) =>
		{
			BeginDockMove();
		};
		moveGrip.DragDelta += (object s, DragDeltaEventArgs e) =>
		{
			if (dragging)
			{
				Rect rect = DesktopWindowPolicy.WorkArea(this, pointer: true);
				Left = FeatureRules.Position(Left + e.HorizontalChange, rect.Left, rect.Width, Width);
				Top = FeatureRules.Position(Top + e.VerticalChange, rect.Top, rect.Height, Height);
			}
		};
		moveGrip.DragCompleted += (object s, DragCompletedEventArgs e) =>
		{
			FinishDockMove();
		};
		autoTimer.Tick += (object s, EventArgs e) =>
		{
			UpdateAutoHide();
		};
		runningTimer.Tick += (object s, EventArgs e) =>
		{
			if (Environment.TickCount - diagTrace > 2500)
			{
				diagTrace = Environment.TickCount;
				System.Windows.IInputElement captured = System.Windows.Input.Mouse.Captured;
				System.Windows.IInputElement over = System.Windows.Input.Mouse.DirectlyOver;
				Store.Trace("DIAG captured=" + ((captured == null) ? "null" : captured.GetType().Name) + " over=" + ((over == null) ? "null" : over.GetType().Name) + " dockOver=" + IsMouseOver + " itemsOver=" + Items.IsMouseOver + " busy=" + DockInputBusy + " hidden=" + renderedHidden);
			}
			ReleaseCancelledExternalDrag();
			C.CheckGameMode();
			RunningWindows.SampleForeground();
			if (!C.GlassSuppressed && (IsVisible || C.Board.IsVisible) && (C.Prefs.Dock.ShowRunning || taskWheel.Running))
			{
				// 增量更新：只改指示器可见性（纯快照读取，无扫描），前台切换 1 秒内跟上
				UpdateRunning();
			}
			if (!C.GlassSuppressed && (IsVisible || C.Board.IsVisible) && (C.Prefs.Dock.ShowRunning || taskWheel.Running) && !((DateTime.UtcNow - lastRunningScan).TotalSeconds < (double)(renderedHidden ? 10 : 3)))
			{
				lastRunningScan = DateTime.UtcNow;
				RunningWindows.Scan();
				RefreshTaskSnapshot();
				UpdateRunning();
				C.Board.UpdateRunning();
			}
		};
		runningTimer.Start();
		SourceInitialized += (object s, EventArgs e) =>
		{
			source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
			source.AddHook(Hook);
			hotkeys = new HotkeyManager(RegisterKey, (int id) =>
			{
				Native.UnregisterHotKey(source.Handle, id);
			});
			List<string> list = hotkeys.Start(C.Prefs.Hotkeys);
			foreach (HotkeySetting k2 in hotkeys.Active)
			{
				Store.Trace("HOTKEY registered id=" + k2.ActionId + " " + HotkeyRules.Label(k2) + " active=" + hotkeys.Active.Count);
			}
			// 出厂组合被其他程序占用时自动改用备选按键：用户拿到手即有可用快捷键，
			// 无需手动进设置改（用户自定义过的组合保持原样，交给下方重试机制）。
			uint[] fallbackKeys = new uint[18] { 69u, 66u, 71u, 75u, 74u, 78u, 89u, 80u, 81u, 85u, 76u, 86u, 77u, 73u, 79u, 90u, 88u, 87u };
			bool changed = false;
			foreach (HotkeySetting k in C.Prefs.Hotkeys)
			{
				if (!k.Enabled || !IsStock(k) || hotkeys.Active.Any((HotkeySetting a) => a.ActionId == k.ActionId))
				{
					continue;
				}
				foreach (uint vk in fallbackKeys)
				{
					if (C.Prefs.Hotkeys.Any((HotkeySetting h) => h.Enabled && h.ActionId != k.ActionId && h.Modifiers == k.Modifiers && h.Key == vk))
					{
						continue;
					}
					HotkeySetting candidate = k.Copy();
					candidate.Key = vk;
					if (RegisterKey(candidate))
					{
						k.Key = vk;
						hotkeys.Active.Add(candidate.Copy());
						changed = true;
						Store.Trace("HOTKEY auto-fallback id=" + k.ActionId + " -> " + HotkeyRules.Label(candidate));
						TrayWarning(HotkeyRules.Actions[k.ActionId - 1] + " 默认组合 " + HotkeyRules.Label(HotkeyRules.Defaults()[k.ActionId - 1]) + " 被占用，已自动改用 " + HotkeyRules.Label(candidate) + "（可在设置中修改）");
						break;
					}
				}
			}
			if (changed)
			{
				Store.Save(C.Prefs);
			}
			if (C.Prefs.Hotkeys.Any((HotkeySetting k) => k.Enabled && !hotkeys.Active.Any((HotkeySetting a) => a.ActionId == k.ActionId)))
			{
				// 开机启动时其他程序可能尚未就绪占用组合键，持续自动重试直到全部注册成功
				//（不设次数上限：占用者无论多晚释放，都会自动接管，无需手动重新注册）。
				DispatcherTimer retry = new DispatcherTimer
				{
					Interval = TimeSpan.FromSeconds(5.0)
				};
				int tries = 0;
				retry.Tick += (object ss, EventArgs ee) =>
				{
					tries++;
					List<string> errors = new List<string>();
					foreach (HotkeySetting k in C.Prefs.Hotkeys)
					{
						if (k.Enabled && !hotkeys.Active.Any((HotkeySetting a) => a.ActionId == k.ActionId))
						{
							if (RegisterKey(k))
							{
								hotkeys.Active.Add(k.Copy());
								Store.Trace("HOTKEY retry ok id=" + k.ActionId + " tries=" + tries);
							}
							else
							{
								errors.Add(HotkeyRules.Actions[k.ActionId - 1] + "（" + HotkeyRules.Label(k) + "）");
							}
						}
					}
					if (errors.Count == 0)
					{
						retry.Stop();
					}
					else if (tries == 6)
					{
						// 只提醒一次：之后继续静默重试，不再打扰
						TrayWarning(string.Join("、", errors) + " 正在后台自动重试，释放后自动恢复");
					}
				};
				retry.Start();
				TrayWarning(string.Join("、", (from k in C.Prefs.Hotkeys
					where k.Enabled && !hotkeys.Active.Any((HotkeySetting a) => a.ActionId == k.ActionId)
					select HotkeyRules.Actions[k.ActionId - 1] + "（" + HotkeyRules.Label(k) + "）").ToList()));
			}
		};
		System.Windows.Controls.ContextMenu contextMenu = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		System.Windows.Controls.MenuItem menuItem = new System.Windows.Controls.MenuItem
		{
			Header = "添加软件或文件到 Dock"
		};
		menuItem.Click += (object ss, RoutedEventArgs ee) =>
		{
			C.ChooseDockFiles();
		};
		contextMenu.Items.Add(menuItem);
		System.Windows.Controls.MenuItem menuItem2 = new System.Windows.Controls.MenuItem
		{
			Header = "外观设置"
		};
		menuItem2.Click += (object s, RoutedEventArgs e) =>
		{
			C.Settings();
		};
		contextMenu.Items.Add(menuItem2);
		AddDockControls(contextMenu);
		System.Windows.Controls.MenuItem menuItem3 = new System.Windows.Controls.MenuItem
		{
			Header = "退出"
		};
		menuItem3.Click += (object s, RoutedEventArgs e) =>
		{
			C.Quit();
		};
		contextMenu.Items.Add(menuItem3);
		ContextMenu = contextMenu;
		TrackMenu(contextMenu);
		// 任意点击先关掉已打开的 Dock 菜单：左键只关菜单不触发动作，右键关旧菜单后交给系统开新菜单
		PreviewMouseDown += (object s, MouseButtonEventArgs e) =>
		{
			if (e.ChangedButton == MouseButton.Right)
			{
				CloseDockMenu();
				dockMenuJustClosed = false;
				return;
			}
			if (CloseDockMenu())
			{
				dockMenuJustClosed = true;
				e.Handled = true;
			}
			else if (dockMenuJustClosed)
			{
				dockMenuJustClosed = false;
			}
		};
		SystemEvents.DisplaySettingsChanged += DisplayChanged;
	}

	private void TrayWarning(string shortcuts)
	{
		Dispatcher.BeginInvoke((Action)(() =>
		{
			if (C.Tray != null)
			{
				C.Tray.ShowBalloonTip(3000, "GlassBoard-Source", shortcuts + " 已被占用，对应功能仍可从托盘操作。", ToolTipIcon.Info);
			}
		}));
	}

	private bool RegisterKey(HotkeySetting key)
	{
		bool flag = Native.RegisterHotKey(source.Handle, key.ActionId, key.Modifiers | 0x4000, key.Key);
		string combo = key.ActionId + "|" + key.Modifiers + "|" + key.Key;
		if (!flag)
		{
			// 同一组合只记一条日志：5 秒自动重试期间不再重复刷日志；
			// 一旦注册成功即清空标记，下次再冲突时重新记录一次。
			if (!loggedHotkeyFailures.Contains(combo))
			{
				loggedHotkeyFailures.Add(combo);
				Store.Log(new Win32Exception(Marshal.GetLastWin32Error(), "RegisterHotKey: " + HotkeyRules.Actions[key.ActionId - 1] + " " + HotkeyRules.Label(key)));
			}
		}
		else
		{
			loggedHotkeyFailures.Remove(combo);
		}
		return flag;
	}

	private static readonly HashSet<string> loggedHotkeyFailures = new HashSet<string>();

	// 判断组合是否为出厂默认（含旧版默认 Ctrl+Alt+D）：
	// 出厂组合冲突时自动改用备选按键；用户自定义过的组合保持原样交给重试机制。
	private static bool IsStock(HotkeySetting k)
	{
		uint key = k.Key;
		switch (k.ActionId)
		{
			case 1:
				return key == 32u && k.Modifiers == 3u;
			case 2:
				return key == 84u && k.Modifiers == 6u;
			case 3:
				return (key == 68u || key == 69u) && k.Modifiers == 3u;
			case 4:
				return key == 65u && k.Modifiers == 3u;
			case 5:
				return key == 83u && k.Modifiers == 3u;
			case 6:
				return key == 82u && k.Modifiers == 3u;
			case 7:
				return key == 72u && k.Modifiers == 3u;
			default:
				return false;
		}
	}

	private void DisplayChanged(object s, EventArgs e)
	{
		Dispatcher.BeginInvoke((Action)(() =>
		{
			C.Board.Hide();
			settledTop = null;
			Refresh();
		}));
	}

	// 主动要求下次刷新重新贴底：设置里「重置 Dock 位置」与调整「距工作区底边」时调用。
	public void ResetSettledTop()
	{
		settledTop = null;
	}

	public bool ApplyHotkeys(List<HotkeySetting> candidate, out string error)
	{
		if (hotkeys == null)
		{
			error = "快捷键窗口尚未就绪。";
			return false;
		}
		return hotkeys.TryApply(candidate, out error);
	}

	public bool HotkeyActive(int id)
	{
		if (hotkeys != null)
		{
			return hotkeys.Active.Any((HotkeySetting k) => k.ActionId == id);
		}
		return false;
	}

	/// <summary>实时探测组合键占用情况：null=可用；""=被其他程序占用；非空字符串=与本程序某动作重复（返回该动作名）。</summary>
	public string ProbeHotkey(uint modifiers, uint key)
	{
		if (hotkeys != null)
		{
			foreach (HotkeySetting item in hotkeys.Active)
			{
				if (item.Modifiers == modifiers && item.Key == key)
				{
					return HotkeyRules.Actions[item.ActionId - 1];
				}
			}
		}
		if (source == null)
		{
			return null;
		}
		int id = 24576 + (int)(key % 511);
		if (Native.RegisterHotKey(source.Handle, id, modifiers | 0x4000, key))
		{
			Native.UnregisterHotKey(source.Handle, id);
			return null;
		}
		return "";
	}

	public string RetryHotkeys()
	{
		if (hotkeys == null)
		{
			return "快捷键窗口尚未就绪。";
		}
		hotkeys.Release();
		List<string> list = hotkeys.Start(C.Prefs.Hotkeys);
		if (list.Count != 0)
		{
			return "以下组合被占用，请改键或使用开关：\n" + string.Join("\n", list);
		}
		return "已重新注册启用的快捷键。";
	}

	public List<HotkeySetting> SuspendHotkeys()
	{
		List<HotkeySetting> result = ((hotkeys == null) ? new List<HotkeySetting>() : hotkeys.Active.Select((HotkeySetting k) => k.Copy()).ToList());
		if (hotkeys != null)
		{
			hotkeys.Release();
		}
		return result;
	}

	public void ResumeHotkeys(List<HotkeySetting> keys)
	{
		if (hotkeys != null)
		{
			hotkeys.Release();
			List<string> list = hotkeys.Start(keys);
			if (list.Count > 0)
			{
				TrayWarning(string.Join("、", list));
			}
		}
	}

	public void ReleaseHotkey()
	{
		CancelDockPointer(refresh: false);
		autoTimer.Stop();
		runningTimer.Stop();
		taskHintTimer.Stop();
		if (hotkeys != null)
		{
			hotkeys.Release();
		}
		SystemEvents.DisplaySettingsChanged -= DisplayChanged;
	}

	private IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
	{
		switch (msg)
		{
		case 132:
			// 诊断：WM_NCHITTEST 到达说明系统在向 Dock 派发鼠标命中测试（限速记录）
			if (Environment.TickCount - hookNcTrace > 1000)
			{
				hookNcTrace = Environment.TickCount;
				Store.Trace("HOOK nchittest x=" + ((int)((long)l & 0xFFFF)) + " y=" + ((int)(((long)l >> 16) & 0xFFFF)));
			}
			break;
		case 513:
			Store.Trace("HOOK lbuttondown");
			break;
		case 512:
			if (Environment.TickCount - hookMouseMoveTrace > 1000)
			{
				hookMouseMoveTrace = Environment.TickCount;
				Store.Trace("HOOK mousemove");
			}
			break;
		case 33:
			// WM_MOUSEACTIVATE -> MA_NOACTIVATE: 点击 Dock 不抢前台焦点。
			// 此刻旧前台窗口尚未失活，先记录它，供"再点关闭"判断使用。
			RunningWindows.RecordPressForeground();
			handled = true;
			return (IntPtr)3;
		case 32815:
			// 重复实例唤起消息：静默忽略（旧实现会触发"恢复任务栏与桌面图标"，
			// 开机双启动/重复启动时把用户的隐藏设置复位——去掉该副作用）。
			handled = true;
			Store.Trace("HOTKEY duplicate-instance restore ignored");
			return IntPtr.Zero;
		case 786:
		{
			int id = w.ToInt32();
			handled = true;
			Store.Trace("HOTKEY press id=" + id);
			Dispatcher.BeginInvoke((Action)(() =>
			{
				if (id == 1)
				{
					C.Toggle();
				}
				else if (id == 2)
				{
					C.ShellAction(1);
				}
				else if (id == 3)
				{
					C.ShellAction(2);
				}
				else if (id == 4)
				{
					C.AddFromAnywhere();
				}
				else if (id == 5)
				{
					C.Settings();
				}
				else if (id == 6)
				{
					C.ShellAction(3);
				}
				else if (id == 7)
				{
					C.ToggleTaskbarAutoHide();
				}
			}));
			break;
		}
		}
		return IntPtr.Zero;
	}

	protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
	{
		Store.Trace("WINMOUSE enter over=" + ((System.Windows.Input.Mouse.DirectlyOver == null) ? "null" : System.Windows.Input.Mouse.DirectlyOver.GetType().Name));
		base.OnMouseEnter(e);
	}

	protected override void OnPreviewMouseDown(System.Windows.Input.MouseButtonEventArgs e)
	{
		System.Windows.Point point = e.GetPosition(this);
		System.Text.StringBuilder stringBuilder = new System.Text.StringBuilder();
		DependencyObject dependencyObject = e.OriginalSource as DependencyObject;
		int num = 0;
		while (dependencyObject != null && num < 14)
		{
			string text = "";
			if (dependencyObject is FrameworkElement frameworkElement && frameworkElement.Tag != null)
			{
				text = "[tag=" + frameworkElement.Tag + "]";
			}
			stringBuilder.Append(" ").Append(dependencyObject.GetType().Name).Append(text);
			if (dependencyObject == this)
			{
				break;
			}
			dependencyObject = ((dependencyObject is System.Windows.Media.Visual || dependencyObject is System.Windows.Media.Media3D.Visual3D) ? System.Windows.Media.VisualTreeHelper.GetParent(dependencyObject) : System.Windows.LogicalTreeHelper.GetParent(dependencyObject));
			num++;
		}
		Store.Trace("WINMOUSE previewdown right=" + (e.ChangedButton == System.Windows.Input.MouseButton.Right) + " pos=" + (int)point.X + "," + (int)point.Y + " chain:" + stringBuilder);
		base.OnPreviewMouseDown(e);
	}

	public void Refresh(bool capture = true)
	{
		if (DockInputBusy)
		{
			return;
		}
		double width = Width;
		double left = Left;
		StopDockLayoutMotion();
		DockOptions dock = C.Prefs.Dock;
		dock.Normalize();
		LightTheme = C.Prefs.Light;
		MenuTheme.Apply(ContextMenu, C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		MotionEnabled = dock.MotionEnabled;
		MotionLevel = dock.MotionLevel;
		ConfigureGlass(dock);
		Topmost = dock.AlwaysOnTop || dock.AutoHide || boardPinDragging;
		revealSerial++;
		BeginAnimation(Window.TopProperty, null);
		renderedHidden = false;
		hidePolicy = new AutoHidePolicy();
		moveGrip.Visibility = (C.Prefs.Placement.DockLocked ? Visibility.Collapsed : Visibility.Visible);
		moveGrip.Cursor = (C.Prefs.Placement.DockLocked ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.SizeAll);
		moveGrip.ToolTip = DockTip(C.Prefs.Placement.DockLocked ? "右键 Dock → 允许自由拖动 Dock" : "拖动把手或空白区域移动 Dock，松手保存");
		Items.Children.Clear();
		// 重建图标的同时清空徽标注册表：DockButton 会重新注册并恢复通知状态
		notifyBadges.Clear();
		notifyIcons.Clear();
		Items.Margin = new Thickness(dock.HoverPadding(), 0.0, dock.HoverPadding(), 0.0);
		Surface.CornerRadius = new CornerRadius(dock.Radius);
		AddSystemButton("glassboard://dock/apps", DockButton("▦", null, () =>
		{
			C.Toggle();
		}, "应用面板"), () =>
		{
			C.Toggle();
		});
		if (dock.ShowComputer && !dock.Consolidate)
		{
			// 此电脑：直接打开系统官方「此电脑」（explorer shell:MyComputerFolder），
			// 不做激活/最小化切换，行为与任务栏固定图标一致。
			AddSystemButton("glassboard://dock/computer", DockButton("computer", null, () =>
			{
				C.Board.Hide();
				Native.Computer();
			}, "此电脑"), () =>
			{
				C.Board.Hide();
				Native.Computer();
			});
			// 回收站：单击打开系统回收站窗口；把文件拖到图标上直接送入回收站（可恢复）
			AddSystemButton("glassboard://dock/recycle", DockButton("recycle", null, () =>
			{
				C.Board.Hide();
				Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
				{
					UseShellExecute = true
				});
			}, "回收站"), () =>
			{
				C.Board.Hide();
				Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
				{
					UseShellExecute = true
				});
			});
		}
		if (dock.ShowControlCenter)
		{
			// 控制中心：Wi-Fi / 蓝牙 / 音量 / 输出设备 / 亮度，点击在 Dock 上方展开。
			AddSystemButton(DockLayoutRules.ControlCenter, DockButton("control", null, () =>
			{
				C.ToggleControlCenter();
			}, "控制中心"), () =>
			{
				C.ToggleControlCenter();
			});
		}
		Rect rect = (dockWorkingArea = DesktopWindowPolicy.WorkArea(this));
		dockScreenArea = DesktopWindowPolicy.WorkArea(this, pointer: false, fullScreen: true);
		List<Entry> list = DockOrderRules.Ordered(C.Prefs);
		int num = (taskWheel.Running ? taskGroups.Count : list.Count);
		int num2 = dock.VisiblePinned(rect.Width - 20.0, num);
		int num3 = ((num > num2) ? Math.Max(0, num2 - 1) : num2);
		if (taskWheel.Running)
		{
			BuildTaskButtons(num3, num2);
		}
		else
		{
			foreach (Entry a in list.Take(num3))
			{
				// 常用软件：左键启动，右键精简菜单（打开 / 位置 / 移动 / 添加 / 自定义 / 移除）
				// 图标走后台管线：缓存未命中先渲染占位符，提取完成后回填，不再阻塞 UI 线程
				System.Windows.Controls.Button button = null;
				ImageSource icon = IconPipeline.Request(a, C.Prefs.Dock.ShowShortcutOverlays, true, false, img =>
				{
					if (button != null)
					{
						DockSetIcon(a, button, img);
					}
				});
				button = DockButton(null, icon, () =>
				{
					C.Launch(a);
				}, a.Name, a);
				System.Windows.Controls.ContextMenu contextMenu = C.DockMenu(a);
				TrackMenu(contextMenu);
				button.ContextMenu = contextMenu;
				Items.Children.Add(button);
			}
			if (list.Count > num3 && num2 > 0)
			{
				System.Windows.Controls.Button more = DockButton("···", null, () =>
				{
				}, "更多固定应用");
				System.Windows.Controls.ContextMenu menu = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
				foreach (Entry item2 in list.Skip(num3))
				{
					System.Windows.Controls.MenuItem item = new System.Windows.Controls.MenuItem
					{
						Header = item2.Name
					};
					Entry entry = item2;
					item.Click += (object s, RoutedEventArgs e) =>
					{
						if (!DockDragSuppresses(item))
						{
							C.Launch(entry);
						}
					};
					BindDockSource(item, entry, () =>
					{
						menu.IsOpen = false;
					});
					menu.Items.Add(item);
				}
				AddDockControls(menu);
				more.ContextMenu = menu;
				TrackMenu(menu);
				more.Click += (object s, RoutedEventArgs e) =>
				{
					menu.PlacementTarget = more;
					menu.Placement = PlacementMode.Top;
					menu.IsOpen = true;
				};
				Items.Children.Add(more);
			}
			if (list.Count == 0)
			{
				Items.Children.Add(DockButton("+", null, () =>
				{
					C.Toggle(force: true);
				}, "打开面板，可拖入软件固定"));
			}
		}
		AddQuickButtons();
		if (dock.ShowSettings && !dock.Consolidate)
		{
			AddSystemButton("glassboard://dock/settings", DockButton("⚙", null, () =>
			{
				C.Settings();
			}, "设置"), () =>
			{
				C.Settings();
			});
		}
		if (dock.ShowRunningList)
		{
			AddRunningButton();
		}
		ArrangeDockItems();
		Width = Math.Min(rect.Width - 20.0, dock.WidthFor((num == 0) ? 1 : num2));
		double num4 = dock.IconSize + 16.0 + (double)(dock.ShowLabels ? 18 : 0);
		double num5 = Math.Min(1.6, 1.0 + (dock.Zoom - 1.0) * MotionLevel);
		Height = 28.0 + num4 + dock.Spacing * 2.0 + Math.Max(0.0, num4 * (num5 - 1.0) * 0.65 + 5.0 * MotionLevel - dock.Spacing) * 2.0;
		double num6;
		if (dock.Alignment != 0)
		{
			num6 = ((dock.Alignment == 2) ? (rect.Right - Width - 10.0) : (rect.Left + (rect.Width - Width) / 2.0));
		}
		else
		{
			num6 = rect.Left + 10.0;
		}
		PlacementOptions placement = C.Prefs.Placement;
		if (placement.DockLeft.HasValue && !placement.DockCenter.HasValue)
		{
			placement.DockCenter = placement.DockLeft.Value + Width / 2.0;
		}
		Left = DockAnchorRules.Left(placement.DockLeft.HasValue ? placement.DockCenter : ((double?)null), Width, num6 + dock.Offset, rect.Left, rect.Width);
		// 未手动拖过 Dock 时贴工作区底边；settledTop 锁定首次贴底结果，
		// 避免任务栏隐藏/显示引起的工作区变化让 Dock 上下漂移。
		double defaultTop = settledTop ?? (rect.Bottom - Height - C.Prefs.DockGap);
		Top = FeatureRules.Position(C.Prefs.Placement.DockTop ?? defaultTop, rect.Top, rect.Height, Height);
		settledTop = Top;
		shownTop = Top;
		if (dock.AutoHide)
		{
			autoTimer.Start();
		}
		else
		{
			autoTimer.Stop();
		}
		GlassPercent = dock.OpacityPercent;
		if (capture)
		{
			Backdrop(C.Prefs.Light);
		}
		else
		{
			SetGlassOpacity(GlassPercent);
			foreach (Border item3 in from b in Root.Children.OfType<Border>()
				where b.Tag as string == "backdrop"
				select b)
			{
				RectangleGeometry clip = (RectangleGeometry)(item3.Clip = new RectangleGeometry(new Rect(0.0, 0.0, Math.Max(1.0, Width - 28.0), Math.Max(1.0, Height - 28.0)), dock.Radius, dock.Radius));
				if (item3.Child is Border border)
				{
					border.Clip = clip;
					border.CornerRadius = Surface.CornerRadius;
					border.Effect = new BlurEffect
					{
						Radius = MaterialBlur,
						RenderingBias = RenderingBias.Performance
					};
				}
			}
		}
		System.Windows.Media.Color color = Accent(dock.Accent);
		Surface.BorderBrush = ((GlassPercent == 0.0) ? ((System.Windows.Media.Brush)System.Windows.Media.Brushes.Transparent) : ((System.Windows.Media.Brush)new LinearGradientBrush(System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 160.0 * EdgeStrength), color.R, color.G, color.B), System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 30.0 * EdgeStrength), byte.MaxValue, byte.MaxValue, byte.MaxValue), 90.0)));
		UpdateRunning();
		if (!capture)
		{
			AnimateDockLayout(left, width);
		}
	}

	private void UpdateAutoHide()
	{
		ReleaseCancelledExternalDrag();
		if (!C.GlassSuppressed && source != null && IsVisible)
		{
			System.Drawing.Point position = System.Windows.Forms.Cursor.Position;
			System.Windows.Point point = source.CompositionTarget.TransformFromDevice.Transform(new System.Windows.Point(position.X, position.Y));
			double bottom = dockScreenArea.Bottom;
			bool over = !renderedHidden && point.X >= Left && point.X <= Left + Width && point.Y >= shownTop && point.Y <= shownTop + Height;
			bool edge = point.X >= Left - 12.0 && point.X <= Left + Width + 12.0 && point.Y >= bottom - C.Prefs.Dock.EdgeRange;
			bool flag = (ContextMenu != null && ContextMenu.IsOpen) || Items.Children.OfType<System.Windows.Controls.Button>().Any((System.Windows.Controls.Button b) => b.ContextMenu != null && b.ContextMenu.IsOpen);
			bool flag2 = hidePolicy.Update(DateTime.UtcNow, C.Prefs.Dock.AutoHide, over, edge, ((dragging || DockInputBusy || externalDockDrag) | flag) || C.UiBusy || C.Board.IsVisible, C.Prefs.Dock.HideDelay);
			if (flag2 != renderedHidden)
			{
				renderedHidden = flag2;
				double target = (flag2 ? (bottom - 20.0) : shownTop);
				AnimateDockVisibility(flag2, target);
			}
		}
	}

	private void UpdateRunning()
	{
		// 活动胶囊：当前前台窗口归属的入口显示长条，其余运行入口显示圆点。
		IntPtr foreground = RunningWindows.EffectiveForeground();
		string fgPath = (foreground == IntPtr.Zero ? "" : RunningWindows.PathOfWindow(foreground));
		foreach (System.Windows.Controls.Button item in Items.Children.OfType<System.Windows.Controls.Button>())
		{
			Entry entry = item.Tag as Entry;
			Grid grid = item.Content as Grid;
			if (entry == null || grid == null)
			{
				continue;
			}
			List<RunningWindow> ws = RunningWindows.For(entry);
			bool isRunning = ws.Count > 0;
			bool isActive = false;
			if (isRunning && foreground != IntPtr.Zero)
			{
				foreach (RunningWindow w in ws)
				{
					if (w.Handle == foreground)
					{
						isActive = true;
						break;
					}
				}
				// 前台事件可能领先窗口快照 1 秒左右：句柄不在快照里时按进程路径兜底，
				// 保证“点一下刚打开的窗口”胶囊立即跟上（不影响圆点/布局）。
				if (!isActive && fgPath.Length > 0)
				{
					isActive = RunningWindows.MatchesPath(entry, fgPath);
				}
			}
			foreach (Border item2 in from b in grid.Children.OfType<Border>()
				where b.Tag as string == "running"
				select b)
			{
				item2.Visibility = ((!C.Prefs.Dock.ShowRunning || !isRunning || isActive) ? Visibility.Collapsed : Visibility.Visible);
			}
			foreach (Border item3 in from b in grid.Children.OfType<Border>()
				where b.Tag as string == "active"
				select b)
			{
				item3.Visibility = ((C.Prefs.Dock.ShowRunning && isActive) ? Visibility.Visible : Visibility.Collapsed);
			}
			// 运行指示器诊断：状态变化才打日志（0=无 1=圆点 2=活动胶囊），日志不含窗口标题等敏感内容
			int newState = isActive ? 2 : (isRunning ? 1 : 0);
			if (RenderDiagnostics.Verbose)
			{
				if (!runState.TryGetValue(entry.Path, out int oldState) || oldState != newState)
				{
					runState[entry.Path] = newState;
					RenderDiagnostics.Trace("RUN state " + (newState == 0 ? "none" : (newState == 1 ? "dot" : "active")) + " entry=" + entry.Path + " windows=" + ws.Count + " fgHwnd=" + foreground.ToInt64() + " fgPath=" + fgPath);
				}
			}
		}
	}

	private static System.Windows.Media.Color Accent(int index)
	{
		return (new System.Windows.Media.Color[6]
		{
			System.Windows.Media.Color.FromRgb(121, 185, 250),
			System.Windows.Media.Color.FromRgb(183, 149, 245),
			System.Windows.Media.Color.FromRgb(105, 218, 183),
			System.Windows.Media.Color.FromRgb(242, 193, 104),
			System.Windows.Media.Color.FromRgb(244, 150, 178),
			System.Windows.Media.Color.FromRgb(194, 203, 217)
		})[index];
	}

	// Dock 图标悬停提示：纯文字气泡，底色/边框/透明度跟随当前 Dock 玻璃样式
	private System.Windows.Controls.ToolTip DockTip(string text)
	{
		(System.Windows.Media.Color bg, System.Windows.Media.Color ink, System.Windows.Media.Color line, System.Windows.Media.Color hover) = MenuTheme.Palette(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
		border.SetValue(Border.BackgroundProperty, new SolidColorBrush(bg));
		border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(line));
		border.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
		border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8.0));
		border.SetValue(Border.PaddingProperty, new Thickness(12.0, 6.0, 12.0, 6.0));
		FrameworkElementFactory label = new FrameworkElementFactory(typeof(TextBlock));
		label.SetValue(TextBlock.TextProperty, text);
		label.SetValue(TextBlock.FontSizeProperty, 12.5);
		label.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(ink));
		border.AppendChild(label);
		return new System.Windows.Controls.ToolTip
		{
			Placement = PlacementMode.Top,
			VerticalOffset = -4.0,
			Template = new ControlTemplate(typeof(System.Windows.Controls.ToolTip))
			{
				VisualTree = border
			}
		};
	}

	// 通知中心回调（UI 线程）：点亮/熄灭入口右上角红点；新通知时触发一次柔光+弹跳。
	// on 表示“有未处理通知”，与通知是否已读是两回事——系统不保证未读数可靠，故只显示红点。
	public void NotifyBadge(string path, bool on, bool pulse)
	{
		notifyOn[path] = on;
		if (notifyBadges.TryGetValue(path, out Border badge))
		{
			badge.Visibility = (on ? Visibility.Visible : Visibility.Collapsed);
		}
		if (on && pulse && notifyIcons.TryGetValue(path, out FrameworkElement icon))
		{
			RunNotifyPulse(icon);
		}
	}

	// 一次性通知动画：图标轻微弹跳 + 柔和外发光后恢复原状。不循环、不持续闪烁；
	// 只作用在图标元素自身的 RenderTransform/Effect 上，不改按钮命中区域与布局。
	private void RunNotifyPulse(FrameworkElement icon)
	{
		try
		{
			if (!MotionEnabled)
			{
				// 动画关闭时仅亮徽标（NotifyBadge 已处理），不播弹跳/发光
				return;
			}
			System.Windows.Media.Effects.DropShadowEffect glow = new System.Windows.Media.Effects.DropShadowEffect
			{
				Color = System.Windows.Media.Color.FromArgb(byte.MaxValue, byte.MaxValue, 96, 96),
				BlurRadius = 0.0,
				ShadowDepth = 0.0,
				Opacity = 0.0
			};
			icon.Effect = glow;
			DoubleAnimationUsingKeyFrames blur = new DoubleAnimationUsingKeyFrames
			{
				Duration = TimeSpan.FromMilliseconds(760.0)
			};
			blur.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
			blur.KeyFrames.Add(new LinearDoubleKeyFrame(18.0, KeyTime.FromPercent(0.4)));
			blur.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0)));
			DoubleAnimationUsingKeyFrames glowOpacity = new DoubleAnimationUsingKeyFrames
			{
				Duration = TimeSpan.FromMilliseconds(760.0)
			};
			glowOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
			glowOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromPercent(0.4)));
			glowOpacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0)));
			glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, blur);
			glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, glowOpacity);
			// 弹跳比发光短：发光结束再摘 Effect，避免把发光中途剪掉
			blur.Completed += (object s, EventArgs e) =>
			{
				if (ReferenceEquals(icon.Effect, glow))
				{
					icon.Effect = null;
				}
			};
			TranslateTransform bounce = new TranslateTransform();
			icon.RenderTransform = bounce;
			DoubleAnimationUsingKeyFrames y = new DoubleAnimationUsingKeyFrames
			{
				Duration = TimeSpan.FromMilliseconds(420.0)
			};
			y.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
			y.KeyFrames.Add(new EasingDoubleKeyFrame(-7.0, KeyTime.FromPercent(0.45), new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			}));
			y.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0), new CubicEase
			{
				EasingMode = EasingMode.EaseIn
			}));
			y.Completed += (object s, EventArgs e) =>
			{
				if (ReferenceEquals(icon.RenderTransform, bounce))
				{
					icon.RenderTransform = null;
				}
			};
			bounce.BeginAnimation(TranslateTransform.YProperty, y);
		}
		catch
		{
			// 动画失败只影响视觉效果，通知徽标本身已点亮
		}
	}

	// 后台图标回填：把固定应用按钮的占位符（□）换成真图标，并同步通知红点动画的图标引用
	private void DockSetIcon(Entry entry, System.Windows.Controls.Button button, ImageSource source)
	{
		if (source == null || entry == null || button == null || button.Parent == null)
		{
			return;
		}
		if (button.Content is Grid grid && grid.Children.Count > 0 && grid.Children[0] is StackPanel sp && sp.Children.Count > 0)
		{
			System.Windows.Controls.Image image = new System.Windows.Controls.Image
			{
				Source = source,
				Width = C.Prefs.Dock.IconSize,
				Height = C.Prefs.Dock.IconSize,
				Stretch = Stretch.Uniform
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
			// WPF 的 Children[i]=x 赋值会抛「索引已经在使用」；必须先移除再插入。
			sp.Children.RemoveAt(0);
			sp.Children.Insert(0, image);
			notifyIcons[entry.Path] = image;
		}
	}

	private System.Windows.Controls.Button DockButton(string text, ImageSource icon, Action action, string tip, Entry entry = null, string glyphKind = null)
	{
		DockOptions o = C.Prefs.Dock;
		double iconSize = o.IconSize;
		Popup previewPopup = null;
		// 点击图标后抑制悬浮预览，直到鼠标离开再回来——否则重复点击时鼠标停在
		// 图标上，450ms 计时器到点又会弹出标题，看起来像"标题常驻"。
		bool previewSuppressed = false;
		System.Windows.Controls.Button button = Button("", (object s, RoutedEventArgs e) =>
		{
			if (!dockIconDragging && !DockDragSuppresses(s as FrameworkElement))
			{
				ClosePreview();
				// 点击图标（启动/切换窗口）时立即关闭本按钮的悬浮预览弹层，
				// 否则窗口打开后弹层还挂在屏幕上。
				if (previewPopup != null)
				{
					previewPopup.IsOpen = false;
					previewPopup.Child = null;
					previewPopup = null;
				}
				previewSuppressed = true;
				// 点击后立即关掉文字气泡：鼠标若停在图标上，ToolTip 服务不会自动收起，
				// 会出现"标题常驻"。禁用后由 MouseLeave 恢复。
				if (s is System.Windows.Controls.Button tipButton)
				{
					ToolTipService.SetIsEnabled(tipButton, false);
				}
				action();
			}
		}, iconSize + 16.0);
		button.Height = iconSize + 16.0 + (double)(o.ShowLabels ? 18 : 0);
		button.Margin = new Thickness(o.Spacing / 2.0, 0.0, o.Spacing / 2.0, 0.0);
		// 悬停显示纯文字提示（跟随 Dock 玻璃样式）
		button.ToolTip = DockTip(tip);
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(Border));
		frameworkElementFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, byte.MaxValue, byte.MaxValue, byte.MaxValue)));
		FrameworkElementFactory frameworkElementFactory2 = new FrameworkElementFactory(typeof(ContentPresenter));
		frameworkElementFactory2.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
		frameworkElementFactory2.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		frameworkElementFactory.AppendChild(frameworkElementFactory2);
		button.Template = new ControlTemplate(typeof(System.Windows.Controls.Button))
		{
			VisualTree = frameworkElementFactory
		};
		FrameworkElement element;
		if (glyphKind != null)
		{
			element = SystemGlyph(glyphKind, o);
		}
		else if (text == "▦")
		{
			// 启动台：用户对 "apps" 的自定义覆盖优先，其次定制 PNG，最后默认图标
			bool appsOverride = (C.Prefs.SystemIconGlyphs != null && C.Prefs.SystemIconGlyphs.ContainsKey("apps")) || (C.Prefs.SystemIconImages != null && C.Prefs.SystemIconImages.ContainsKey("apps"));
			if (appsOverride)
			{
				element = SystemGlyph("apps", o);
			}
			else
			{
				ImageSource launchpad = CustomIcons.Launchpad();
				if (launchpad != null)
				{
					System.Windows.Controls.Image image2 = new System.Windows.Controls.Image
					{
						Source = launchpad,
						Width = iconSize,
						Height = iconSize,
						Stretch = Stretch.Uniform
					};
					RenderOptions.SetBitmapScalingMode(image2, BitmapScalingMode.HighQuality);
					element = image2;
				}
				else
				{
					element = SystemGlyph("apps", o);
				}
			}
		}
		else if (text == "computer")
		{
			// 此电脑：默认直接用系统官方图标；用户右键更换过图标时尊重自定义
			bool computerOverridden = (C.Prefs.SystemIconImages != null && C.Prefs.SystemIconImages.ContainsKey("computer")) || (C.Prefs.SystemIconGlyphs != null && C.Prefs.SystemIconGlyphs.ContainsKey("computer"));
			ImageSource official = computerOverridden ? null : Native.MyComputerIcon();
			if (official != null)
			{
				System.Windows.Controls.Image computerImage = new System.Windows.Controls.Image
				{
					Source = official,
					Width = iconSize,
					Height = iconSize,
					Stretch = Stretch.Uniform
				};
				RenderOptions.SetBitmapScalingMode(computerImage, BitmapScalingMode.HighQuality);
				element = computerImage;
			}
			else
			{
				element = SystemGlyph("computer", o);
			}
		}
		else if (text == "recycle")
		{
			// 回收站：默认直接用系统官方图标；用户右键更换过图标时尊重自定义
			bool recycleOverridden = (C.Prefs.SystemIconImages != null && C.Prefs.SystemIconImages.ContainsKey("recycle")) || (C.Prefs.SystemIconGlyphs != null && C.Prefs.SystemIconGlyphs.ContainsKey("recycle"));
			ImageSource official = recycleOverridden ? null : Native.RecycleIcon();
			if (official != null)
			{
				System.Windows.Controls.Image recycleImage = new System.Windows.Controls.Image
				{
					Source = official,
					Width = iconSize,
					Height = iconSize,
					Stretch = Stretch.Uniform
				};
				RenderOptions.SetBitmapScalingMode(recycleImage, BitmapScalingMode.HighQuality);
				element = recycleImage;
			}
			else
			{
				element = SystemGlyph("recycle", o);
			}
		}
		else if (text == "⚙" || text == "control" || text == "power" || text == "wifi")
		{
			element = SystemGlyph(text switch
			{
				"⚙" => "settings", 
				"control" => "control", 
				"power" => "power", 
				_ => "wifi", 
			}, o);
		}
		else if (icon == null)
		{
			element = new TextBlock
			{
				Text = (text ?? "□"),
				FontSize = iconSize * 0.72,
				Width = iconSize,
				Height = iconSize,
				TextAlignment = TextAlignment.Center,
				Foreground = TextBrush,
				VerticalAlignment = VerticalAlignment.Center
			};
		}
		else
		{
			System.Windows.Controls.Image image = new System.Windows.Controls.Image
			{
				Source = icon,
				Width = iconSize,
				Height = iconSize,
				Stretch = Stretch.Uniform
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
			element = image;
		}
		StackPanel stackPanel = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center
		};
		stackPanel.Children.Add(element);
		if (o.ShowLabels)
		{
			stackPanel.Children.Add(new TextBlock
			{
				Text = text switch
				{
					"···" => "更多", 
					"⚙" => "设置", 
					"▦" => "应用", 
					"computer" => "此电脑", 
					"control" => "控制中心", 
					_ => tip, 
				},
				FontSize = 10.0,
				Foreground = MutedBrush,
				TextAlignment = TextAlignment.Center,
				TextTrimming = TextTrimming.CharacterEllipsis,
				MaxWidth = iconSize + 12.0,
				Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
			});
		}
		Grid grid2 = new Grid
		{
			IsHitTestVisible = false
		};
		grid2.Children.Add(stackPanel);
		if (entry != null)
		{
			button.Tag = entry;
			// 运行圆点：无标签时用 -2 底部负边距把圆点放到图标下缘外侧（Grid 高度不变，不影响布局与点击区）；
			// 显示标签时标签自带 4px 上边距，圆点贴图标底边正好落在该间隙里。
			double runDotMarginBottom = (o.ShowLabels ? 0.0 : -2.0);
			grid2.Children.Add(new Border
			{
				Tag = "running",
				Width = RunDotWidth,
				Height = RunDotHeight,
				CornerRadius = new CornerRadius(RunDotRadius),
				Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(135, 205, byte.MaxValue)),
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Bottom,
				Margin = new Thickness(0.0, 0.0, 0.0, runDotMarginBottom),
				Visibility = Visibility.Collapsed
			});
			// 活动窗口指示条：当前前台窗口所属应用显示长胶囊（覆盖圆点），颜色跟随 Dock 强调色
			grid2.Children.Add(new Border
			{
				Tag = "active",
				Width = RunActiveWidth,
				Height = RunActiveHeight,
				CornerRadius = new CornerRadius(RunActiveRadius),
				Background = new SolidColorBrush(Accent(C.Prefs.Dock.Accent)),
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Bottom,
				Margin = new Thickness(0.0, 0.0, 0.0, runDotMarginBottom),
				Visibility = Visibility.Collapsed
			});
			if (RenderDiagnostics.Verbose)
			{
				// 指示器创建诊断：确认圆点/胶囊进了可视树且尺寸正常
				RenderDiagnostics.Trace("RUN marker created entry=" + entry.Path + " dot=" + RunDotWidth + "x" + RunDotHeight + " capsule=" + RunActiveWidth + "x" + RunActiveHeight + " dotMarginBottom=" + runDotMarginBottom + " iconSize=" + iconSize);
			}
			// 通知红点：右上角 9×9，只表示“有未处理通知”，不显示数字（无法可靠获取未读数时不伪造）
			Border notifyBadge = new Border
			{
				Tag = "notify",
				Width = 9.0,
				Height = 9.0,
				CornerRadius = new CornerRadius(4.5),
				Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(byte.MaxValue, 64, 64)),
				BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue)),
				BorderThickness = new Thickness(1.0),
				HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0.0, 1.0, 3.0, 0.0),
				Visibility = Visibility.Collapsed
			};
			grid2.Children.Add(notifyBadge);
			notifyBadges[entry.Path] = notifyBadge;
			notifyIcons[entry.Path] = element;
			if (notifyOn.TryGetValue(entry.Path, out bool on) && on)
			{
				notifyBadge.Visibility = Visibility.Visible;
			}
		}
		button.Content = grid2;
		ScaleTransform scale = new ScaleTransform(1.0, 1.0);
		TranslateTransform lift = new TranslateTransform();
		TransformGroup transformGroup = new TransformGroup();
		transformGroup.Children.Add(scale);
		transformGroup.Children.Add(lift);
		button.RenderTransform = transformGroup;
		button.RenderTransformOrigin = new System.Windows.Point(0.5, 0.65);
		double zoomTarget = 1.0;
		Action<bool> animate = (bool hover) =>
		{
			if (dockPressActive)
			{
				// 按下/点击期间冻结悬停动画，点击图标不再“跳动”
				return;
			}
			zoomTarget = (hover ? Math.Min(1.6, 1.0 + (o.Zoom - 1.0) * MotionLevel) : 1.0);
			if (MotionEnabled)
			{
				CubicEase easingFunction = new CubicEase
				{
					EasingMode = EasingMode.EaseOut
				};
				scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(zoomTarget, TimeSpan.FromMilliseconds(180.0))
				{
					EasingFunction = easingFunction
				});
				scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(zoomTarget, TimeSpan.FromMilliseconds(180.0))
				{
					EasingFunction = easingFunction
				});
				lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(hover ? (-5.0 * MotionLevel) : 0.0, TimeSpan.FromMilliseconds(180.0))
				{
					EasingFunction = easingFunction
				});
			}
		};
		button.MouseEnter += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			if (!DockInputBusy)
			{
				animate(obj: true);
			}
		};
		button.MouseLeave += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			animate(obj: false);
			// 鼠标离开后恢复文字气泡（点击时被禁用防常驻）。
			ToolTipService.SetIsEnabled(button, true);
		};
		if (entry != null)
		{
			// 悬浮预览：仅应用图标注册——悬停 450ms 后在图标上方弹出窗口标题列表；
			// 系统内置图标只用普通名称提示（ToolTip），不弹卡片。
			DispatcherTimer previewTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(450.0)
			};
			DispatcherTimer leaveTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(220.0)
			};
			previewPopup = null;
			bool previewKeep = false;
			Action closePreview = delegate
			{
				previewTimer.Stop();
				leaveTimer.Stop();
				previewKeep = false;
				if (previewPopup != null)
				{
					watchPopups.Remove(previewPopup);
					watchAway.Remove(previewPopup);
					if (watchPopups.Count == 0)
					{
						StopWatchTimer();
					}
					previewPopup.IsOpen = false;
					previewPopup.Child = null;
					previewPopup = null;
				}
			};
			// Refresh 会清空 Items 重建按钮：旧按钮销毁时必须同步关掉它的弹层，
			// 否则孤儿 Popup 会永远悬浮在屏幕上（悬浮标题一直显示）。
			button.Unloaded += delegate
			{
				closePreview();
			};
			leaveTimer.Tick += delegate
			{
				leaveTimer.Stop();
				if (!previewKeep)
				{
					closePreview();
				}
			};
			previewTimer.Tick += delegate
			{
				previewTimer.Stop();
				// 用户若在等待期间已离开按钮（leaveTimer 先跑完且弹层尚未创建），
				// 到点仍创建 Popup 会变成永远悬浮的孤儿——这里直接放弃弹出；
				// 点击过图标后同样不弹，直到鼠标离开再回来。
				if (!button.IsMouseOver || previewSuppressed)
				{
					return;
				}
				ShowWindowPreview(button, entry, text, tip, ref previewPopup, closePreview, delegate(bool k)
				{
					previewKeep = k;
				});
			};
			button.MouseEnter += delegate
			{
				if (!DockInputBusy)
				{
					previewSuppressed = false;
					leaveTimer.Stop();
					previewTimer.Stop();
					previewTimer.Start();
				}
			};
			button.MouseLeave += delegate
			{
				previewSuppressed = false;
				previewTimer.Stop();
				leaveTimer.Stop();
				leaveTimer.Start();
			};
			if (entry != null)
			{
				BindDockIcon(button, entry);
			}
		}
		return button;
	}

	private void ShowWindowPreview(System.Windows.Controls.Button button, Entry entry, string text, string tip, ref Popup popup, Action close, Action<bool> setKeep)
	{
		setKeep(false);
		// 防重复打开：若上一轮弹层未正常关闭（孤儿），先强制回收再新建
		if (popup != null)
		{
			watchPopups.Remove(popup);
			watchAway.Remove(popup);
			popup.IsOpen = false;
			popup.Child = null;
			popup = null;
		}
		List<RunningWindow> wins = new List<RunningWindow>();
		if (entry != null)
		{
			try
			{
				RunningWindows.Scan();
			}
			catch (Exception)
			{
			}
			wins = RunningWindows.For(entry);
			if (wins.Count == 0)
			{
				return;
			}
			// 单窗口应用不弹标题列表（用户要求：要么单窗口就不显示标题），
			// 悬停文字气泡照常，多窗口才显示可点击的窗口标题列表。
			if (wins.Count < 2)
			{
				return;
			}
		}
		(System.Windows.Media.Color bg, System.Windows.Media.Color ink, System.Windows.Media.Color line, System.Windows.Media.Color hover) = MenuTheme.Palette(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		Border container = new Border
		{
			Background = new SolidColorBrush(bg),
			BorderBrush = new SolidColorBrush(line),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(14.0),
			Padding = new Thickness(8.0)
		};
		StackPanel rows = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		container.Child = rows;
		if (entry != null)
		{
			// 纯标题列表：不再抓取窗口画面，只显示窗口标题内容，主题色取自 dock 玻璃样式
			rows.Orientation = System.Windows.Controls.Orientation.Vertical;
			foreach (RunningWindow w in wins.Take(8))
			{
				string title = string.IsNullOrWhiteSpace(w.Title) ? entry.Name : w.Title;
				Border row = new Border
				{
					CornerRadius = new CornerRadius(8.0),
					Padding = new Thickness(14.0, 7.0, 14.0, 7.0),
					Margin = new Thickness(0.0, 2.0, 0.0, 2.0),
					Background = System.Windows.Media.Brushes.Transparent
				};
				row.Child = new TextBlock
				{
					Text = title,
					FontSize = 12.5,
					Foreground = new SolidColorBrush(ink),
					MaxWidth = 240.0,
					TextTrimming = TextTrimming.CharacterEllipsis,
					TextAlignment = TextAlignment.Left
				};
				row.MouseEnter += delegate
				{
					row.Background = new SolidColorBrush(hover);
				};
				row.MouseLeave += delegate
				{
					row.Background = System.Windows.Media.Brushes.Transparent;
				};
				IntPtr handle = w.Handle;
				row.PreviewMouseLeftButtonUp += delegate
				{
					close();
					// 与 dock 图标统一：该窗口已在前台时再点=最小化，否则提到最上层。
					if (RunningWindows.IsForeground(handle))
					{
						RunningWindows.Minimize(handle);
					}
					else
					{
						ActivateTask(handle);
					}
				};
				rows.Children.Add(row);
			}
		}
		Popup p = new Popup
		{
			PlacementTarget = button,
			Placement = PlacementMode.Custom,
			CustomPopupPlacementCallback = delegate(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
			{
				double x = targetSize.Width / 2.0 - popupSize.Width / 2.0;
				return new CustomPopupPlacement[2]
				{
					new CustomPopupPlacement(new System.Windows.Point(x, -popupSize.Height - 8.0), PopupPrimaryAxis.Vertical),
					new CustomPopupPlacement(new System.Windows.Point(x, targetSize.Height + 8.0), PopupPrimaryAxis.Vertical)
				};
			},
			StaysOpen = true,
			AllowsTransparency = true,
			PopupAnimation = PopupAnimation.None,
			Child = container
		};
		// Popup 自身不接收 Direct 鼠标事件（AllowsTransparency 弹层命中测试落在 Child 上），
		// 所以 keep/close 必须挂在真正渲染内容的 container 上，否则弹层时灵时不灵。
		container.MouseEnter += delegate
		{
			setKeep(true);
		};
		container.MouseLeave += delegate
		{
			close();
		};
		popup = p;
		watchPopups.Add(p);
		watchAway[p] = 0;
		EnsureWatchTimer();
		p.IsOpen = true;
	}

	private FrameworkElement SystemGlyph(string kind, DockOptions o)
	{
		// 系统入口图标统一走内置图标包（IconPacks：简洁线条 / 圆润填充 / 强调徽章），
		// 风格与强调色在设置中可切换，主题色跟随当前设置。
		// 支持右键"更换图标"覆盖：上传的自定义图片优先，其次是其他内置图标样式。
		if (C.Prefs.SystemIconImages != null && C.Prefs.SystemIconImages.TryGetValue(kind, out string imagePath) && !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
		{
			try
			{
				FileInfo fileInfo = new FileInfo(imagePath);
				string cacheKey = fileInfo.FullName + "/" + fileInfo.Length + "/" + fileInfo.LastWriteTimeUtc.Ticks;
				if (!customSystemIconCache.TryGetValue(cacheKey, out ImageSource cachedImage))
				{
					BitmapImage bitmapImage = new BitmapImage();
					bitmapImage.BeginInit();
					bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
					bitmapImage.UriSource = new Uri(imagePath);
					bitmapImage.DecodePixelWidth = 256;
					bitmapImage.EndInit();
					bitmapImage.Freeze();
					cachedImage = bitmapImage;
					customSystemIconCache[cacheKey] = cachedImage;
				}
				// 主题自适应：深色剪影图标在深色 Dock 上不可见，自动反白（反之亦然）
				string adaptiveKey = cacheKey + "/a" + (LightTheme ? 1 : 0);
				if (!customSystemIconCache.TryGetValue(adaptiveKey, out ImageSource adaptedImage))
				{
					adaptedImage = AdaptIcon(cachedImage as BitmapSource, LightTheme);
					customSystemIconCache[adaptiveKey] = adaptedImage;
				}
				System.Windows.Controls.Image image = new System.Windows.Controls.Image
				{
					Source = adaptedImage,
					Width = o.IconSize,
					Height = o.IconSize,
					Stretch = Stretch.Uniform
				};
				RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
				return image;
			}
			catch
			{
			}
		}
		if (C.Prefs.SystemIconGlyphs != null && C.Prefs.SystemIconGlyphs.TryGetValue(kind, out string replaced) && SystemIconActions.IsValid(replaced))
		{
			kind = replaced;
		}
		// 运行列表入口默认图标：用户定制的 2.5d 图标（嵌入资源），不属于任何风格包——
		// 切换风格保持原样，除非用户主动更换/上传了图标。
		if (kind == "running")
		{
			ImageSource builtin = CustomIcons.Running();
			if (builtin != null)
			{
				System.Windows.Controls.Image runningImage = new System.Windows.Controls.Image
				{
					Source = AdaptIcon(builtin as BitmapSource, LightTheme),
					Width = o.IconSize * 1.05,
					Height = o.IconSize * 1.05,
					Stretch = Stretch.Uniform
				};
				RenderOptions.SetBitmapScalingMode(runningImage, BitmapScalingMode.HighQuality);
				return runningImage;
			}
		}
		return IconPacks.Glyph(kind, o.IconPack, o.Accent, TextBrush, o.IconSize * 1.05);
	}

	private static readonly Dictionary<string, ImageSource> customSystemIconCache = new Dictionary<string, ImageSource>();

	// 剪影图标主题自适应：黑底 Dock 上把深色剪影反白、浅色主题上把浅色剪影加深；彩色图标原样保留
	private ImageSource AdaptIcon(BitmapSource source, bool lightTheme)
	{
		try
		{
			if (source == null || source.Format.BitsPerPixel != 32)
			{
				return source;
			}
			int width = source.PixelWidth;
			int height = source.PixelHeight;
			int stride = width * 4;
			byte[] pixels = new byte[stride * height];
			source.CopyPixels(pixels, stride, 0);
			long total = 0L;
			long dark = 0L;
			long light = 0L;
			double saturationSum = 0.0;
			for (int i = 0; i < pixels.Length; i += 4)
			{
				byte b = pixels[i];
				byte g = pixels[i + 1];
				byte r = pixels[i + 2];
				if (pixels[i + 3] < 10)
				{
					continue;
				}
				total++;
				int max = Math.Max(r, Math.Max(g, b));
				int min = Math.Min(r, Math.Min(g, b));
				int luminance = (r * 299 + g * 587 + b * 114) / 1000;
				if (luminance < 100)
				{
					dark++;
				}
				if (luminance > 200)
				{
					light++;
				}
				saturationSum += max - min;
			}
			if (total == 0L)
			{
				return source;
			}
			double avgSaturation = saturationSum / (double)total;
			bool darkSilhouette = dark * 10L >= total * 8L;
			bool lightSilhouette = light * 10L >= total * 8L;
			bool needInvert = (avgSaturation < 60.0) && ((!lightTheme && darkSilhouette) || (lightTheme && lightSilhouette));
			if (!needInvert)
			{
				return source;
			}
			for (int j = 0; j < pixels.Length; j += 4)
			{
				if (pixels[j + 3] >= 10)
				{
					pixels[j] = (byte)(255 - pixels[j]);
					pixels[j + 1] = (byte)(255 - pixels[j + 1]);
					pixels[j + 2] = (byte)(255 - pixels[j + 2]);
				}
			}
			WriteableBitmap inverted = new WriteableBitmap(source);
			inverted.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
			inverted.Freeze();
			return inverted;
		}
		catch
		{
			return source;
		}
	}

	private void StopDockLayoutMotion()
	{
		double width = Width;
		double left = Left;
		layoutSerial++;
		BeginAnimation(FrameworkElement.WidthProperty, null);
		BeginAnimation(Window.LeftProperty, null);
		if (!double.IsNaN(width))
		{
			Width = width;
		}
		if (!double.IsNaN(left))
		{
			Left = left;
		}
	}

	private void AnimateDockLayout(double oldLeft, double oldWidth)
	{
		if (!MotionEnabled || !IsVisible || double.IsNaN(oldWidth) || double.IsNaN(oldLeft) || oldWidth <= 0.0 || Math.Abs(oldWidth - Width) < 0.5)
		{
			return;
		}
		int serial = ++layoutSerial;
		TimeSpan timeSpan = TimeSpan.FromMilliseconds(180.0);
		CubicEase easingFunction = new CubicEase
		{
			EasingMode = EasingMode.EaseOut
		};
		DoubleAnimation doubleAnimation = new DoubleAnimation(oldWidth, Width, timeSpan)
		{
			EasingFunction = easingFunction,
			FillBehavior = FillBehavior.Stop
		};
		DoubleAnimation animation = new DoubleAnimation(oldLeft, Left, timeSpan)
		{
			EasingFunction = easingFunction,
			FillBehavior = FillBehavior.Stop
		};
		doubleAnimation.Completed += (object s, EventArgs e) =>
		{
			if (serial == layoutSerial)
			{
				BeginAnimation(FrameworkElement.WidthProperty, null);
				BeginAnimation(Window.LeftProperty, null);
			}
		};
		BeginAnimation(FrameworkElement.WidthProperty, doubleAnimation, HandoffBehavior.SnapshotAndReplace);
		BeginAnimation(Window.LeftProperty, animation, HandoffBehavior.SnapshotAndReplace);
	}

	public void AddDockControls(System.Windows.Controls.ContextMenu menu)
	{
		menu.Items.Add(new Separator());
		System.Windows.Controls.MenuItem movable = new System.Windows.Controls.MenuItem
		{
			Header = "允许自由拖动 Dock",
			IsCheckable = true
		};
		System.Windows.Controls.MenuItem spring = new System.Windows.Controls.MenuItem
		{
			Header = "弹簧弹出效果",
			IsCheckable = true,
			ToolTip = "自动隐藏后弹出时生效，遵循界面动效总开关"
		};
		System.Windows.Controls.MenuItem automatic = new System.Windows.Controls.MenuItem
		{
			Header = "Dock 自动隐藏",
			IsCheckable = true
		};
		menu.Opened += (object s, RoutedEventArgs e) =>
		{
			movable.IsChecked = !C.Prefs.Placement.DockLocked;
			spring.IsChecked = C.Prefs.Dock.SpringReveal;
			automatic.IsChecked = C.Prefs.Dock.AutoHide;
		};
		movable.Click += (object s, RoutedEventArgs e) =>
		{
			C.Prefs.Placement.DockLocked = !movable.IsChecked;
			C.Save();
			Refresh(capture: false);
		};
		spring.Click += (object s, RoutedEventArgs e) =>
		{
			C.Prefs.Dock.SpringReveal = spring.IsChecked;
			C.Save();
		};
		automatic.Click += (object s, RoutedEventArgs e) =>
		{
			C.Prefs.Dock.AutoHide = automatic.IsChecked;
			C.Save();
			Refresh(capture: false);
		};
		menu.Items.Add(movable);
		menu.Items.Add(spring);
		menu.Items.Add(automatic);
		System.Windows.Controls.MenuItem settingsIcon = new System.Windows.Controls.MenuItem
		{
			Header = "显示 Dock 设置图标",
			IsCheckable = true
		};
		menu.Opened += (object s, RoutedEventArgs e) =>
		{
			settingsIcon.IsChecked = C.Prefs.Dock.ShowSettings;
		};
		settingsIcon.Click += (object s, RoutedEventArgs e) =>
		{
			C.Prefs.Dock.ShowSettings = settingsIcon.IsChecked;
			C.Save();
			Refresh(capture: false);
		};
		menu.Items.Add(settingsIcon);
	}

	private void InitializeBackgroundMove()
	{
		Root.Background = System.Windows.Media.Brushes.Transparent;
		Root.PreviewMouseLeftButtonDown += (object s, MouseButtonEventArgs e) =>
		{
			if (!e.Handled && e.ButtonState == MouseButtonState.Pressed && !C.Prefs.Placement.DockLocked && !DockInputBusy)
			{
				DependencyObject dependencyObject = e.OriginalSource as DependencyObject;
				while (dependencyObject != null && dependencyObject != Root)
				{
					if (dependencyObject is System.Windows.Controls.Primitives.ButtonBase || dependencyObject is Thumb || dependencyObject is System.Windows.Controls.Primitives.TextBoxBase)
					{
						return;
					}
					dependencyObject = ((dependencyObject is Visual) ? VisualTreeHelper.GetParent(dependencyObject) : LogicalTreeHelper.GetParent(dependencyObject));
				}
				if (dependencyObject == Root)
				{
					e.Handled = true;
					BeginDockMove();
					try
					{
						DragMove();
					}
					catch (InvalidOperationException)
					{
					}
					finally
					{
						FinishDockMove();
					}
				}
			}
		};
	}

	private void BeginDockMove()
	{
		if (!C.Prefs.Placement.DockLocked)
		{
			StopDockLayoutMotion();
			dragging = true;
			double top = Top;
			revealSerial++;
			BeginAnimation(Window.TopProperty, null);
			Top = top;
			renderedHidden = false;
			hidePolicy = new AutoHidePolicy();
			ResetDockHover();
		}
	}

	private void FinishDockMove()
	{
		if (!dragging)
		{
			return;
		}
		try
		{
			Rect rect = (dockWorkingArea = DesktopWindowPolicy.WorkArea(this));
			dockScreenArea = DesktopWindowPolicy.WorkArea(this, pointer: false, fullScreen: true);
			Left = FeatureRules.Position(Left, rect.Left, rect.Width, Width);
			Top = FeatureRules.Position(Top, rect.Top, rect.Height, Height);
			shownTop = Top;
			C.Prefs.Placement.DockLeft = Left;
			C.Prefs.Placement.DockCenter = Left + Width / 2.0;
			C.Prefs.Placement.DockTop = Top;
			hidePolicy = new AutoHidePolicy();
			C.Save();
			Backdrop(C.Prefs.Light);
		}
		finally
		{
			dragging = false;
		}
	}

	private void AnimateDockVisibility(bool hide, double target)
	{
		double top = Top;
		int serial = ++revealSerial;
		BeginAnimation(Window.TopProperty, null);
		Top = target;
		if (!MotionEnabled)
		{
			return;
		}
		if (!hide && C.Prefs.Dock.SpringReveal)
		{
			double[] array = DockRevealRules.Stops(top, target, MotionLevel, dockWorkingArea.Top);
			DoubleAnimationUsingKeyFrames doubleAnimationUsingKeyFrames = new DoubleAnimationUsingKeyFrames
			{
				Duration = TimeSpan.FromMilliseconds(420.0),
				FillBehavior = FillBehavior.Stop
			};
			doubleAnimationUsingKeyFrames.KeyFrames.Add(new LinearDoubleKeyFrame(array[0], KeyTime.FromTimeSpan(TimeSpan.Zero)));
			doubleAnimationUsingKeyFrames.KeyFrames.Add(new SplineDoubleKeyFrame(array[1], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230.0)), new KeySpline(0.18, 0.72, 0.28, 1.0)));
			doubleAnimationUsingKeyFrames.KeyFrames.Add(new SplineDoubleKeyFrame(array[2], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330.0)), new KeySpline(0.35, 0.0, 0.65, 1.0)));
			doubleAnimationUsingKeyFrames.KeyFrames.Add(new SplineDoubleKeyFrame(array[3], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420.0)), new KeySpline(0.35, 0.0, 0.5, 1.0)));
			doubleAnimationUsingKeyFrames.Completed += (object s, EventArgs e) =>
			{
				if (serial == revealSerial)
				{
					BeginAnimation(Window.TopProperty, null);
				}
			};
			BeginAnimation(Window.TopProperty, doubleAnimationUsingKeyFrames, HandoffBehavior.SnapshotAndReplace);
			return;
		}
		DoubleAnimation doubleAnimation = new DoubleAnimation(top, target, TimeSpan.FromMilliseconds(hide ? 160 : 190))
		{
			EasingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			},
			FillBehavior = FillBehavior.Stop
		};
		doubleAnimation.Completed += (object s, EventArgs e) =>
		{
			if (serial == revealSerial)
			{
				BeginAnimation(Window.TopProperty, null);
			}
		};
		BeginAnimation(Window.TopProperty, doubleAnimation, HandoffBehavior.SnapshotAndReplace);
	}

	private void InitTaskDock()
	{
		AllowDrop = true;
		taskHint = new TextBlock
		{
			FontSize = 10.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(10.0, 2.0, 0.0, 0.0),
			IsHitTestVisible = false,
			Visibility = Visibility.Collapsed
		};
		Root.Children.Add(taskHint);
		taskHintTimer.Tick += (object s, EventArgs e) =>
		{
			taskHintTimer.Stop();
			taskHint.Visibility = Visibility.Collapsed;
		};
		PreviewDragEnter += DockExternalOver;
		PreviewDragOver += DockExternalOver;
		PreviewDragLeave += (object s, System.Windows.DragEventArgs e) =>
		{
			System.Windows.Point position = e.GetPosition(this);
			if (!InsideDock(position))
			{
				ClearExternalDockDrag();
			}
		};
		PreviewDrop += (object s, System.Windows.DragEventArgs e) =>
		{
			if (!DockInputBusy)
			{
				string[] array = DockPayload(e.Data);
				if (array.Length != 0)
				{
					e.Handled = true;
					e.Effects = System.Windows.DragDropEffects.None;
					try
					{
						string[] files = (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? ((string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)) : null);
						System.Windows.Controls.Button recycle = ((files == null || files.Length == 0) ? null : Items.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault((System.Windows.Controls.Button b) => string.Equals(ItemKey(b), RecycleBin.Clsid, StringComparison.OrdinalIgnoreCase) || string.Equals(ItemKey(b), "glassboard://dock/recycle", StringComparison.OrdinalIgnoreCase)));
						if (recycle != null)
						{
							System.Windows.Point point = e.GetPosition(recycle);
							if (point.X >= 0.0 && point.Y >= 0.0 && point.X <= recycle.ActualWidth && point.Y <= recycle.ActualHeight)
							{
								ClearExternalDockDrag();
								int deleted = C.DeleteToRecycleBin(files);
								ShowTaskHint((deleted > 0) ? ("已删除 " + deleted + " 项到回收站") : "未找到可删除的文件");
								return;
							}
						}
						DockDropTarget target = ExternalDockTarget(e.GetPosition(Items).X);
						ClearExternalDockDrag();
						DockPinResult dockPinResult = C.PinDockPaths(array, target);
						e.Effects = ((dockPinResult.Paths.Count > 0) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None);
						ShowTaskHint((dockPinResult.Paths.Count > 0) ? ("已放入 Dock" + ((dockPinResult.Skipped > 0) ? " · 已达固定上限" : "")) : "无法固定此入口");
						return;
					}
					catch (Exception ex)
					{
						ClearExternalDockDrag();
						Store.Log(ex);
						ShowTaskHint("添加失败，请重试");
						return;
					}
				}
				ClearExternalDockDrag();
			}
		};
		IsVisibleChanged += (object s, DependencyPropertyChangedEventArgs e) =>
		{
			if (!IsVisible)
			{
				ClearExternalDockDrag();
			}
		};
		Closed += (object s, EventArgs e) =>
		{
			taskHintTimer.Stop();
		};
	}

	private void ShowTaskHint(string text)
	{
		taskHint.Text = text;
		taskHint.Foreground = MutedBrush;
		taskHint.Visibility = Visibility.Visible;
		taskHintTimer.Stop();
		taskHintTimer.Start();
	}

	private void SetTaskMode(bool running)
	{
		if (!DockInputBusy && !externalDockDrag)
		{
			taskWheel.Set(running);
			ApplyTaskMode();
		}
	}

	private void ApplyTaskMode()
	{
		ClosePreview();
		if (taskWheel.Running)
		{
			RunningWindows.Scan();
			ReadTaskGroups();
		}
		ResetDockHover();
		Refresh(capture: false);
		ShowTaskHint(taskWheel.Running ? "正在运行 · 上滚回常用" : "常用软件 · 下滚看运行");
		if (MotionEnabled)
		{
			Items.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(160.0))
			{
				FillBehavior = FillBehavior.Stop
			});
		}
	}

	private static string TaskKey(RunningWindow window)
	{
		string fileName = Path.GetFileName(window.Path ?? "");
		if (!string.IsNullOrEmpty(window.Path) && !fileName.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
		{
			return window.Path;
		}
		return "window:" + window.Handle.ToInt64();
	}

	private void ReadTaskGroups()
	{
		taskGroups = RunningWindows.SnapshotWithBackground().GroupBy(TaskKey, StringComparer.OrdinalIgnoreCase).OrderBy((IGrouping<string, RunningWindow> g) => g.Key, StringComparer.OrdinalIgnoreCase)
			.Select((IGrouping<string, RunningWindow> g) =>
			{
				List<RunningWindow> windows = g.OrderBy((RunningWindow w) => w.Handle.ToInt64()).ToList();
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(windows[0].Path ?? "");
				Entry entry = C.Prefs.Apps.FirstOrDefault((Entry a) => !a.IsFolder && WindowActivationRules.Executables(a).Contains(windows[0].Path, StringComparer.OrdinalIgnoreCase));
				return new TaskGroup
				{
					Key = g.Key,
					Name = ((entry != null) ? entry.Name : ((string.IsNullOrWhiteSpace(fileNameWithoutExtension) || g.Key.StartsWith("window:")) ? windows[0].Title : fileNameWithoutExtension)),
					Windows = windows
				};
			})
			.ToList();
		taskSignature = TaskSignature();
	}

	private string TaskSignature()
	{
		return string.Join("|", from w in RunningWindows.SnapshotWithBackground()
			orderby w.Handle.ToInt64()
			select w.Handle.ToInt64() + ":" + w.Path + ":" + w.Title);
	}

	private void RefreshTaskSnapshot()
	{
		if (taskWheel.Running && !DockInputBusy && !externalDockDrag && !dragging && !C.Board.ModalBusy && (ContextMenu == null || !ContextMenu.IsOpen) && (previewPopup == null || !previewPopup.IsOpen) && !Items.Children.OfType<System.Windows.Controls.Button>().Any((System.Windows.Controls.Button b) => b.IsMouseOver || (b.ContextMenu != null && b.ContextMenu.IsOpen)) && !(taskSignature == TaskSignature()))
		{
			if (taskWheel.Running)
			{
				ReadTaskGroups();
			}
			Refresh(capture: false);
		}
	}

	private void BuildTaskButtons(int shown, int slots)
	{
		List<string> taskOrder = DockLayoutRules.Running(C.Prefs, TaskKeys);
		List<TaskGroup> list = taskGroups.OrderBy((TaskGroup g) => taskOrder.IndexOf("running:" + g.Key)).ToList();
		foreach (TaskGroup item in list.Take(shown))
		{
			TaskGroup selected = item;
			System.Windows.Controls.Button button = DockButton(null, RunningWindows.Icon(item.Windows[0]), () =>
			{
				OpenTask(selected, null);
			}, item.Name + ((item.Windows.Count > 1) ? (" · " + item.Windows.Count + " 个窗口") : ""));
			button.Tag = item;
			BindLayoutButton(button, "running:" + item.Key, item.Name, () =>
			{
				OpenTask(selected, button);
			});
			if (button.Content is Grid { Children: var children })
			{
				children.Add(new Border
				{
					Width = ((item.Windows.Count > 1) ? 12 : 5),
					Height = 4.0,
					CornerRadius = new CornerRadius(2.0),
					Background = Polish.Accent,
					HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Bottom,
					IsHitTestVisible = false
				});
			}
			// 任务按钮右键：定制菜单（打开 / 固定切换 / 退出程序）。不直接复用 DockMenu——
			// 任务轮模式下"从 Dock 移除"只取消固定、图标因程序仍在运行不会消失，造成"点了没反应"。
			Entry entry = null;
			if (item.Windows.Count > 0)
			{
				string p = EntryPathRules.Normalize(item.Windows[0].Path);
				entry = C.Prefs.Apps.FirstOrDefault((Entry a) => EntryPathRules.Normalize(a.Path).Equals(p, StringComparison.OrdinalIgnoreCase))
					?? C.Prefs.Apps.FirstOrDefault((Entry a) => RunningWindows.For(a).Any((RunningWindow w) => w.Handle == item.Windows[0].Handle));
			}
			System.Windows.Controls.ContextMenu menu2 = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
			{
				TaskGroup g2 = item;
				System.Windows.Controls.MenuItem open2 = new System.Windows.Controls.MenuItem
				{
					Header = "打开"
				};
				open2.Click += (object s, RoutedEventArgs e) =>
				{
					menu2.IsOpen = false;
					Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
					{
						OpenTask(g2, button);
					}));
				};
				menu2.Items.Add(open2);
				if (entry != null)
				{
					Entry e2 = entry;
					System.Windows.Controls.MenuItem pin2 = new System.Windows.Controls.MenuItem
					{
						Header = e2.Pinned ? "取消 Dock 固定（运行中仍会显示）" : "固定到 Dock"
					};
					pin2.Click += (object s, RoutedEventArgs e) =>
					{
						menu2.IsOpen = false;
						Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
						{
							C.Pin(e2);
						}));
					};
					menu2.Items.Add(pin2);
				}
				System.Windows.Controls.MenuItem stop2 = new System.Windows.Controls.MenuItem
				{
					Header = "退出程序"
				};
				stop2.Click += (object s, RoutedEventArgs e) =>
				{
					menu2.IsOpen = false;
					Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
					{
						StopTask(g2);
					}));
				};
				menu2.Items.Add(stop2);
			}
			TrackMenu(menu2);
			button.ContextMenu = menu2;
			AttachPreview(button, item);
			Items.Children.Add(button);
		}
		if (taskGroups.Count > shown && slots > 0)
		{
			System.Windows.Controls.Button more = DockButton("···", null, () =>
			{
			}, "更多正在运行的软件");
			System.Windows.Controls.ContextMenu menu = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
			foreach (TaskGroup item2 in list.Skip(shown))
			{
				TaskGroup selected2 = item2;
				System.Windows.Controls.MenuItem menuItem = new System.Windows.Controls.MenuItem
				{
					Header = item2.Name + ((item2.Windows.Count > 1) ? ("（" + item2.Windows.Count + "）") : "")
				};
				menuItem.Click += (object s, RoutedEventArgs e) =>
				{
					menu.IsOpen = false;
					OpenTask(selected2, more);
				};
				menu.Items.Add(menuItem);
			}
			more.Click += (object s, RoutedEventArgs e) =>
			{
				menu.PlacementTarget = more;
				menu.Placement = PlacementMode.Top;
				menu.IsOpen = true;
			};
			more.ContextMenu = menu;
			TrackMenu(menu);
			Items.Children.Add(more);
		}
		if (taskGroups.Count == 0)
		{
			Items.Children.Add(DockButton("—", null, () =>
			{
				SetTaskMode(running: false);
			}, "暂无正在运行的软件 · 点击返回常用"));
		}
	}

	private void OpenTask(TaskGroup group, FrameworkElement owner)
	{
		RunningWindow target = group.Windows.FirstOrDefault((RunningWindow w) => RunningWindows.IsForeground(w.Handle));
		if (target == null)
		{
			// 优先唤醒可见窗口；纯托盘组再退到隐藏窗口
			target = group.Windows.FirstOrDefault((RunningWindow w) => !w.Tray) ?? group.Windows[0];
		}
		if (target == null)
		{
			return;
		}
		if (RunningWindows.IsForeground(target.Handle))
		{
			RunningWindows.Minimize(target.Handle);
			return;
		}
		if (RunningWindows.ActivateHandle(target.Handle))
		{
			C.Board.CollapseSmooth();
			return;
		}
		if (target.Tray)
		{
			// 纯托盘程序（无可见窗口）：先尝试恢复隐藏窗口 / 模拟单击托盘图标唤醒，
			// 都不行才退回重新启动（单实例应用启动后不会弹新窗口，最后手段）。
			string[] names = group.Windows.Select((RunningWindow w) => w.Title).Concat(new string[1] { group.Name }).Concat(group.Windows.Select((RunningWindow w) => System.IO.Path.GetFileNameWithoutExtension(w.Path))).Where((string n) => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			if (RunningWindows.WakeTray(target, names))
			{
				C.Board.CollapseSmooth();
				return;
			}
			try
			{
				Process.Start(target.Path);
			}
			catch
			{
			}
			ShowTaskHint("正在唤醒 " + group.Name);
			return;
		}
		RunningWindows.Scan();
		ReadTaskGroups();
		Refresh(capture: false);
		ShowTaskHint("此窗口已关闭");
	}

	private void StopTask(TaskGroup group)
	{
		foreach (RunningWindow w in group.Windows)
		{
			RunningWindows.Close(w.Handle);
		}
		RunningWindows.Scan();
		ReadTaskGroups();
		Refresh(capture: false);
	}

	private void ActivateTask(IntPtr handle)
	{
		if (RunningWindows.IsForeground(handle))
		{
			RunningWindows.Minimize(handle);
			return;
		}
		if (RunningWindows.ActivateHandle(handle))
		{
			C.Board.CollapseSmooth();
			return;
		}
		RunningWindows.Scan();
		ReadTaskGroups();
		Refresh(capture: false);
		ShowTaskHint("此窗口已关闭");
	}

	private static string[] DockPayload(System.Windows.IDataObject data)
	{
		if (data.GetDataPresent("GlassBoard.Entries"))
		{
			return (data.GetData("GlassBoard.Entries") as string[]) ?? new string[0];
		}
		if (data.GetDataPresent("GlassBoard.Entry"))
		{
			return new string[1] { data.GetData("GlassBoard.Entry") as string };
		}
		if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop))
		{
			return new string[0];
		}
		return (data.GetData(System.Windows.DataFormats.FileDrop) as string[]) ?? new string[0];
	}

	private DockDropTarget ExternalDockTarget(double x)
	{
		List<System.Windows.Controls.Button> list = (from b in Items.Children.OfType<System.Windows.Controls.Button>()
			where ItemKey(b) != null
			select b).ToList();
		return DockDropRules.OnGap(null, list.Select(ItemKey).ToArray(), list.Select((System.Windows.Controls.Button b) => b.TranslatePoint(new System.Windows.Point(b.ActualWidth / 2.0, 0.0), Items).X).ToArray(), x);
	}

	private void DockExternalOver(object sender, System.Windows.DragEventArgs e)
	{
		if (DockInputBusy || DockPayload(e.Data).Length == 0)
		{
			e.Effects = System.Windows.DragDropEffects.None;
			e.Handled = true;
			return;
		}
		e.Handled = true;
		System.Windows.Controls.Button recycle = ((!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) ? null : Items.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault((System.Windows.Controls.Button b) => string.Equals(ItemKey(b), RecycleBin.Clsid, StringComparison.OrdinalIgnoreCase) || string.Equals(ItemKey(b), "glassboard://dock/recycle", StringComparison.OrdinalIgnoreCase)));
		if (recycle != null)
		{
			System.Windows.Point point = e.GetPosition(recycle);
			if (point.X >= 0.0 && point.Y >= 0.0 && point.X <= recycle.ActualWidth && point.Y <= recycle.ActualHeight)
			{
				e.Effects = System.Windows.DragDropEffects.Move;
				ShowTaskHint("松手删除到回收站");
				return;
			}
		}
		e.Effects = System.Windows.DragDropEffects.Copy;
		if (!externalDockDrag)
		{
			externalDockDrag = true;
			if (taskWheel.Running)
			{
				taskWheel.Set(running: false);
				Refresh(capture: false);
				UpdateLayout();
			}
			ShowTaskHint("松手固定到 Dock");
		}
		ShowDockDrop(ExternalDockTarget(e.GetPosition(Items).X));
	}

	public void BeginBoardPinDrag()
	{
		boardPinDragging = true;
		if (IsVisible)
		{
			Topmost = true;
		}
	}

	public void EndBoardPinDrag()
	{
		boardPinDragging = false;
		ClearExternalDockDrag();
		if (IsVisible)
		{
			Topmost = C.Prefs.Dock.AlwaysOnTop || C.Prefs.Dock.AutoHide;
		}
	}

	internal void CheckTaskDock()
	{
		SetTaskMode(running: false);
		UpdateLayout();
		foreach (System.Windows.Controls.Button item in Items.Children.OfType<System.Windows.Controls.Button>())
		{
			item.ApplyTemplate();
			SolidColorBrush solidColorBrush = ((!(VisualTreeHelper.GetChild(item, 0) is Border border)) ? null : (border.Background as SolidColorBrush));
			if (solidColorBrush == null || solidColorBrush.Color.A == 0)
			{
				throw new Exception("Dock 整块点击区域缺少有效像素");
			}
			System.Windows.Point[] array = new System.Windows.Point[3]
			{
				new System.Windows.Point(item.ActualWidth / 2.0, item.ActualHeight / 2.0),
				new System.Windows.Point(4.0, 4.0),
				new System.Windows.Point(item.ActualWidth - 4.0, item.ActualHeight - 4.0)
			};
			foreach (System.Windows.Point point in array)
			{
				DependencyObject dependencyObject = item.InputHitTest(point) as DependencyObject;
				while (dependencyObject != null && dependencyObject != item)
				{
					dependencyObject = ((dependencyObject is Visual) ? VisualTreeHelper.GetParent(dependencyObject) : LogicalTreeHelper.GetParent(dependencyObject));
				}
				if (dependencyObject != item)
				{
					throw new Exception("Dock 图标空白区域无法命中按钮");
				}
			}
		}
		MouseWheelEventArgs e = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
		{
			RoutedEvent = Mouse.PreviewMouseWheelEvent
		};
		RaiseEvent(e);
		if (!e.Handled || !taskWheel.Running)
		{
			throw new Exception("Dock 滚轮未切换运行软件");
		}
		SetTaskMode(running: false);
	}

	private void ReleaseCancelledExternalDrag()
	{
		if (externalDockDrag && (GetAsyncKeyState((!SystemInformation.MouseButtonsSwapped) ? 1 : 2) & 0x8000) == 0)
		{
			ClearExternalDockDrag();
		}
	}

	private void ClearExternalDockDrag()
	{
		externalDockDrag = false;
		ClearDockMarkers();
	}

	private static string ItemKey(System.Windows.Controls.Button b)
	{
		object obj = b.GetValue(LayoutKey) as string;
		if (obj == null)
		{
			if (!(b.Tag is Entry))
			{
				return null;
			}
			obj = ((Entry)b.Tag).Path;
		}
		return (string)obj;
	}

	internal bool MoveLayout(string source, string target, bool before)
	{
		return DockLayoutRules.Move(C.Prefs, source, target, before, taskWheel.Running ? TaskKeys : null);
	}

	internal bool StepLayout(string source, int step)
	{
		return DockLayoutRules.Step(C.Prefs, source, step, taskWheel.Running ? TaskKeys : null);
	}

	private void AddSystemButton(string key, System.Windows.Controls.Button button, Action action)
	{
		button.SetValue(LayoutKey, key);
		BindLayoutButton(button, key, SystemEntryName(key), action);
		System.Windows.Controls.ContextMenu contextMenu = button.ContextMenu ?? MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		System.Windows.Controls.MenuItem menuItem = new System.Windows.Controls.MenuItem
		{
			Header = "向前移动"
		};
		menuItem.Click += (object s, RoutedEventArgs e) =>
		{
			C.StepDock(key, -1);
		};
		System.Windows.Controls.MenuItem menuItem2 = new System.Windows.Controls.MenuItem
		{
			Header = "向后移动"
		};
		menuItem2.Click += (object s, RoutedEventArgs e) =>
		{
			C.StepDock(key, 1);
		};
		if (contextMenu.Items.Count > 0)
		{
			contextMenu.Items.Add(new Separator());
		}
		// 更换图标：从内置图标样式（9 种几何，跟随当前图标风格包与强调色）中挑选
		string kind = SystemEntryKind(key);
		string currentGlyph = null;
		bool hasCustomImage = false;
		if (C.Prefs.SystemIconGlyphs != null)
		{
			C.Prefs.SystemIconGlyphs.TryGetValue(kind, out currentGlyph);
		}
		if (C.Prefs.SystemIconImages != null)
		{
			hasCustomImage = C.Prefs.SystemIconImages.ContainsKey(kind);
		}
		System.Windows.Controls.MenuItem changeIcon = new System.Windows.Controls.MenuItem
		{
			Header = "更换图标"
		};
		string[] glyphKinds = SystemIconActions.GlyphKinds;
		string[] glyphNames = SystemIconActions.GlyphNames;
		for (int g = 0; g < glyphKinds.Length; g++)
		{
			string choice = glyphKinds[g];
			System.Windows.Controls.MenuItem pick = new System.Windows.Controls.MenuItem
			{
				Header = glyphNames[g],
				Icon = IconPacks.Glyph(choice, C.Prefs.Dock.IconPack, C.Prefs.Dock.Accent, TextBrush, 18.0),
				IsChecked = !hasCustomImage && string.Equals(currentGlyph, choice, StringComparison.Ordinal)
			};
			pick.Click += (object s, RoutedEventArgs e) =>
			{
				C.SetSystemIconGlyph(kind, choice);
			};
			changeIcon.Items.Add(pick);
		}
		if (hasCustomImage || !string.IsNullOrEmpty(currentGlyph))
		{
			changeIcon.Items.Add(new Separator());
			System.Windows.Controls.MenuItem resetIcon = new System.Windows.Controls.MenuItem
			{
				Header = "恢复默认图标"
			};
			resetIcon.Click += (object s, RoutedEventArgs e) =>
			{
				C.SetSystemIconGlyph(kind, null);
			};
			changeIcon.Items.Add(resetIcon);
		}
		changeIcon.Items.Add(new Separator());
		System.Windows.Controls.MenuItem uploadIcon = new System.Windows.Controls.MenuItem
		{
			Header = "上传自定义图片…"
		};
		uploadIcon.Click += (object s, RoutedEventArgs e) =>
		{
			C.ChooseSystemIconImage(kind);
		};
		changeIcon.Items.Add(uploadIcon);
		contextMenu.Items.Add(changeIcon);
		contextMenu.Items.Add(menuItem);
		contextMenu.Items.Add(menuItem2);
		// 系统入口可直接在 dock 上隐藏（应用面板是应用图标的宿主，不提供隐藏）
		if (key != DockLayoutRules.Apps)
		{
			System.Windows.Controls.MenuItem hideEntry = new System.Windows.Controls.MenuItem
			{
				Header = "隐藏此入口"
			};
			hideEntry.Click += (object s, RoutedEventArgs e) =>
			{
				C.HideSystemEntry(key);
			};
			contextMenu.Items.Add(hideEntry);
		}
		if (button.ContextMenu == null)
		{
			AddDockControls(contextMenu);
		}
		TrackMenu(contextMenu);
		button.ContextMenu = contextMenu;
		Items.Children.Add(button);
	}

	// 正在运行的程序：单一入口按钮，点开/右键都弹出运行列表；
	// 每项点击=打开主面板，右键项=子菜单（打开主面板 / 退出程序）。
	private void AddRunningButton()
	{
		System.Windows.Controls.Button running = DockButton("▤", null, () => { }, "正在运行的程序", null, "running");
		// 与其它系统入口一致：右键=更换图标/向前向后移动，左键=弹出运行列表。
		// 注意：系统按钮的单击必须走 AddSystemButton 的 action——按钮按下时会先被
		// BindPointerItem 的 PreviewMouseDown 捕获（供拖拽判定），Button.Click 不会触发。
		AddSystemButton("glassboard://dock/running", running, () =>
		{
			Store.Trace("RUNNINGLIST click open=" + ((runningListPopup != null) ? (runningListPopup.IsOpen ? "true" : "false") : "null"));
			if (runningListPopup != null && runningListPopup.IsOpen)
			{
				runningListPopup.IsOpen = false;
				return;
			}
			runningListPopup = OpenRunningList(running);
		});
	}

	// 运行列表用 Popup 弹层：单击图标即弹出（与控制中心一样），再点或点击弹层外任意位置自动收起。
	// 不用 ContextMenu：StaysOpen 菜单会吞掉下一次单击（点一下关、点两下才开）。
	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint dwFlags);

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct DockRECT
	{
		public int Left, Top, Right, Bottom;
	}

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct DockMONITORINFO
	{
		public int cbSize;
		public DockRECT rcMonitor;
		public DockRECT rcWork;
		public uint dwFlags;
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(IntPtr hMonitor, ref DockMONITORINFO lpmi);

	private System.Windows.Controls.Primitives.Popup OpenRunningList(System.Windows.Controls.Button owner)
	{
		System.Windows.Controls.Primitives.Popup created = null;
		Action close = delegate
		{
			if (created != null)
			{
				created.IsOpen = false;
			}
		};
		RunningWindows.Scan();
		ReadTaskGroups();
		List<TaskGroup> groups = taskGroups.OrderBy((TaskGroup g) => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
		(System.Windows.Media.Color bg, System.Windows.Media.Color ink, System.Windows.Media.Color line, System.Windows.Media.Color hover) = MenuTheme.Palette(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(4.0)
		};
		if (groups.Count == 0)
		{
			panel.Children.Add(new TextBlock
			{
				Text = "暂无正在运行的程序",
				Foreground = new System.Windows.Media.SolidColorBrush(ink),
				FontSize = 12.5,
				Margin = new Thickness(10.0, 6.0, 10.0, 8.0)
			});
		}
		else
		{
			foreach (TaskGroup g in groups)
			{
				TaskGroup selected = g;
				string suffix = selected.Windows.All((RunningWindow w) => w.Tray) ? "（托盘）" : ((selected.Windows.Count > 1) ? ("（" + selected.Windows.Count + " 个窗口）") : "");
				System.Windows.Controls.Border row = new System.Windows.Controls.Border
				{
					CornerRadius = new CornerRadius(6.0),
					Background = System.Windows.Media.Brushes.Transparent,
					Padding = new Thickness(10.0, 6.0, 10.0, 6.0),
					Cursor = System.Windows.Input.Cursors.Hand
				};
				StackPanel content = new StackPanel
				{
					Orientation = System.Windows.Controls.Orientation.Horizontal
				};
				TextBlock nameBlock = new TextBlock
				{
					Text = selected.Name + suffix,
					Foreground = new System.Windows.Media.SolidColorBrush(ink),
					FontSize = 12.5,
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(9.0, 0.0, 0.0, 0.0),
					MaxWidth = 240.0,
					TextTrimming = TextTrimming.CharacterEllipsis
				};
				content.Children.Add(nameBlock);
				Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
				{
					try
					{
						ImageSource iconSource = RunningWindows.Icon(selected.Windows[0]);
						content.Children.Insert(0, new System.Windows.Controls.Image
						{
							Source = iconSource,
							Width = 18.0,
							Height = 18.0,
							Stretch = Stretch.Uniform,
							VerticalAlignment = VerticalAlignment.Center
						});
					}
					catch
					{
					}
				}));
				row.Child = content;
				row.MouseEnter += (object ss, System.Windows.Input.MouseEventArgs ee) =>
				{
					row.Background = new System.Windows.Media.SolidColorBrush(hover);
				};
				row.MouseLeave += (object ss, System.Windows.Input.MouseEventArgs ee) =>
				{
					row.Background = System.Windows.Media.Brushes.Transparent;
				};
				row.MouseLeftButtonUp += (object ss, MouseButtonEventArgs ee) =>
				{
					close();
					OpenTask(selected, null);
				};
				System.Windows.Controls.ContextMenu rowMenu = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
				System.Windows.Controls.MenuItem openItem = new System.Windows.Controls.MenuItem
				{
					Header = "打开主面板"
				};
				openItem.Click += (object ss, RoutedEventArgs ee) =>
				{
					close();
					OpenTask(selected, null);
				};
				System.Windows.Controls.MenuItem quitItem = new System.Windows.Controls.MenuItem
				{
					Header = "退出程序"
				};
				quitItem.Click += (object ss, RoutedEventArgs ee) =>
				{
					close();
					KillTask(selected);
				};
				// —— 任务栏式窗口切换：单窗口直接项，多窗口给「切换窗口」子菜单 ——
				if (selected.Windows.Count == 1)
				{
					RunningWindow single = selected.Windows[0];
					System.Windows.Controls.MenuItem showItem = new System.Windows.Controls.MenuItem
					{
						Header = "显示窗口",
						ToolTip = single.Title
					};
					showItem.Click += (object ss, RoutedEventArgs ee) =>
					{
						close();
						RunningWindows.ActivateHandle(single.Handle);
					};
					rowMenu.Items.Add(showItem);
				}
				else if (selected.Windows.Count > 1)
				{
					System.Windows.Controls.MenuItem switchItem = new System.Windows.Controls.MenuItem
					{
						Header = "切换窗口"
					};
					foreach (RunningWindow w in selected.Windows)
					{
						RunningWindow captured = w;
						string label = string.IsNullOrWhiteSpace(w.Title) ? "（无标题）" : ((w.Title.Length > 28) ? (w.Title.Substring(0, 28) + "…") : w.Title);
						System.Windows.Controls.MenuItem one = new System.Windows.Controls.MenuItem
						{
							Header = label,
							ToolTip = w.Title
						};
						one.Click += (object ss, RoutedEventArgs ee) =>
						{
							close();
							RunningWindows.ActivateHandle(captured.Handle);
						};
						switchItem.Items.Add(one);
					}
					rowMenu.Items.Add(switchItem);
				}
				rowMenu.Items.Add(openItem);
				rowMenu.Items.Add(quitItem);
				row.ContextMenu = rowMenu;
				panel.Children.Add(row);
			}
		}
		ScrollViewer scroller = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
			MaxHeight = 380.0,
			Content = panel
		};
		Border shell = new Border
		{
			CornerRadius = new CornerRadius(12.0),
			Background = new System.Windows.Media.SolidColorBrush(bg),
			BorderBrush = new System.Windows.Media.SolidColorBrush(line),
			BorderThickness = new Thickness(1.0),
			Child = scroller,
			MinWidth = 220.0
		};
		System.Windows.Controls.Primitives.Popup popup = new System.Windows.Controls.Primitives.Popup
		{
			StaysOpen = false,
			AllowsTransparency = true,
			PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
			Placement = PlacementMode.Absolute,
			Child = shell
		};
		// 不依赖 PlacementTarget：绝对坐标定位，避免 dock Refresh 重建按钮时弹层被连带关闭。
		popup.Opened += delegate
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
			{
				try
				{
					PositionRunningList(popup, shell, owner, shell.ActualWidth, shell.ActualHeight);
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			}));
		};
		created = popup;
		// 打开前先手动测量得到预期尺寸并定位：Absolute 弹层若先开再定位，会先在屏幕
		// 左上角 (0,0) 闪出，等定位回调才跳到位（"点运行程序左上角出现几秒"的根因）。
		shell.Measure(new System.Windows.Size(460.0, 380.0));
		PositionRunningList(popup, shell, owner, shell.DesiredSize.Width, shell.DesiredSize.Height);
		popup.IsOpen = true;
		return popup;
	}

	private void PositionRunningList(System.Windows.Controls.Primitives.Popup popup, FrameworkElement shell, FrameworkElement owner, double widthHint, double heightHint)
	{
		double w = widthHint;
		double h = heightHint;
		if (w < 10.0)
		{
			w = 220.0;
		}
		if (h < 10.0)
		{
			h = 60.0;
		}
		// 与控制中心窗口完全同款：右边缘对齐 dock 右边缘、紧贴 dock 上方，
		// 全程 DIP（Left/Top/WorkArea 均为 DIP），无任何物理像素换算。
		System.Windows.Rect wa = SystemParameters.WorkArea;
		double left = Math.Max(wa.Left + 8.0, Math.Min(Left + Width - w - 8.0, wa.Right - w - 8.0));
		double top = Top - h - 10.0;
		if (top < wa.Top + 8.0)
		{
			top = Top + Height + 10.0;
		}
		popup.HorizontalOffset = left;
		popup.VerticalOffset = top;
		Store.Trace("RUNNINGLIST popup at " + (int)left + "," + (int)top + " size=" + (int)w + "x" + (int)h + " dock=" + (int)Left + "," + (int)Top);
	}

	private void KillTask(TaskGroup group)
	{
		List<int> pids = group.Windows.Select((RunningWindow w) => (int)w.ProcessId).Distinct().ToList();
		Task.Run(delegate
		{
			foreach (int pid in pids)
			{
				try
				{
					using (Process process = Process.GetProcessById(pid))
					{
						if (!process.CloseMainWindow() || !process.WaitForExit(1200))
						{
							process.Kill();
						}
					}
				}
				catch
				{
				}
			}
			Dispatcher.BeginInvoke(new Action(delegate
			{
				RunningWindows.Scan();
				ReadTaskGroups();
				Refresh(capture: false);
			}));
		});
	}

	// 系统入口图标的对应名称：悬浮提示与拖动标签都按各自的内容显示，不再统一成"系统入口"
	internal static string SystemEntryName(string key)
	{
		return key switch
		{
			"glassboard://dock/apps" => "应用面板",
			"glassboard://dock/computer" => "此电脑",
			"glassboard://dock/recycle" => "回收站",
			"glassboard://dock/control" => "控制中心",
			"glassboard://dock/settings" => "设置",
			"glassboard://dock/power" => "电源",
			"glassboard://dock/network" => "Wi-Fi / 蓝牙",
			"glassboard://dock/running" => "正在运行的程序",
			_ => "系统入口"
		};
	}

	// 系统入口对应的内置图标样式（更换图标时的默认样式与覆盖键）
	internal static string SystemEntryKind(string key)
	{
		return key switch
		{
			"glassboard://dock/apps" => "apps",
			"glassboard://dock/computer" => "computer",
			"glassboard://dock/recycle" => "recycle",
			"glassboard://dock/control" => "control",
			"glassboard://dock/settings" => "settings",
			"glassboard://dock/power" => "power",
			"glassboard://dock/network" => "wifi",
			"glassboard://dock/running" => "running",
			_ => "apps"
		};
	}

	private void BindLayoutButton(System.Windows.Controls.Button button, string key, string name, Action action)
	{
		button.SetValue(LayoutKey, key);
		BindPointerItem(button, key, name, action);
		AddInsertMarker(button);
	}

	private void AddInsertMarker(System.Windows.Controls.Button button)
	{
		if (button.Content is Grid grid && !grid.Children.OfType<Border>().Any((Border b) => b.Tag as string == "dockInsert"))
		{
			grid.Children.Add(new Border
			{
				Tag = "dockInsert",
				Width = 3.0,
				Height = C.Prefs.Dock.IconSize + 8.0,
				CornerRadius = new CornerRadius(2.0),
				Background = Polish.Accent,
				VerticalAlignment = VerticalAlignment.Center,
				Visibility = Visibility.Collapsed,
				IsHitTestVisible = false
			});
		}
	}

	private void ArrangeDockItems()
	{
		List<System.Windows.Controls.Button> list = Items.Children.OfType<System.Windows.Controls.Button>().ToList();
		List<string> layoutKeys = LayoutKeys;
		List<System.Windows.Controls.Button> list2 = new List<System.Windows.Controls.Button>();
		foreach (string key in layoutKeys)
		{
			System.Windows.Controls.Button button = list.FirstOrDefault((System.Windows.Controls.Button x) => string.Equals(ItemKey(x), key, StringComparison.OrdinalIgnoreCase));
			if (button != null)
			{
				list2.Add(button);
			}
		}
		List<System.Windows.Controls.Button> collection = list.Where((System.Windows.Controls.Button b) => ItemKey(b) == null).ToList();
		int num = list2.FindLastIndex((System.Windows.Controls.Button b) => !DockLayoutRules.SystemItem(ItemKey(b)));
		list2.InsertRange((num < 0) ? Math.Min(2, list2.Count) : (num + 1), collection);
		Items.Children.Clear();
		foreach (System.Windows.Controls.Button item in list2)
		{
			Items.Children.Add(item);
			if (C.Prefs.Dock.ShowSeparator && ItemKey(item) == "glassboard://dock/apps")
			{
				Items.Children.Add(new Border
				{
					Width = 1.0,
					Height = C.Prefs.Dock.IconSize * 0.7,
					Margin = new Thickness(8.0, 0.0, 8.0, 0.0),
					Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(65, 220, 220, 230))
				});
			}
		}
	}

	internal void CheckMixedPointerReorder()
	{
		C.Prefs.Dock.MotionEnabled = false;
		C.Prefs.Dock.ShowPower = (C.Prefs.Dock.ShowNetwork = (C.Prefs.Dock.ShowComputer = (C.Prefs.Dock.ShowSettings = (C.Prefs.Dock.ShowControlCenter = true))));
		taskWheel.Set(running: false);
		Refresh(capture: false);
		UpdateLayout();
		string[] systemKeys = DockLayoutRules.SystemKeys;
		foreach (string key in systemKeys)
		{
			for (int j = 0; j < 2; j++)
			{
				System.Windows.Controls.Button button = Items.Children.OfType<System.Windows.Controls.Button>().First((System.Windows.Controls.Button b) => ItemKey(b) == key);
				List<System.Windows.Controls.Button> list = (from b in Items.Children.OfType<System.Windows.Controls.Button>()
					where b.Tag is Entry
					select b).ToList();
				System.Windows.Controls.Button button2 = ((j == 0) ? list.First() : list.Last());
				string text = ItemKey(button2);
				DockDropTarget dockDropTarget = DockLayoutRules.OnIcon(C.Prefs, key, text);
				if (dockDropTarget == null)
				{
					throw new Exception("混排目标无效");
				}
				List<string> list2 = LayoutKeys.ToList();
				list2.Remove(key);
				list2.Insert(list2.IndexOf(text) + ((!dockDropTarget.Before) ? 1 : 0), key);
				bool clicked = false;
				System.Windows.Point point = button.TranslatePoint(new System.Windows.Point(button.ActualWidth / 2.0, button.ActualHeight / 2.0), this);
				System.Windows.Point point2 = button2.TranslatePoint(new System.Windows.Point(button2.ActualWidth / 2.0, button2.ActualHeight / 2.0), this);
				if (!StartPointerItem(button, key, key, () =>
				{
					clicked = true;
				}, point, null))
				{
					throw new Exception("系统入口无法捕获拖动");
				}
				MoveDockPointer(point2);
				FinishDockPointer(point2);
				UpdateLayout();
				if (clicked || !LayoutKeys.SequenceEqual(list2))
				{
					throw new Exception("系统入口拖放未保存，或拖动触发了点击");
				}
			}
		}
		string text2 = string.Join("|", LayoutKeys);
		C.Prefs.Dock.ShowSettings = false;
		Refresh(capture: false);
		if (Items.Children.OfType<System.Windows.Controls.Button>().Any((System.Windows.Controls.Button b) => ItemKey(b) == "glassboard://dock/settings") || ContextMenu == null)
		{
			throw new Exception("隐藏设置图标后仍显示，或缺少恢复菜单");
		}
		C.Prefs.Dock.ShowSettings = true;
		Refresh(capture: false);
		if (string.Join("|", LayoutKeys) != text2)
		{
			throw new Exception("重新显示设置图标后丢失位置");
		}
	}

	// ==================== 悬浮窗口预览（任务栏式缩略图）====================
	private void AttachPreview(System.Windows.Controls.Button button, object owner)
	{
		button.MouseEnter += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			Store.Trace("PREVIEW mouseenter");
			PreviewHover(button, owner);
		};
		button.MouseLeave += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			PreviewLeave();
		};
	}

	private void PreviewHover(FrameworkElement anchor, object owner)
	{
		if (DockInputBusy || dockIconDragging || externalDockDrag)
		{
			Store.Trace("PREVIEW hover blocked busy=" + DockInputBusy + " icon=" + dockIconDragging + " ext=" + externalDockDrag);
			return;
		}
		if (previewPopup != null && previewPopup.IsOpen && previewAnchor == anchor)
		{
			if (previewCloseTimer != null)
			{
				previewCloseTimer.Stop();
			}
			return;
		}
		ClosePreview();
		previewAnchor = anchor;
		previewOwner = owner;
		if (previewTimer == null)
		{
			previewTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(380.0)
			};
			previewTimer.Tick += (object s, EventArgs e) =>
			{
				previewTimer.Stop();
				PreviewOpen();
			};
		}
		previewTimer.Stop();
		previewTimer.Start();
	}

	private void PreviewLeave()
	{
		if (previewTimer != null)
		{
			previewTimer.Stop();
		}
		if (previewPopup == null || !previewPopup.IsOpen)
		{
			previewAnchor = null;
			previewOwner = null;
			return;
		}
		// 3 秒运行扫描会触发视觉树刷新并短暂重算悬停状态：光标仍停在图标上时保持打开，避免闪烁
		if (previewAnchor != null && previewAnchor.IsMouseOver)
		{
			return;
		}
		if (previewCloseTimer == null)
		{
			previewCloseTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(300.0)
			};
			previewCloseTimer.Tick += (object s, EventArgs e) =>
			{
				previewCloseTimer.Stop();
				if (previewAnchor != null && previewAnchor.IsMouseOver)
				{
					return;
				}
				ClosePreview();
			};
		}
		previewCloseTimer.Stop();
		previewCloseTimer.Start();
	}

	private List<RunningWindow> PreviewWindows(object owner)
	{
		if (owner is TaskGroup group)
		{
			return group.Windows;
		}
		if (owner is Entry entry)
		{
			RunningWindows.Scan();
			return RunningWindows.For(entry);
		}
		return null;
	}

	private void PreviewOpen()
	{
		FrameworkElement anchor = previewAnchor;
		object owner = previewOwner;
		if (anchor == null || owner == null || !anchor.IsMouseOver)
		{
			ClosePreview();
			return;
		}
		List<RunningWindow> windows = PreviewWindows(owner);
		if (windows == null || windows.Count <= 1)
		{
			Store.Trace("PREVIEW skip count=" + ((windows == null) ? 0 : windows.Count));
			ClosePreview();
			return;
		}
		bool light = C.Prefs.Light;
		(System.Windows.Media.Color bg, System.Windows.Media.Color ink, System.Windows.Media.Color line, System.Windows.Media.Color hover) palette = MenuTheme.Palette(light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		System.Windows.Media.Color popupBg = palette.bg;
		System.Windows.Media.Color edge = palette.line;
		ClosePreview();
		previewAnchor = anchor;
		previewOwner = owner;
		string name;
		ImageSource appIcon = null;
		if (owner is TaskGroup group2)
		{
			name = group2.Name;
			appIcon = RunningWindows.Icon(group2.Windows[0]);
		}
		else
		{
			Entry entry2 = (Entry)owner;
			name = entry2.Name;
			appIcon = FeatureUi.Icon(entry2, false, true);
		}
		Border card = new Border
		{
			Background = new SolidColorBrush(popupBg),
			BorderBrush = new SolidColorBrush(edge),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(14.0),
			Padding = new Thickness(10.0),
			SnapsToDevicePixels = true
		};
		StackPanel root = new StackPanel();
		StackPanel head = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(2.0, 0.0, 2.0, 8.0)
		};
		if (appIcon != null)
		{
			head.Children.Add(new System.Windows.Controls.Image
			{
				Source = appIcon,
				Width = 18.0,
				Height = 18.0,
				Stretch = Stretch.Uniform,
				Margin = new Thickness(0.0, 0.0, 7.0, 0.0)
			});
		}
		head.Children.Add(new TextBlock
		{
			Text = name + " · " + windows.Count + " 个窗口",
			FontSize = 12.5,
			Foreground = TextBrush,
			VerticalAlignment = VerticalAlignment.Center
		});
		root.Children.Add(head);
		StackPanel row = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		previewSlots = new System.Collections.Generic.List<System.Windows.Controls.Image>();
		previewSlotWindows = new System.Collections.Generic.List<IntPtr>();
		foreach (RunningWindow window in windows)
		{
			row.Children.Add(PreviewCard(window, light, palette.hover));
		}
		root.Children.Add(row);
		card.Child = root;
		if (anchor is System.Windows.Controls.Button btn)
		{
			ToolTipService.SetIsEnabled(btn, false);
		}
		previewSetupTries = 0;
		previewPopup = new System.Windows.Controls.Primitives.Popup
		{
			Child = card,
			PlacementTarget = anchor,
			Placement = PlacementMode.Custom,
			AllowsTransparency = true,
			PopupAnimation = PopupAnimation.None,
			StaysOpen = true,
			Focusable = false
		};
		// 悬浮面板始终在图标正上方水平居中，向上留 12px 间距
		previewPopup.CustomPopupPlacementCallback = delegate (System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
		{
			Store.Trace("PREVIEW placement popup=" + (int)popupSize.Width + "x" + (int)popupSize.Height + " target=" + (int)targetSize.Width + "x" + (int)targetSize.Height);
			return new CustomPopupPlacement[1]
			{
				new CustomPopupPlacement(new System.Windows.Point((targetSize.Width - popupSize.Width) / 2.0, -popupSize.Height - 12.0), PopupPrimaryAxis.Horizontal)
			};
		};
		card.MouseEnter += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			if (previewCloseTimer != null)
			{
				previewCloseTimer.Stop();
			}
		};
		card.MouseLeave += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			PreviewLeave();
		};
		previewPopup.Opened += (object s, EventArgs e) =>
		{
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(SetupPreviewThumbs));
		};
		previewPopup.IsOpen = true;
		watchPopups.Add(previewPopup);
		watchAway[previewPopup] = 0;
		EnsureWatchTimer();
		// 兜底：Opened 在无资源管理器外壳的 DSH 环境可能不触发，轮询等到弹窗拿到 PresentationSource 且卡片完成布局后再挂缩略图
		int tries = 0;
		Action trySetup = null;
		trySetup = delegate
		{
			if (previewPopup == null || !previewPopup.IsOpen)
			{
				return;
			}
			if (PresentationSource.FromVisual(card) != null && previewSlots != null && previewSlots.Count > 0 && previewSlots[0].ActualWidth > 1.0)
			{
				SetupPreviewThumbs();
				return;
			}
			if (++tries > 30)
			{
				Store.Trace("PREVIEW thumbs setup gave up");
				return;
			}
			if (tries == 1 || tries == 10 || tries == 20 || tries == 30)
			{
				string extra = (previewSlots != null && previewSlots.Count > 0) ? (" slot0=" + (int)previewSlots[0].ActualWidth + "x" + (int)previewSlots[0].ActualHeight) : " slots=null";
				Store.Trace("PREVIEW poll tries=" + tries + extra + " child=" + ((card.RenderSize.Width > 1.0) ? ((int)card.RenderSize.Width + "x" + (int)card.RenderSize.Height) : "0x0"));
			}
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, trySetup);
		};
		Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, trySetup);
		Store.Trace("PREVIEW open " + name + " windows=" + windows.Count);
	}

	private FrameworkElement PreviewCard(RunningWindow window, bool light, System.Windows.Media.Color hoverColor)
	{
		System.Windows.Media.Color cardBg = System.Windows.Media.Color.FromArgb(light ? (byte)64 : (byte)58, hoverColor.R, hoverColor.G, hoverColor.B);
		System.Windows.Media.Color hoverBg = System.Windows.Media.Color.FromArgb(light ? (byte)150 : (byte)120, hoverColor.R, hoverColor.G, hoverColor.B);
		Border outer = new Border
		{
			Background = new SolidColorBrush(cardBg),
			CornerRadius = new CornerRadius(10.0),
			Margin = new Thickness(3.5, 0.0, 3.5, 0.0),
			Padding = new Thickness(6.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			SnapsToDevicePixels = true
		};
		StackPanel sp = new StackPanel();
		System.Windows.Controls.Image img = new System.Windows.Controls.Image
		{
			Width = 168.0,
			Height = 105.0,
			Stretch = Stretch.Uniform,
			Opacity = 0.7
		};
		RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
		if (previewSlots != null)
		{
			previewSlots.Add(img);
			previewSlotWindows.Add(window.Handle);
		}
		BitmapSource cached = PreviewThumb(window.Handle);
		if (cached != null)
		{
			img.Source = cached;
			img.Opacity = 1.0;
		}
		sp.Children.Add(img);
		sp.Children.Add(new TextBlock
		{
			Text = (string.IsNullOrWhiteSpace(window.Title) ? "未命名窗口" : window.Title),
			FontSize = 11.0,
			Foreground = TextBrush,
			Width = 180.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextAlignment = TextAlignment.Center,
			Margin = new Thickness(0.0, 6.0, 0.0, 2.0)
		});
		outer.Child = sp;
		IntPtr handle = window.Handle;
		outer.MouseEnter += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			outer.Background = new SolidColorBrush(hoverBg);
		};
		outer.MouseLeave += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			outer.Background = new SolidColorBrush(cardBg);
		};
		outer.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			ClosePreview();
			ActivateTask(handle);
		};
		if (cached == null)
		{
			Task.Run(() => CaptureThumb(handle)).ContinueWith(delegate (Task<BitmapSource> t)
			{
				BitmapSource src = (t.Status == TaskStatus.RanToCompletion) ? t.Result : null;
				if (src != null)
				{
					Dispatcher.BeginInvoke(new Action(delegate
					{
						img.Source = src;
						img.Opacity = 1.0;
					}));
				}
			});
		}
		return outer;
	}

	private BitmapSource PreviewThumb(IntPtr handle)
	{
		if (previewCache.TryGetValue(handle.ToInt64(), out BitmapSource src) && src != null)
		{
			return src;
		}
		return null;
	}

	private static void StoreThumb(IntPtr handle, BitmapSource src)
	{
		if (previewCache.Count > 48)
		{
			previewCache.Clear();
		}
		previewCache[handle.ToInt64()] = src;
	}

	private BitmapSource CaptureThumb(IntPtr handle)
	{
		try
		{
			if (!PreviewIsWindow(handle) || !PreviewIsWindowVisible(handle) || PreviewIsIconic(handle))
			{
				return null;
			}
			PreviewRect r;
			if (!PreviewGetWindowRect(handle, out r))
			{
				return null;
			}
			int width = r.Right - r.Left;
			int height = r.Bottom - r.Top;
			if (width <= 0 || height <= 0 || width > 6000 || height > 4000)
			{
				return null;
			}
			IntPtr windowDc = GetWindowDC(handle);
			IntPtr memDc = CreateCompatibleDC(windowDc);
			IntPtr bmp = CreateCompatibleBitmap(windowDc, width, height);
			IntPtr old = SelectObject(memDc, bmp);
			bool ok = PrintWindow(handle, memDc, 2u);
			if (!ok)
			{
				ok = PrintWindow(handle, memDc, 0u);
			}
			SelectObject(memDc, old);
			ReleaseDC(handle, windowDc);
			DeleteDC(memDc);
			if (!ok)
			{
				DeleteObject(bmp);
				return null;
			}
			try
			{
				using (System.Drawing.Bitmap b = System.Drawing.Image.FromHbitmap(bmp))
				{
					IntPtr hb = b.GetHbitmap();
					try
					{
						BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(hb, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
						src.Freeze();
						StoreThumb(handle, src);
						return src;
					}
					finally
					{
						DeleteObject(hb);
					}
				}
			}
			finally
			{
				DeleteObject(bmp);
			}
		}
		catch
		{
			return null;
		}
	}

	// —— 悬浮弹层看门狗 ——
	private void EnsureWatchTimer()
	{
		if (watchTimer == null)
		{
			watchTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(250.0)
			};
			watchTimer.Tick += WatchTick;
		}
		if (!watchTimer.IsEnabled)
		{
			watchTimer.Start();
		}
	}

	private void StopWatchTimer()
	{
		if (watchTimer != null)
		{
			watchTimer.Stop();
		}
	}

	private void WatchTick(object sender, EventArgs e)
	{
		if (watchPopups.Count == 0)
		{
			StopWatchTimer();
			return;
		}
		if (!IsVisible)
		{
			// Dock 收隐（全屏游戏等）：弹层是独立置顶窗口，不会跟着 Dock 隐藏，必须立即全部关闭
			while (watchPopups.Count > 0)
			{
				System.Windows.Controls.Primitives.Popup orphan = watchPopups[watchPopups.Count - 1];
				watchPopups.RemoveAt(watchPopups.Count - 1);
				watchAway.Remove(orphan);
				if (orphan == previewPopup)
				{
					ClosePreview();
				}
				else if (orphan != null)
				{
					orphan.IsOpen = false;
					orphan.Child = null;
				}
			}
			StopWatchTimer();
			return;
		}
		for (int i = watchPopups.Count - 1; i >= 0; i--)
		{
			System.Windows.Controls.Primitives.Popup p = watchPopups[i];
			if (p == null)
			{
				watchPopups.RemoveAt(i);
				continue;
			}
			bool hovered = p.IsOpen && (((p.PlacementTarget as FrameworkElement) != null && ((FrameworkElement)p.PlacementTarget).IsMouseOver) || (p.Child != null && p.Child.IsMouseOver));
			if (hovered)
			{
				watchAway[p] = 0;
				continue;
			}
			int away = watchAway.TryGetValue(p, out int current) ? current + 1 : 1;
			watchAway[p] = away;
			if (away < 4)
			{
				continue;
			}
			// 图标与弹层都不在鼠标下超过 1 秒：强制关闭，覆盖切换窗口/Dock 被遮挡等漏事件场景
			watchPopups.RemoveAt(i);
			watchAway.Remove(p);
			if (p == previewPopup)
			{
				ClosePreview();
			}
			else
			{
				p.IsOpen = false;
				p.Child = null;
			}
		}
		if (watchPopups.Count == 0)
		{
			StopWatchTimer();
		}
	}

	private void ClosePreview()
	{
		if (previewTimer != null)
		{
			previewTimer.Stop();
		}
		if (previewCloseTimer != null)
		{
			previewCloseTimer.Stop();
		}
		if (previewPopup != null)
		{
			watchPopups.Remove(previewPopup);
			watchAway.Remove(previewPopup);
			if (watchPopups.Count == 0)
			{
				StopWatchTimer();
			}
		}
		if (previewThumbs != null)
		{
			foreach (IntPtr thumb in previewThumbs)
			{
				DwmUnregisterThumbnail(thumb);
			}
			previewThumbs.Clear();
		}
		if (previewHosts != null)
		{
			foreach (System.Windows.Interop.HwndSource host in previewHosts)
			{
				host.Dispose();
			}
			previewHosts.Clear();
			previewHosts = null;
		}
		if (previewThumbs != null)
		{
			previewThumbs = null;
		}
		previewSlots = null;
		previewSlotWindows = null;
		if (previewPopup != null)
		{
			if (previewAnchor is System.Windows.Controls.Button btn)
			{
				ToolTipService.SetIsEnabled(btn, true);
			}
			previewPopup.IsOpen = false;
			previewPopup = null;
			Store.Trace("PREVIEW close");
		}
		previewAnchor = null;
		previewOwner = null;
	}

	// 用 DWM 实时缩略图（任务栏同款）覆盖每个窗口卡片；注册失败时保留 PrintWindow 抓图回退
	private void SetupPreviewThumbs()
	{
		try
		{
			if (previewHosts != null)
			{
				return; // 已挂载（Opened 与轮询兜底可能各触发一次）
			}
			if (previewPopup == null || !previewPopup.IsOpen || previewPopup.Child == null)
			{
				Store.Trace("PREVIEW thumbs abort popup=" + ((previewPopup != null) ? (previewPopup.IsOpen ? "open" : "closed") : "null"));
				return;
			}
			if (previewSlots == null || previewSlotWindows == null)
			{
				Store.Trace("PREVIEW thumbs abort slots=null");
				return;
			}
			HwndSource popupSource = PresentationSource.FromVisual(previewPopup.Child) as HwndSource;
			if (popupSource == null)
			{
				Store.Trace("PREVIEW thumbs abort source=null");
				return;
			}
			Store.Trace("PREVIEW thumbs setup slots=" + previewSlots.Count);
			if (previewSlots.Any((System.Windows.Controls.Image s) => s != null && s.IsVisible && s.ActualWidth < 2.0))
			{
				// 弹窗刚打开时布局可能尚未完成，稍后重试
				if (++previewSetupTries > 20)
				{
					Store.Trace("PREVIEW thumbs setup gave up");
					return;
				}
				if (previewSetupTries == 1 || previewSetupTries == 10 || previewSetupTries == 20)
				{
					string sizes = "";
					foreach (System.Windows.Controls.Image image in previewSlots)
					{
						sizes += "[" + ((int)image.ActualWidth) + "x" + ((int)image.ActualHeight) + " vis=" + image.IsVisible + " " + image.Visibility + "]";
					}
					Store.Trace("PREVIEW retry tries=" + previewSetupTries + sizes + " popupChild=" + ((int)previewPopup.Child.RenderSize.Width) + "x" + ((int)previewPopup.Child.RenderSize.Height));
				}
				Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(SetupPreviewThumbs));
				return;
			}
			previewSetupTries = 0;
			IntPtr popupHwnd = popupSource.Handle;
			System.Windows.Point popupOrigin = previewPopup.Child.PointToScreen(new System.Windows.Point(0.0, 0.0));
			previewHosts = new System.Collections.Generic.List<System.Windows.Interop.HwndSource>();
			previewThumbs = new System.Collections.Generic.List<IntPtr>();
			for (int i = 0; i < previewSlots.Count; i++)
			{
				System.Windows.Controls.Image slot = previewSlots[i];
				IntPtr target = previewSlotWindows[i];
				if (slot == null || target == IntPtr.Zero || !slot.IsVisible)
				{
					Store.Trace("PREVIEW thumb skip slot=" + ((slot != null) ? (slot.IsVisible ? "visible" : "hidden") : "null") + " target=" + ((target != IntPtr.Zero) ? target.ToInt64() : 0L));
					continue;
				}
				System.Windows.Point topLeft = slot.TransformToAncestor(previewPopup.Child).Transform(new System.Windows.Point(0.0, 0.0));
				System.Windows.Point slotScreen = previewPopup.Child.PointToScreen(topLeft);
				System.Windows.Point bottomRight = previewPopup.Child.PointToScreen(new System.Windows.Point(topLeft.X + slot.ActualWidth, topLeft.Y + slot.ActualHeight));
				int x = (int)Math.Round(slotScreen.X - popupOrigin.X);
				int y = (int)Math.Round(slotScreen.Y - popupOrigin.Y);
				int width = (int)Math.Round(bottomRight.X - slotScreen.X);
				int height = (int)Math.Round(bottomRight.Y - slotScreen.Y);
				if (width < 2 || height < 2)
				{
					Store.Trace("PREVIEW thumb skip size=" + width + "x" + height + " slotW=" + slot.ActualWidth + " slotH=" + slot.ActualHeight);
					continue;
				}
				System.Windows.Interop.HwndSource host = new System.Windows.Interop.HwndSource(0, 1073741824 | 268435456, 0, x, y, width, height, "gbthumb", popupHwnd);
				host.CompositionTarget.BackgroundColor = Colors.Transparent;
				IntPtr thumb;
				int result = DwmRegisterThumbnail(host.Handle, target, out thumb);
				Store.Trace("PREVIEW thumb hwnd=" + target.ToInt64() + " reg=" + result);
				if (result == 0)
				{
					DWM_THUMBNAIL_PROPERTIES props = default(DWM_THUMBNAIL_PROPERTIES);
					props.dwFlags = 1 | 4 | 8 | 16;
					props.opacity = byte.MaxValue;
					props.fVisible = true;
					props.fSourceClientAreaOnly = true;
					props.rcDestination = new PreviewRect
					{
						Left = (int)Math.Round(slotScreen.X),
						Top = (int)Math.Round(slotScreen.Y),
						Right = (int)Math.Round(bottomRight.X),
						Bottom = (int)Math.Round(bottomRight.Y)
					};
					DwmUpdateThumbnailProperties(thumb, ref props);
					previewThumbs.Add(thumb);
					slot.Visibility = Visibility.Hidden;
				}
				IntPtr handle = target;
				host.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
				{
					if (msg == 514)
					{
						Dispatcher.BeginInvoke(new Action(delegate
						{
							ClosePreview();
							ActivateTask(handle);
						}));
						handled = true;
					}
					return IntPtr.Zero;
				});
				previewHosts.Add(host);
			}
		}
		catch (Exception ex)
		{
			Store.Trace("PREVIEW thumbs setup error " + ex.GetType().Name + " " + ex.Message);
		}
	}

	private struct PreviewRect
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DWM_THUMBNAIL_PROPERTIES
	{
		public int dwFlags;

		public PreviewRect rcDestination;

		public PreviewRect rcSource;

		public byte opacity;

		public bool fVisible;

		public bool fSourceClientAreaOnly;
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

	[DllImport("dwmapi.dll")]
	private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

	[DllImport("dwmapi.dll")]
	private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES props);

	[DllImport("user32.dll")]
	private static extern bool PreviewGetWindowRect(IntPtr hWnd, out PreviewRect rect);

	[DllImport("user32.dll")]
	private static extern bool PreviewIsWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool PreviewIsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool PreviewIsIconic(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindowDC(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

	[DllImport("gdi32.dll")]
	private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

	[DllImport("gdi32.dll")]
	private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

	[DllImport("gdi32.dll")]
	private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(IntPtr obj);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteDC(IntPtr hdc);

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int key);

	private bool DockDragSuppresses(FrameworkElement element)
	{
		if (element != null)
		{
			return (bool)element.GetValue(DragSuppressed);
		}
		return false;
	}

	private void BindDockSource(FrameworkElement element, Entry entry, Action beforeDrag = null)
	{
		BindPointerItem(element, entry.Path, entry.Name, () =>
		{
			C.Launch(entry);
		}, beforeDrag);
	}

	private void BindPointerItem(FrameworkElement element, string key, string name, Action click, Action beforeDrag = null)
	{
		element.ToolTip = DockTip(name);
		element.AddHandler(Mouse.PreviewMouseDownEvent, (MouseButtonEventHandler)((object s, MouseButtonEventArgs e) =>
		{
			if (e.ChangedButton != MouseButton.Left)
			{
				return;
			}
			if (dockMenuJustClosed)
			{
				dockMenuJustClosed = false;
				e.Handled = true; // 关闭菜单的那一击不触发点击动作
				return;
			}
			if (!DockInputBusy)
			{
				System.Windows.Point position = e.GetPosition(this);
				if (StartPointerItem(element, key, name, click, position, beforeDrag))
				{
					e.Handled = true;
				}
			}
		}), handledEventsToo: true);
	}

	// 记录最近打开的 Dock 菜单，并在打开时释放 Dock 自己的鼠标捕获，保证点菜单外任意位置都能关掉
	private void TrackMenu(System.Windows.Controls.ContextMenu menu)
	{
		menu.Opened += (object s, RoutedEventArgs e) =>
		{
			dockOpenMenu = menu;
			if (Mouse.Captured == this)
			{
				ReleaseMouseCapture();
			}
		};
		menu.Closed += (object s, RoutedEventArgs e) =>
		{
			if (dockOpenMenu == menu)
			{
				dockOpenMenu = null;
			}
		};
	}

	private bool CloseDockMenu()
	{
		if (dockOpenMenu != null && dockOpenMenu.IsOpen)
		{
			dockOpenMenu.IsOpen = false;
			dockOpenMenu = null;
			return true;
		}
		return false;
	}

	private bool StartDockPointer(FrameworkElement owner, Entry entry, System.Windows.Point point, Action beforeDrag)
	{
		return StartPointerItem(owner, entry.Path, entry.Name, () =>
		{
			C.Launch(entry);
		}, point, beforeDrag);
	}

	private bool StartPointerItem(FrameworkElement owner, string key, string name, Action click, System.Windows.Point point, Action beforeDrag)
	{
		if (!IsVisible || !LayoutKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
		{
			return false;
		}
		beforeDrag?.Invoke();
		dockPressActive = true;
		if (!Mouse.Capture(this, CaptureMode.Element))
		{
			dockPressActive = false;
			Store.Trace("DOCK pointer capture failed");
			return false;
		}
		StopDockLayoutMotion();
		dockPointer = new DockPointerGesture(key, point.X, point.Y, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance);
		dockPointerClick = click;
		dockPointerOwner = owner;
		dockPointerFromMenu = owner is System.Windows.Controls.MenuItem;
		owner.SetValue(DragSuppressed, true);
		dockPointerPreviousBusy = C.Board.ModalBusy;
		C.Board.ModalBusy = true;
		dockPointerLabel.Text = name;
		dockPointerWatch.Start();
		Store.Trace("DOCK pointer press");
		return true;
	}

	private void MoveDockPointer(System.Windows.Point point)
	{
		if (dockPointer == null)
		{
			return;
		}
		bool flag = dockPointer.Dragging;
		dockPointer.Move(point.X, point.Y);
		if (!dockPointer.Dragging)
		{
			return;
		}
		if (!flag)
		{
			dockIconDragging = true;
			ResetDockHover();
			if (dockPointerOwner != null)
			{
				dockPointerOwner.BeginAnimation(UIElement.OpacityProperty, null);
				dockPointerOwner.Opacity = 0.45;
			}
			Store.Trace("DOCK pointer dragging");
		}
		System.Windows.Point point2 = TranslatePoint(point, Root);
		dockPointerPreview.RenderTransform = new TranslateTransform(point2.X + 12.0, point2.Y - 36.0);
		dockPointerPreview.Visibility = Visibility.Visible;
		ShowDockDrop(InsideDock(point) ? ResolveDockDrop(dockPointer.Source, TranslatePoint(point, Items).X) : null);
	}

	private bool InsideDock(System.Windows.Point point)
	{
		if (point.X >= 0.0 && point.Y >= 0.0 && point.X <= ActualWidth)
		{
			return point.Y <= ActualHeight;
		}
		return false;
	}

	private void FinishDockPointer(System.Windows.Point point)
	{
		if (dockPointer != null)
		{
			MoveDockPointer(point);
			DockPointerGesture dockPointerGesture = dockPointer;
			bool flag = InsideDock(point);
			DockPointerResult dockPointerResult = dockPointerGesture.Release(point.X, point.Y, flag, flag ? ResolveDockDrop(dockPointerGesture.Source, TranslatePoint(point, Items).X) : null, dockPointerFromMenu | flag);
			Action action = dockPointerClick;
			ClearDockPointer();
			bool flag2 = false;
			if (dockPointerResult.Target != null)
			{
				flag2 = C.ReorderDock(dockPointerGesture.Source, dockPointerResult.Target.Path, dockPointerResult.Target.Before);
			}
			if (!flag2 && !dockPointerResult.Click)
			{
				Refresh(capture: false);
			}
			Store.Trace("DOCK pointer release dragged=" + dockPointerResult.Dragged + " inside=" + flag + " candidate=" + (dockPointerResult.Target != null) + " click=" + dockPointerResult.Click + " changed=" + flag2);
			if (dockPointerResult.Click)
			{
				action?.Invoke();
			}
		}
	}

	private void ClearDockPointer()
	{
		FrameworkElement owner = dockPointerOwner;
		dockPointer = null;
		dockPointerClick = null;
		dockPointerOwner = null;
		dockIconDragging = false;
		dockPressActive = false;
		dockPointerWatch.Stop();
		C.Board.ModalBusy = dockPointerPreviousBusy;
		try
		{
			ClearDockMarkers();
			dockPointerPreview.Visibility = Visibility.Collapsed;
			dockPointerLabel.Text = "";
			if (owner == null)
			{
				return;
			}
			owner.BeginAnimation(UIElement.OpacityProperty, null);
			owner.Opacity = 1.0;
			if (!Dispatcher.HasShutdownStarted)
			{
				Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() =>
				{
					owner.SetValue(DragSuppressed, false);
				}));
			}
		}
		finally
		{
			if (Mouse.Captured == this)
			{
				Mouse.Capture(null);
			}
		}
	}

	private void RouteDockPointer(Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			CancelDockPointer(refresh: false);
			if (IsVisible && !C.ShuttingDown)
			{
				Refresh(capture: false);
			}
		}
	}

	private void CancelDockPointer(bool refresh = true)
	{
		if (dockPointer != null)
		{
			dockPointer.Cancel();
			ClearDockPointer();
			Store.Trace("DOCK pointer cancelled");
			if (refresh && IsVisible && !C.ShuttingDown)
			{
				Refresh(capture: false);
			}
		}
	}

	private void ClearDockMarkers()
	{
		foreach (System.Windows.Controls.Button item in Items.Children.OfType<System.Windows.Controls.Button>())
		{
			if (!(item.Content is Grid grid))
			{
				continue;
			}
			foreach (Border item2 in from b in grid.Children.OfType<Border>()
				where b.Tag as string == "dockInsert"
				select b)
			{
				item2.Visibility = Visibility.Collapsed;
			}
		}
	}

	private void ResetDockHover()
	{
		foreach (System.Windows.Controls.Button item in Items.Children.OfType<System.Windows.Controls.Button>())
		{
			if (!(item.RenderTransform is TransformGroup transformGroup))
			{
				continue;
			}
			foreach (ScaleTransform item2 in transformGroup.Children.OfType<ScaleTransform>())
			{
				item2.BeginAnimation(ScaleTransform.ScaleXProperty, null);
				item2.BeginAnimation(ScaleTransform.ScaleYProperty, null);
				double scaleX = (item2.ScaleY = 1.0);
				item2.ScaleX = scaleX;
			}
			foreach (TranslateTransform item3 in transformGroup.Children.OfType<TranslateTransform>())
			{
				item3.BeginAnimation(TranslateTransform.YProperty, null);
				item3.Y = 0.0;
			}
		}
	}

	private void BindDockIcon(System.Windows.Controls.Button button, Entry entry)
	{
		BindDockSource(button, entry);
		AddInsertMarker(button);
	}

	private DockDropTarget ResolveDockDrop(string source, double x)
	{
		List<System.Windows.Controls.Button> list = (from b in Items.Children.OfType<System.Windows.Controls.Button>()
			where ItemKey(b) != null
			select b).ToList();
		foreach (System.Windows.Controls.Button item in list)
		{
			double x2 = item.TranslatePoint(new System.Windows.Point(0.0, 0.0), Items).X;
			if (x >= x2 && x <= x2 + item.ActualWidth)
			{
				return DockLayoutRules.OnIcon(C.Prefs, source, ItemKey(item), taskWheel.Running ? TaskKeys : null);
			}
		}
		return DockDropRules.OnGap(source, list.Select(ItemKey).ToArray(), list.Select((System.Windows.Controls.Button b) => b.TranslatePoint(new System.Windows.Point(b.ActualWidth / 2.0, 0.0), Items).X).ToArray(), x);
	}

	private void ShowDockDrop(DockDropTarget drop)
	{
		ClearDockMarkers();
		if (drop == null)
		{
			return;
		}
		List<System.Windows.Controls.Button> list = (from b in Items.Children.OfType<System.Windows.Controls.Button>()
			where ItemKey(b) != null
			select b).ToList();
		System.Windows.Controls.Button button = ((drop.Path == null) ? list.LastOrDefault() : list.FirstOrDefault((System.Windows.Controls.Button b) => string.Equals(ItemKey(b), drop.Path, StringComparison.OrdinalIgnoreCase)));
		Grid grid = ((button == null) ? null : (button.Content as Grid));
		if (grid == null)
		{
			return;
		}
		foreach (Border item in from b in grid.Children.OfType<Border>()
			where b.Tag as string == "dockInsert"
			select b)
		{
			item.HorizontalAlignment = ((drop.Path == null || !drop.Before) ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left);
			item.Visibility = Visibility.Visible;
		}
	}

	private void InitDockPointer()
	{
		Items.Background = System.Windows.Media.Brushes.Transparent;
		dockPointerLabel = new TextBlock
		{
			Foreground = System.Windows.Media.Brushes.White,
			MaxWidth = 180.0,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		dockPointerPreview = new Border
		{
			Child = dockPointerLabel,
			Padding = new Thickness(12.0, 8.0, 12.0, 8.0),
			CornerRadius = new CornerRadius(12.0),
			Background = Polish.Color("#E52A3447"),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			IsHitTestVisible = false,
			Visibility = Visibility.Collapsed
		};
		Root.Children.Add(dockPointerPreview);
		AddHandler(Mouse.PreviewMouseMoveEvent, (System.Windows.Input.MouseEventHandler)((object s, System.Windows.Input.MouseEventArgs e) =>
		{
			if (dockPointer != null)
			{
				e.Handled = true;
				RouteDockPointer(() =>
				{
					MoveDockPointer(e.GetPosition(this));
				});
			}
		}), handledEventsToo: true);
		AddHandler(Mouse.PreviewMouseUpEvent, (MouseButtonEventHandler)((object s, MouseButtonEventArgs e) =>
		{
			if (dockPointer != null && e.ChangedButton == MouseButton.Left)
			{
				e.Handled = true;
				RouteDockPointer(() =>
				{
					FinishDockPointer(e.GetPosition(this));
				});
			}
		}), handledEventsToo: true);
		AddHandler(Mouse.LostMouseCaptureEvent, (System.Windows.Input.MouseEventHandler)((object s, System.Windows.Input.MouseEventArgs e) =>
		{
			if (dockPointer != null && Mouse.Captured != this)
			{
				CancelDockPointer();
			}
		}), handledEventsToo: true);
		PreviewKeyDown += (object s, System.Windows.Input.KeyEventArgs e) =>
		{
			if (dockPointer != null && e.Key == Key.Escape)
			{
				e.Handled = true;
				CancelDockPointer();
			}
		};
		dockPointerWatch.Tick += (object s, EventArgs e) =>
		{
			if (dockPointer == null)
			{
				dockPointerWatch.Stop();
			}
			else if ((GetAsyncKeyState(27) & 0x8000) != 0)
			{
				CancelDockPointer();
			}
			else if (Mouse.Captured != this)
			{
				CancelDockPointer();
			}
			else if ((GetAsyncKeyState((!SystemInformation.MouseButtonsSwapped) ? 1 : 2) & 0x8000) == 0)
			{
				RouteDockPointer(() =>
				{
					FinishDockPointer(Mouse.GetPosition(this));
				});
			}
		};
		IsVisibleChanged += (object s, DependencyPropertyChangedEventArgs e) =>
		{
			if (!IsVisible)
			{
				CancelDockPointer(refresh: false);
			}
		};
		Closed += (object s, EventArgs e) =>
		{
			CancelDockPointer(refresh: false);
			dockPointerWatch.Stop();
		};
	}

	internal void CheckPointerReorder()
	{
		List<System.Windows.Controls.Button> list = (from b in Items.Children.OfType<System.Windows.Controls.Button>()
			where b.Tag is Entry
			select b).ToList();
		if (list.Count < 3)
		{
			throw new Exception("指针排序测试缺少可见图标");
		}
		Entry entry = (Entry)list.First().Tag;
		Entry entry2 = (Entry)list.Last().Tag;
		System.Windows.Point point = list.Last().TranslatePoint(new System.Windows.Point(list.Last().ActualWidth / 2.0, list.Last().ActualHeight / 2.0), this);
		System.Windows.Point point2 = list.First().TranslatePoint(new System.Windows.Point(list.First().ActualWidth / 2.0, list.First().ActualHeight / 2.0), this);
		if (!StartDockPointer(list.Last(), entry2, point, null))
		{
			throw new Exception("Dock 未捕获排序鼠标");
		}
		MoveDockPointer(point2);
		FinishDockPointer(point2);
		UpdateLayout();
		if (DockOrderRules.Ordered(C.Prefs)[0].Path != entry2.Path)
		{
			throw new Exception("后向前松手未提交");
		}
		list = (from b in Items.Children.OfType<System.Windows.Controls.Button>()
			where b.Tag is Entry
			select b).ToList();
		point = list.First().TranslatePoint(new System.Windows.Point(list.First().ActualWidth / 2.0, list.First().ActualHeight / 2.0), this);
		point2 = list.Last().TranslatePoint(new System.Windows.Point(list.Last().ActualWidth / 2.0, list.Last().ActualHeight / 2.0), this);
		if (!StartDockPointer(list.First(), entry2, point, null))
		{
			throw new Exception("第二次排序未捕获鼠标");
		}
		MoveDockPointer(point2);
		FinishDockPointer(point2);
		UpdateLayout();
		if (DockOrderRules.Ordered(C.Prefs).Last().Path != entry2.Path)
		{
			throw new Exception("前向后松手未提交");
		}
		list = (from b in Items.Children.OfType<System.Windows.Controls.Button>()
			where b.Tag is Entry
			select b).ToList();
		string text = string.Join("|", C.Prefs.DockOrder);
		point = list.First().TranslatePoint(new System.Windows.Point(20.0, 20.0), this);
		if (!StartDockPointer(list.First(), entry, point, null))
		{
			throw new Exception("取消测试未捕获鼠标");
		}
		MoveDockPointer(new System.Windows.Point(point.X + 30.0, point.Y));
		CancelDockPointer();
		if (string.Join("|", C.Prefs.DockOrder) != text || Mouse.Captured == this || dockPointer != null)
		{
			throw new Exception("取消后排序或捕获未恢复");
		}
	}

	[DllImport("PowrProf.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWake);

	private void AddQuickButtons()
	{
		if (C.Prefs.Dock.ShowPower && !C.Prefs.Dock.Consolidate)
		{
			AddQuickButton("glassboard://dock/power", "power", "电源", new string[3] { "睡眠", "重启", "关机" }, (int i) =>
			{
				Power(i);
			});
		}
		if (!C.Prefs.Dock.ShowNetwork || C.Prefs.Dock.Consolidate)
		{
			return;
		}
		AddQuickButton("glassboard://dock/network", "wifi", "Wi-Fi / 蓝牙", new string[4] { "控制中心 · Wi-Fi / 蓝牙 / 音量 / 亮度", "Wi-Fi 设置", "蓝牙设置", "网络设置" }, (int i) =>
		{
			try
			{
				if (i == 0)
				{
					C.ToggleControlCenter();
				}
				else
				{
					Process.Start(new ProcessStartInfo((new string[3] { "ms-settings:network-wifi", "ms-settings:bluetooth", "ms-settings:network" })[i - 1])
					{
						UseShellExecute = true
					});
				}
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show(this, ex.Message, "无法打开系统设置");
			}
		});
	}

	private void AddQuickButton(string key, string glyph, string title, string[] names, Action<int> action)
	{
		System.Windows.Controls.Button b = DockButton(glyph, null, () =>
		{
		}, title);
		System.Windows.Controls.ContextMenu menu = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		for (int num = 0; num < names.Length; num++)
		{
			int index = num;
			System.Windows.Controls.MenuItem menuItem = new System.Windows.Controls.MenuItem
			{
				Header = names[num]
			};
			menuItem.Click += (object s, RoutedEventArgs e) =>
			{
				action(index);
			};
			menu.Items.Add(menuItem);
		}
		b.ContextMenu = menu;
		TrackMenu(menu);
		Action open = () =>
		{
			menu.PlacementTarget = b;
			menu.Placement = PlacementMode.Top;
			menu.IsOpen = true;
		};
		b.Click += (object s, RoutedEventArgs e) =>
		{
			if (!DockDragSuppresses(b))
			{
				open();
			}
		};
		AddSystemButton(key, b, open);
	}

	private void Power(int action)
	{
		string text = (new string[3] { "睡眠", "重启", "关机" })[action];
		if (System.Windows.MessageBox.Show(this, "确定要" + text + "电脑吗？请先保存正在编辑的内容。", "电源 · " + text, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
		{
			return;
		}
		try
		{
			if (action == 0)
			{
				if (!SetSuspendState(hibernate: false, force: false, disableWake: false))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
			}
			else
			{
				Process.Start(new ProcessStartInfo("shutdown.exe", (action == 1) ? "/r /t 0" : "/s /t 0")
				{
					UseShellExecute = false,
					CreateNoWindow = true
				});
			}
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show(this, ex.Message, "电源操作失败");
		}
	}
}
