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

	// 图标倒影转换器：把图标位图纵向镜像（翻转+平移归位）后裁出底部 ratio 比例作为倒影图，
	// 自动跟随源 Image.Source 的异步更新（IconPipeline 回填后倒影同步出现）。
	private sealed class FlipReflectConverter : System.Windows.Data.IValueConverter
	{
		private readonly double ratio;

		public FlipReflectConverter(double ratio)
		{
			this.ratio = ratio;
		}

		public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
		{
			if (value is DrawingImage di)
			{
				// 矢量图标源：先光栅化再走位图翻转管线。
				// 尺寸以 Drawing 内容边界为准（DrawingImage.Width/Height 常为 0）。
				Rect bnds = di.Drawing.Bounds;
				if (bnds.Width <= 0.0 || bnds.Height <= 0.0 || double.IsInfinity(bnds.Width) || double.IsInfinity(bnds.Height))
				{
					return null;
				}
				int rw = Math.Max(1, (int)Math.Ceiling(bnds.Width));
				int rh = Math.Max(1, (int)Math.Ceiling(bnds.Height));
				RenderTargetBitmap rtb = new RenderTargetBitmap(rw, rh, 96.0, 96.0, PixelFormats.Pbgra32);
				DrawingVisual dv = new DrawingVisual();
				using (DrawingContext dctx = dv.RenderOpen())
				{
					dctx.DrawImage(di, new Rect(0.0, 0.0, rw, rh));
				}
				rtb.Render(dv);
				value = rtb;
			}
			if (!(value is BitmapSource bmp) || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0)
			{
				return null;
			}
			try
			{
				int w = bmp.PixelWidth;
				int h = Math.Max(1, (int)Math.Round(bmp.PixelHeight * ratio));
				// TransformedBitmap 对 CachedBitmap 等特殊源会静默输出透明，
				// 这里强制转 Bgra32 拿像素后手动镜像，任何源都稳定。
				FormatConvertedBitmap fcb = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0.0);
				int stride = w * 4;
				byte[] px = new byte[stride * bmp.PixelHeight];
				fcb.CopyPixels(px, stride, 0);
				byte[] outPx = new byte[stride * h];
				for (int y = 0; y < h; y++)
				{
					int srcY = bmp.PixelHeight - 1 - y;
					Buffer.BlockCopy(px, srcY * stride, outPx, y * stride, stride);
				}
				WriteableBitmap wb = new WriteableBitmap(w, h, 96.0, 96.0, PixelFormats.Bgra32, null);
				wb.WritePixels(new Int32Rect(0, 0, w, h), outPx, stride, 0);
				wb.Freeze();
				return wb;
			}
			catch (Exception ex)
			{
				Store.Trace("DOCK refl conv fail type=" + bmp.GetType().Name + " fmt=" + bmp.Format + " " + bmp.PixelWidth + "x" + bmp.PixelHeight + " err=" + ex.Message);
				return null;
			}
		}

		public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
		{
			throw new NotSupportedException();
		}
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
	// 普通应用图标的 DWM 实时预览：独立顶层 Window 承载卡片，缩略图宿主为覆盖在卡片槽位上的顶层小窗
	private System.Windows.Window previewWin;
	private System.Windows.Controls.StackPanel previewRows;
	private readonly System.Collections.Generic.List<System.Windows.Interop.HwndSource> previewWinHosts = new System.Collections.Generic.List<System.Windows.Interop.HwndSource>();
	private readonly System.Collections.Generic.List<IntPtr> previewWinThumbs = new System.Collections.Generic.List<IntPtr>();
	private System.Windows.Threading.DispatcherTimer previewWinTimer;
	private System.Windows.Threading.DispatcherTimer previewWinCloseTimer;
	private System.Windows.Controls.Button previewWinAnchor;
	private bool previewWinKeep;
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

	// —— 苹果式邻近放大（帧驱动引擎）——
	// 设计要点（参考 macOS Dock 的丝滑实现）：
	// 1) 输入与渲染解耦：MouseMove 只记录光标 X，不做任何布局/变换；
	//    独立的 15ms 帧循环（Render 优先级）按 smoothstep 曲线计算目标缩放，
	//    再用指数平滑（帧时间自适应）插值逼近——鼠标 125Hz 采样经过 60fps
	//    插值后不再有台阶感，快速扫动也连贯。
	// 2) 水平推开：放大图标按扩展量向两侧推开邻居（TranslateTransform），
	//    高倍数下图标不再互相挤压；窗口左右各加透明延展区吸收推开量。
	// 3) 收敛自停：所有图标稳定到目标后帧循环自动停止，零空闲开销。
	private sealed class ZoomNode
	{
		public System.Windows.Controls.Button Button;
		public ScaleTransform Scale;
		public TranslateTransform Shift;
		public double BaseCenter = double.NaN;
		public double Current = 1.0;
		public double Target = 1.0;
		public double ShiftTargetX = 0.0;
		public int Z = 0;
	}
	private readonly List<ZoomNode> zoomNodes = new List<ZoomNode>();
	private System.Windows.Threading.DispatcherTimer zoomLeaveTimer;
	private bool zoomHoverActive;
	private double zoomMouseX = double.NaN;
	// 帧计时用 Stopwatch（QueryPerformanceCounter）：DateTime.UtcNow 分辨率只有
	// ~15.6ms，帧间隔被量化成 0/15.6/31.2ms → 平滑系数 k 跟着跳台阶，慢速移动时
	// 每帧增量小看不出来，快速移动时大增量被量化成“要么不动、要么猛跳一步”，
	// 就是慢→快的瞬间卡顿的来源。
	private readonly System.Diagnostics.Stopwatch zoomClock = new System.Diagnostics.Stopwatch();
	private double zoomLastSec;
	private bool zoomReorderPending;
	// —— 悬停放大诊断（默认关闭，零开销路径只是一个 0.5s 一次的 File.Exists）——
	// zoom-test.ps1 创建 zoom-diag.on 后，帧循环把每帧状态追加到 zoom-diag.csv：
	// 帧时刻ms|帧间隔ms|鼠标X|节点Current@按钮屏幕中心X...（分号分隔）。
	private static readonly string zoomDiagFlag = System.IO.Path.Combine(Store.Folder, "zoom-diag.on");
	private static readonly string zoomDiagCsv = System.IO.Path.Combine(Store.Folder, "zoom-diag.csv");
	private bool zoomDiagOn;
	private double zoomDiagCheckedSec = -1.0;

	// —— 运行状态指示器（macOS 风格）：尺寸/圆角集中管理 ——
	// 运行中 = 图标下方小圆点；活动窗口 = 稍长的胶囊形指示条（覆盖圆点显示）。
	// 指示器放在图标内容块正下方，与内容块留出 RunMarkerGap 间距；
	// 颜色按 Dock 玻璃背景合成后的明暗自动切换：深色底用白色、浅色底用黑色。
	private const double RunDotWidth = 5.0;
	private const double RunDotHeight = 4.0;
	private const double RunDotRadius = 2.0;
	private const double RunActiveWidth = 14.0;
	private const double RunActiveHeight = 4.0;
	private const double RunActiveRadius = 2.0;
	private const double RunMarkerGap = 4.0;
	// Bottom 对齐的 WPF 元素要用负底边距向下延伸；标记和空隙占用按钮现有的底部留白。
	private const double RunMarkerBottomMargin = -(RunMarkerGap + RunDotHeight);

	// 指示器颜色缓存：仅当背景合成明暗变化时重建画刷（不读屏幕像素、不逐帧计算）
	private System.Windows.Media.Color runMarkerColorCache = System.Windows.Media.Colors.Transparent;
	private System.Windows.Media.Brush runMarkerBrush;

	private AutoHidePolicy hidePolicy = new AutoHidePolicy();

	private bool renderedHidden;

	private double shownTop;

	// 运行期记忆的"贴底"位置：任务栏隐藏/显示会改变 WorkingArea 高度，
	// 默认贴底的 Dock 每次 Refresh 都会跟着漂移；首次定位后固定住，
	// 只有显示设置变化或用户主动"重置位置/改 DockGap"才重新贴底。
	private double? settledBottom;

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

	private string folderSig = "";

	private bool externalDockDrag;

	private bool boardPinDragging;
	private bool desktopRaised;

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

	// 指示器过渡动画：只有状态真实变化才启动；markerWanted 记录每个标记的目标可见性，
	// 隐藏动画完成时按它决定是否折叠（快速来回切换时不会被旧动画的完成回调误折叠）。
	private readonly Dictionary<Border, bool> markerWanted = new Dictionary<Border, bool>();
	private static readonly TimeSpan RunMarkerAnimDuration = TimeSpan.FromMilliseconds(100.0);
	private static readonly CubicEase RunMarkerEase = new CubicEase
	{
		EasingMode = EasingMode.EaseOut
	};

	// 前台事件可能连续到达（快速 Alt-Tab）：合并到单个 UI 节拍，避免排队多次刷新。
	private bool runUpdatePending;

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
		DesktopCoverGuard.Track(this, (bool r) =>
		{
			desktopRaised = r;
			ApplyTopmost();
		});
		ContentGrid.Children.Add(Items);
		MouseMove += OnDockZoomMove;
		MouseLeave += OnDockZoomLeave;
		InitDockPointer();
		InitializeBackgroundMove();
		InitTaskDock();
		RunningWindows.InitForegroundTracking();
		RunningWindows.ForegroundChanged += OnRunningForegroundChanged;
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
			if (!C.GlassSuppressed && (IsVisible || (C.Board != null && C.Board.IsVisible)) && (C.Prefs.Dock.ShowRunning || taskWheel.Running))
			{
				// 增量更新：只改指示器可见性（纯快照读取，无扫描），前台切换 1 秒内跟上
				UpdateRunning();
			}
			if (!C.GlassSuppressed && (IsVisible || (C.Board != null && C.Board.IsVisible)) && (C.Prefs.Dock.ShowRunning || taskWheel.Running) && !((DateTime.UtcNow - lastRunningScan).TotalSeconds < (double)(renderedHidden ? 10 : 1.5)))
			{
				lastRunningScan = DateTime.UtcNow;
				RunningWindows.Scan();
				RefreshTaskSnapshot();
				UpdateRunning();
				C.Board?.UpdateRunning();
			}
			// 文件夹聚合按钮：打开≥1 个资源管理器窗口时出现在 Dock 尾部，全关即消失。
			// 出现/消失是结构变化：立即重建（用户盯着 Dock 等按钮出现时，鼠标悬停防护会永久卡住初始出现）；
			// 数量增减时保留空闲防护，避免打断拖拽/悬停。
			List<IntPtr> folderHandles = RunningWindows.ExplorerFolders().Select((RunningWindow w) => w.Handle).OrderBy((IntPtr h) => h.ToInt64()).ToList();
			string folderSigNow = string.Join(",", folderHandles);
			if (!string.Equals(folderSigNow, folderSig, StringComparison.Ordinal))
			{
				Store.Trace("FOLDER dock tick count=" + folderHandles.Count + " prev=" + folderSig.Length);
			}
			bool folderEdgeChange = string.IsNullOrEmpty(folderSig) != string.IsNullOrEmpty(folderSigNow);
			if (!string.Equals(folderSigNow, folderSig, StringComparison.Ordinal) && (folderEdgeChange || (!DockInputBusy && !dragging && !externalDockDrag && (C.Board == null || !C.Board.ModalBusy) && (ContextMenu == null || !ContextMenu.IsOpen) && (previewPopup == null || !previewPopup.IsOpen) && !Items.Children.OfType<System.Windows.Controls.Button>().Any((System.Windows.Controls.Button b) => b.IsMouseOver))))
			{
				Store.Trace("FOLDER dock tick apply count=" + folderHandles.Count + " prev=" + folderSig.Length + " edge=" + folderEdgeChange);
				folderSig = folderSigNow;
				Refresh(capture: false);
			}
		};
		runningTimer.Start();
		SourceInitialized += (object s, EventArgs e) =>
		{
			try
			{
			Store.Trace("HOTKEY SourceInitialized begin");
			source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
			source.AddHook(Hook);
			hotkeys = new HotkeyManager(RegisterKey, (int id) =>
			{
				Native.UnregisterHotKey(source.Handle, id);
			});
			Store.Trace("HOTKEY init start count=" + C.Prefs.Hotkeys.Count + " enabled=" + C.Prefs.Hotkeys.Count((HotkeySetting k) => k.Enabled));
			List<string> list = hotkeys.Start(C.Prefs.Hotkeys);
			Store.Trace("HOTKEY init done active=" + hotkeys.Active.Count + " errors=" + list.Count);
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
			}
			catch (Exception ex)
			{
				Store.Log(new Exception("HOTKEY init failed", ex));
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
			C.Board?.HideWithConsent();
			settledBottom = null;
			Refresh();
		}));
	}

	// 主动要求下次刷新重新贴底：设置里「重置 Dock 位置」与调整「距工作区底边」时调用。
	public void ResetSettledTop()
	{
		settledBottom = null;
	}

	// 调整「距工作区底边」时的实时跟随：只重算贴底位置并移动窗口，
	// 不动内容、不重建按钮——滑块拖动中每帧即时生效（轻量，无迟滞）。
	public void ApplyDockGap()
	{
		if (DockInputBusy)
		{
			return;
		}
		Rect rect = dockWorkingArea;
		if (rect.IsEmpty)
		{
			rect = DesktopWindowPolicy.WorkArea(this);
		}
		double defaultTop = (settledBottom.HasValue ? (settledBottom.Value - Height) : (rect.Bottom - Height - C.Prefs.DockGap));
		Top = FeatureRules.Position(C.Prefs.Placement.DockTop ?? defaultTop, rect.Top, rect.Height, Height);
		settledBottom = Top + Height;
		shownTop = Top;
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
		StopRunMarkerAnimations();
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
		ApplyTopmost();
		revealSerial++;
		BeginAnimation(Window.TopProperty, null);
		renderedHidden = false;
		hidePolicy = new AutoHidePolicy();
		moveGrip.Visibility = (C.Prefs.Placement.DockLocked ? Visibility.Collapsed : Visibility.Visible);
		moveGrip.Cursor = (C.Prefs.Placement.DockLocked ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.SizeAll);
		moveGrip.ToolTip = DockTip(C.Prefs.Placement.DockLocked ? "右键 Dock → 允许自由拖动 Dock" : "拖动把手或空白区域移动 Dock，松手保存");
		StopRunMarkerAnimations();
		Items.Children.Clear();
		// 重建图标的同时清空徽标注册表：DockButton 会重新注册并恢复通知状态
		notifyBadges.Clear();
		notifyIcons.Clear();
		// 按钮重建后缩放注册表同样重新注册（苹果式邻近放大）；
		// ArrangeDockItems 会按用户布局重排视觉顺序，注册顺序失效，
		// 帧循环捕获 BaseCenter 后按视觉 X 重排一次（见 OnZoomFrameTick）。
		zoomNodes.Clear();
		zoomReorderPending = true;
		Items.Margin = new Thickness(dock.HoverPadding());
		Surface.CornerRadius = new CornerRadius(dock.Radius);
		Rect rect = (dockWorkingArea = DesktopWindowPolicy.WorkArea(this));
		dockScreenArea = DesktopWindowPolicy.WorkArea(this, pointer: false, fullScreen: true);
		// hExtra（悬停放大水平延展）也占用窗口宽度：VisiblePinned 的可用宽度必须扣掉它，
		// 否则按满宽预算的应用+系统按钮总数超出窗口，右端内置图标（此电脑/电源等）被裁出窗口。
		double zoomS0 = Math.Min(2.0, 1.0 + (dock.Zoom - 1.0) * MotionLevel);
		double hExtra0 = Math.Max(0.0, Math.Min(40.0, Math.Ceiling((zoomS0 - 1.0) * (dock.IconSize + 16.0) * 0.9)));
		List<Entry> list = DockOrderRules.Ordered(C.Prefs);
		int num = (taskWheel.Running ? taskGroups.Count : list.Count);
		int num2 = dock.VisiblePinned(rect.Width - 20.0 - hExtra0, num);
		int num3 = ((num > num2) ? Math.Max(0, num2 - 1) : num2);
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
				C.Board?.HideWithConsent();
				Native.Computer();
			}, "此电脑"), () =>
			{
				C.Board?.HideWithConsent();
				Native.Computer();
			});
		}
		if (dock.ShowRecycle && !dock.Consolidate)
		{
			// 回收站：单击打开系统回收站窗口；把文件拖到图标上直接送入回收站（可恢复）
			AddSystemButton("glassboard://dock/recycle", DockButton("recycle", null, () =>
			{
				C.Board?.HideWithConsent();
				Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
				{
					UseShellExecute = true
				});
			}, "回收站"), () =>
			{
				C.Board?.HideWithConsent();
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
		if (dock.ShowDesktopIcons)
		{
			string tip = C.Prefs.HideDesktopIcons ? "显示桌面图标" : "隐藏桌面图标";
			AddSystemButton("glassboard://dock/desktop-icons", DockButton(null, null, () =>
			{
				C.ShellAction(2);
			}, tip, null, "desktop"), () =>
			{
				C.ShellAction(2);
			});
		}
		if (dock.ShowGameMode)
		{
			string tip = C.Prefs.GameMode ? "游戏模式已开启，点击退出" : "进入游戏模式";
			AddSystemButton("glassboard://dock/gamemode", DockButton(null, null, () =>
			{
				C.SetGameMode(!C.Prefs.GameMode);
			}, tip, null, "gamepad"), () =>
			{
				C.SetGameMode(!C.Prefs.GameMode);
			});
		}
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
		// 文件夹聚合按钮不依赖"运行列表"开关：只要开着资源管理器窗口就显示
		AddFolderButton();
		ArrangeDockItems();
		double num4 = dock.IconSize + 16.0 + (double)(dock.ShowLabels ? 18 : 0);
		// 倒影需要窗口高度预算（与按钮高度同步），否则倒影下缘被窗口硬切（用户看到"直接裁剪"而非渐隐）。
		double reflPadH = (dock.Reflections && !dock.ShowLabels && DockGlassFullyTransparent()) ? (Math.Max(6.0, Math.Floor(dock.IconSize * 0.50)) + 1.0) : 0.0;
		// 悬停放大需要的顶部空间由窗口上方的透明延展区提供：窗口高度 = 玻璃高度 + zoomExtra，
		// 玻璃与图标位置完全不动，只有悬停图标放大时伸入透明带。dock 的视觉位置永远不受 Zoom 影响。
		double zoomS = Math.Min(2.0, 1.0 + (dock.Zoom - 1.0) * MotionLevel);
		// 注意：基线必须是常数 14.0（GlassWindow 构造时 Root.Margin=14），不能读 Root.Margin.Top——
		// Refresh 每轮会把它改成 14+zoomExtra，下次 Refresh 就会把 zoomExtra 递归算塌成 0。
		// 按钮高度 = num4 + reflPadH（有倒影时更高），放大原点 (0.5,0.8) 在按钮上，顶部上升量按按钮全高计算。
		double zoomExtra = Math.Max(0.0, Math.Min(56.0, Math.Ceiling((0.8 * (num4 + reflPadH) - (num4 - dock.IconSize) * 0.5) * (zoomS - 1.0) - (14.0 + (num4 - dock.IconSize) * 0.5)) + 2.0));
		// 水平方向同理：放大图标把邻居向两侧推开，推开量由窗口左右对称的透明延展区吸收，
		// 玻璃条宽度与位置不受 Zoom 影响。放大上限约每侧 31px，40 的延展上限足够。
		double hExtra = Math.Max(0.0, Math.Min(40.0, Math.Ceiling((zoomS - 1.0) * (dock.IconSize + 16.0) * 0.9)));
		Root.Margin = new Thickness(14.0 + hExtra * 0.5, 14.0 + zoomExtra, 14.0 + hExtra * 0.5, Root.Margin.Bottom);
		PlacementOptions placement = C.Prefs.Placement;
		// 已保存过位置时，按保存坐标所在屏幕的工作区钳制：多显示器下 Dock 不会被
		// 启动瞬间鼠标所在屏幕"拉走"（WorkArea 的 pointer 模式按鼠标定位屏幕），位置跨启动稳定。
		// 必须在 Measure/Width 之前完成，宽度上限与居中基准都用锚定屏的工作区。
		if (placement.DockLeft.HasValue || placement.DockCenter.HasValue)
		{
			double anchorX = placement.DockCenter ?? placement.DockLeft.Value;
			double anchorY = placement.DockTop ?? (rect.Bottom - 60.0);
			Rect anchoredRect = DesktopWindowPolicy.WorkAreaAt(this, anchorX, anchorY);
			if (!anchoredRect.IsEmpty)
			{
				rect = (dockWorkingArea = anchoredRect);
			}
		}
		// 宽度按内容实测（不再用 WidthFor 公式估算——系统项计数与实际按钮数不一致时两端会多出大段留白）
		Items.Measure(new System.Windows.Size(rect.Width, double.PositiveInfinity));
		Width = Math.Min(rect.Width - 20.0, 28.0 + Items.DesiredSize.Width + hExtra);
		Height = 28.0 + num4 + dock.Spacing * 2.0 + 6.0 + zoomExtra + reflPadH;
		double num6;
		if (dock.Alignment != 0)
		{
			num6 = ((dock.Alignment == 2) ? (rect.Right - Width - 10.0) : (rect.Left + (rect.Width - Width) / 2.0));
		}
		else
		{
			num6 = rect.Left + 10.0;
		}
		if (placement.DockLeft.HasValue && !placement.DockCenter.HasValue)
		{
			placement.DockCenter = placement.DockLeft.Value + Width / 2.0;
		}
		Left = DockAnchorRules.Left(placement.DockLeft.HasValue ? placement.DockCenter : ((double?)null), Width, num6 + dock.Offset, rect.Left, rect.Width);
		// 未手动拖过 Dock 时贴工作区底边；settledBottom 锁定首次贴底结果，
		// 避免任务栏隐藏/显示引起的工作区变化让 Dock 上下漂移。
		// 窗口高度随 zoomExtra 变化时锚定底边，玻璃底边与图标位置完全不动，
		// 放大空间由窗口上方的透明延展区吸收。
		double defaultTop = (settledBottom.HasValue ? (settledBottom.Value - Height) : (rect.Bottom - Height - C.Prefs.DockGap));
		Top = FeatureRules.Position(C.Prefs.Placement.DockTop ?? defaultTop, rect.Top, rect.Height, Height);
		settledBottom = Top + Height;
		shownTop = Top;
		if (!placement.DockLeft.HasValue)
		{
			// 从未手动拖过 Dock：把本次推导出的水平位置固化为持久位置，
			// 下次启动回到同一屏幕、同一位置（自检模式 Save 为 no-op）。
			// 垂直位置不固化：永远按「距工作区底边」(DockGap) 实时推导，
			// 调整 DockGap 后重启依然贴底（用户拖动 Dock 时 FinishDockMove 才写 DockTop）。
			placement.DockLeft = Left;
			placement.DockCenter = Left + Width / 2.0;
			C.Save();
		}
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
			// 模糊已烘进底图（捕获时一次性模糊），这里只同步裁剪/圆角/材质层裁剪，
			// 不再挂运行时 BlurEffect（透明窗上的 GPU 高斯模糊是 banding 污斑来源之一）。
			RefreshBackdropStyle();
		}
		System.Windows.Media.Color color = Accent(dock.Accent);
		Surface.BorderBrush = ((GlassPercent == 0.0) ? ((System.Windows.Media.Brush)System.Windows.Media.Brushes.Transparent) : ((System.Windows.Media.Brush)new LinearGradientBrush(System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 160.0 * EdgeStrength), color.R, color.G, color.B), System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 30.0 * EdgeStrength), byte.MaxValue, byte.MaxValue, byte.MaxValue), 90.0)));
		UpdateRunning();
		if (!capture)
		{
			AnimateDockLayout(left, width);
		}
	}

	// 设置页实时预览（透明度/模糊/材质/圆角滑块）走这里：只更新玻璃层与表面属性，
	// 不清空重建整排图标。预览每个 tick 都重建会闪烁，并打断图标提取回填。
	public void RefreshAppearance(bool capture = false)
	{
		if (DockInputBusy)
		{
			return;
		}
		DockOptions dock = C.Prefs.Dock;
		dock.Normalize();
		LightTheme = C.Prefs.Light;
		MenuTheme.Apply(ContextMenu, C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		MotionEnabled = dock.MotionEnabled;
		MotionLevel = dock.MotionLevel;
		ConfigureGlass(dock);
		ApplyTopmost();
		Items.Margin = new Thickness(dock.HoverPadding());
		Surface.CornerRadius = new CornerRadius(dock.Radius);
		dockWorkingArea = DesktopWindowPolicy.WorkArea(this);
		// 预览期间不重算窗口宽高：拖“图标尺寸/间距”滑杆时若窗口尺寸连续变化
		// 而图标行未重建，会出现窗口与内容不匹配的畸形（两端空档反复变宽/收缩）。
		// 尺寸统一等设置面板关闭后由完整 Refresh() 一次性重算并重建图标行。
		GlassPercent = dock.OpacityPercent;
		if (capture)
		{
			Backdrop(C.Prefs.Light);
		}
		else
		{
			SetGlassOpacity(GlassPercent);
			// 模糊已烘进底图（捕获时一次性模糊），这里只同步裁剪/圆角/材质层裁剪。
			RefreshBackdropStyle();
		}
		System.Windows.Media.Color color = Accent(dock.Accent);
		Surface.BorderBrush = ((GlassPercent == 0.0) ? ((System.Windows.Media.Brush)System.Windows.Media.Brushes.Transparent) : ((System.Windows.Media.Brush)new LinearGradientBrush(System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 160.0 * EdgeStrength), color.R, color.G, color.B), System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 30.0 * EdgeStrength), byte.MaxValue, byte.MaxValue, byte.MaxValue), 90.0)));
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
			bool flag2 = hidePolicy.Update(DateTime.UtcNow, C.Prefs.Dock.AutoHide, over, edge, ((dragging || DockInputBusy || externalDockDrag) | flag) || C.UiBusy || (C.Board != null && C.Board.IsVisible), C.Prefs.Dock.HideDelay);
			if (flag2 != renderedHidden)
			{
				renderedHidden = flag2;
				double target = (flag2 ? (bottom - 20.0) : shownTop);
				AnimateDockVisibility(flag2, target);
			}
		}
	}

	// 前台切换即时更新：SetWinEventHook 回调（UI 线程）→ 合并进单个 UI 节拍 →
	// 立即重算指示器并顺带触发一次扫描（RunningWindows 内部 1.2s 窗口扫描节流），
	// 新开进程也能尽快进入窗口快照。
	private void OnRunningForegroundChanged()
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke((Action)OnRunningForegroundChanged);
			return;
		}
		if (runUpdatePending)
		{
			return;
		}
		runUpdatePending = true;
		Dispatcher.BeginInvoke((Action)(() =>
		{
			runUpdatePending = false;
			if (C.GlassSuppressed || (!IsVisible && (C.Board == null || !C.Board.IsVisible)))
			{
				return;
			}
			if (!C.Prefs.Dock.ShowRunning && !taskWheel.Running)
			{
				return;
			}
			UpdateRunning();
			C.Board?.UpdateRunning();
			RunningWindows.Scan();
		}));
	}

	// 运行指示器过渡：出现=淡入+0.8→1.0 缩放；消失=淡出（完成后再折叠）。
	// 从当前呈现值续接（快速切换不跳变）；禁用动效时直接落最终状态。
	private void AnimateRunMarker(Border marker, bool show)
	{
		if (marker == null)
		{
			return;
		}
		if (!(marker.RenderTransform is ScaleTransform scale))
		{
			scale = new ScaleTransform(1.0, 1.0);
			marker.RenderTransform = scale;
			marker.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		}
		bool firstTime = !markerWanted.ContainsKey(marker);
		markerWanted[marker] = show;
		if (!MotionEnabled)
		{
			// 关闭动效：取消残留动画，直接落最终状态（Visibility/透明度/缩放一次性到位）
			marker.BeginAnimation(UIElement.OpacityProperty, null);
			scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			scale.ScaleX = 1.0;
			scale.ScaleY = 1.0;
			marker.Opacity = (show ? 1.0 : 0.0);
			marker.Visibility = (show ? Visibility.Visible : Visibility.Collapsed);
			return;
		}
		if (show)
		{
			marker.Visibility = Visibility.Visible;
			double curO = (firstTime ? 0.0 : marker.Opacity);
			double curS = (firstTime ? 0.8 : scale.ScaleX);
			marker.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(curO, 1.0, RunMarkerAnimDuration)
			{
				EasingFunction = RunMarkerEase
			});
			scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(curS, 1.0, RunMarkerAnimDuration)
			{
				EasingFunction = RunMarkerEase
			});
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(curS, 1.0, RunMarkerAnimDuration)
			{
				EasingFunction = RunMarkerEase
			});
		}
		else
		{
			double curO = marker.Opacity;
			DoubleAnimation fadeOut = new DoubleAnimation(curO, 0.0, RunMarkerAnimDuration)
			{
				EasingFunction = RunMarkerEase
			};
			fadeOut.Completed += delegate
			{
				// 动画可能已被新的显示动画替换：只有目标仍为隐藏时才折叠
				if (markerWanted.TryGetValue(marker, out bool want) && !want)
				{
					marker.BeginAnimation(UIElement.OpacityProperty, null);
					marker.Visibility = Visibility.Collapsed;
				}
			};
			marker.BeginAnimation(UIElement.OpacityProperty, fadeOut);
		}
	}

	// 停止全部指示器过渡动画（Dock 关闭或重建前调用，避免残留动画对象）
	private void StopRunMarkerAnimations()
	{
		foreach (System.Windows.Controls.Button item in Items.Children.OfType<System.Windows.Controls.Button>())
		{
			if (item.Content is Grid grid)
			{
				foreach (Border b in grid.Children.OfType<Border>())
				{
					string tag = b.Tag as string;
					if (tag == "running" || tag == "active")
					{
						b.BeginAnimation(UIElement.OpacityProperty, null);
						if (b.RenderTransform is ScaleTransform st)
						{
							st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
							st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
						}
					}
				}
			}
		}
		markerWanted.Clear();
		runState.Clear();
	}

	// 运行指示颜色：按 Dock 玻璃背景“实际呈现”的合成明暗选择（深底→白，浅底→黑）。
	// 不读屏幕像素：复用 GlassMaterials 的底色公式（Dock 背景不再掺强调色）与透明度，桌面亮度按当前主题估计。
	private static System.Windows.Media.Color RunMarkerColor(DockOptions o, bool light)
	{
		System.Windows.Media.Color glass;
		if (string.IsNullOrEmpty(o.Material.Tint))
		{
			glass = (light ? System.Windows.Media.Color.FromRgb(234, 237, 244) : System.Windows.Media.Color.FromRgb(24, 26, 32));
		}
		else
		{
			try
			{
				glass = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(o.Material.Tint);
			}
			catch
			{
				glass = (light ? System.Windows.Media.Color.FromRgb(234, 237, 244) : System.Windows.Media.Color.FromRgb(24, 26, 32));
			}
		}
		double glassLum = (0.2126 * (double)(int)glass.R + 0.7152 * (double)(int)glass.G + 0.0722 * (double)(int)glass.B) / 255.0;
		double t = Math.Max(0.0, Math.Min(1.0, o.OpacityPercent / 100.0));
		// 半透明玻璃叠在桌面上：桌面亮度按主题估计（浅色主题桌面偏亮 0.55 / 深色主题偏暗 0.12）
		double desktopLum = (light ? 0.55 : 0.12);
		double lum = t * glassLum + (1.0 - t) * desktopLum;
		return (lum < 0.5 ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black);
	}

	private void UpdateRunning()
	{
		// 活动胶囊：当前前台窗口归属的入口显示长条，其余运行入口显示圆点。
		// 颜色按 Dock 玻璃背景合成明暗自动切换；背景/主题/透明度变化后，下一次 1 秒节拍内即更新（只重建一次画刷，不做屏幕采样）。
		System.Windows.Media.Color wantColor = RunMarkerColor(C.Prefs.Dock, C.Prefs.Light);
		bool markerColorChanged = wantColor != runMarkerColorCache;
		if (markerColorChanged)
		{
			runMarkerColorCache = wantColor;
			runMarkerBrush = new SolidColorBrush(wantColor);
		}
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
			// 只有指示状态真实变化才启动动画（0=无 1=圆点 2=活动胶囊）；
			// 圆点↔胶囊互切时两者同时动画 = 交叉淡化，其余轮询不再触碰动画。
			int newState = isActive ? 2 : (isRunning ? 1 : 0);
			if (runState.TryGetValue(entry.Path, out int oldState) && oldState == newState)
			{
				continue;
			}
			runState[entry.Path] = newState;
			bool showDot = C.Prefs.Dock.ShowRunning && newState == 1;
			bool showCapsule = C.Prefs.Dock.ShowRunning && newState == 2;
			foreach (Border item2 in from b in grid.Children.OfType<Border>()
				where b.Tag as string == "running"
				select b)
			{
				AnimateRunMarker(item2, showDot);
			}
			foreach (Border item3 in from b in grid.Children.OfType<Border>()
				where b.Tag as string == "active"
				select b)
			{
				AnimateRunMarker(item3, showCapsule);
			}
			if (RenderDiagnostics.Verbose)
			{
				RenderDiagnostics.Trace("RUN state " + (newState == 0 ? "none" : (newState == 1 ? "dot" : "active")) + " entry=" + entry.Path + " windows=" + ws.Count + " fgHwnd=" + foreground.ToInt64() + " fgPath=" + fgPath);
			}
		}
		if (markerColorChanged)
		{
			// 颜色统一刷给所有运行指示器（含任务轮模式按钮的指示条），保证一次背景变化内全部同步
			foreach (System.Windows.Controls.Button item4 in Items.Children.OfType<System.Windows.Controls.Button>())
			{
				if (item4.Content is Grid grid4)
				{
					foreach (Border b4 in grid4.Children.OfType<Border>())
					{
						string tag4 = b4.Tag as string;
						if (tag4 == "running" || tag4 == "active")
						{
							b4.Background = runMarkerBrush;
						}
					}
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

	// Dock 玻璃背景不掺强调色：强调色只用于系统图标、名称标签、插入标记等前景元素
	protected override int AccentForFill => -1;

	// Dock 图标悬停提示：纯文字、无背景底板，颜色跟随主题玻璃文字色
	private System.Windows.Controls.ToolTip DockTip(string text)
	{
		FrameworkElementFactory label = new FrameworkElementFactory(typeof(TextBlock));
		label.SetValue(TextBlock.TextProperty, text);
		label.SetValue(TextBlock.FontSizeProperty, 12.5);
		label.SetValue(TextBlock.ForegroundProperty, Polish.Ink(C.Prefs.Light));
		// 圆角胶囊底板：半透明深浅色按主题自适应，无底板纯文字在壁纸/毛玻璃上可读性差
		FrameworkElementFactory pill = new FrameworkElementFactory(typeof(Border));
		pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(8.0));
		pill.SetValue(Border.PaddingProperty, new Thickness(11.0, 6.0, 11.0, 6.0));
		pill.SetValue(Border.BackgroundProperty, new SolidColorBrush(C.Prefs.Light ? System.Windows.Media.Color.FromArgb(240, byte.MaxValue, byte.MaxValue, byte.MaxValue) : System.Windows.Media.Color.FromArgb(215, 22, 22, 28)));
		pill.AppendChild(label);
		return new System.Windows.Controls.ToolTip
		{
			PlacementTarget = null,
			Placement = PlacementMode.Custom,
			CustomPopupPlacementCallback = delegate(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
			{
				// 提示窗相对目标左上角定位：水平居中于图标正上方，留 4px 间距；屏幕边缘放不下时翻到下方
				double x = targetSize.Width / 2.0 - popupSize.Width / 2.0;
				return new CustomPopupPlacement[2]
				{
					new CustomPopupPlacement(new System.Windows.Point(x, -popupSize.Height - 4.0), PopupPrimaryAxis.Vertical),
					new CustomPopupPlacement(new System.Windows.Point(x, targetSize.Height + 4.0), PopupPrimaryAxis.Vertical)
				};
			},
			HasDropShadow = true,
			Template = new ControlTemplate(typeof(System.Windows.Controls.ToolTip))
			{
				VisualTree = pill
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
				Duration = TimeSpan.FromMilliseconds(440.0)
			};
			// 两段式弹跳：先上跳 -5 再小回弹 +2，比单段上跳更 Q 弹、不突兀
			y.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
			y.KeyFrames.Add(new EasingDoubleKeyFrame(-5.0, KeyTime.FromPercent(0.35), new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			}));
			y.KeyFrames.Add(new EasingDoubleKeyFrame(2.0, KeyTime.FromPercent(0.68), new CubicEase
			{
				EasingMode = EasingMode.EaseInOut
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
			// 移除旧倒影（若有）后补齐新图标倒影：倒影是 Convert 一次性生成，
			// 绑定旧图标元素的旧倒影在新图标替换后不会自动更新。
			for (int i = sp.Children.Count - 1; i >= 0; i--)
			{
				if (sp.Children[i] is System.Windows.Controls.Image ri && (string)ri.Tag == "icon-reflection")
				{
					sp.Children.RemoveAt(i);
				}
			}
			if (C.Prefs.Dock.Reflections && !C.Prefs.Dock.ShowLabels && DockGlassFullyTransparent())
			{
				// 异步回填真实图标后补齐倒影（构建时是文字占位符没有倒影）
				AddIconReflection(sp, image, C.Prefs.Dock.IconSize);
			}
			notifyIcons[entry.Path] = image;
		}
	}

	// 倒影仅在 Dock 玻璃全透明（UI 透明度 100%，内部不透明度 0）时启用；
	// 统一面板开启时读全局统一值（那里拉到 100% 也算），否则读 Dock 自己的值。
	private static long reflJudgeMs;
	private bool DockGlassFullyTransparent()
	{
		// 统一模式读 GlobalStyle 原始值（Prefs.OpacityPercent 被 ApplyGlobalStyle clamp 到 ≥10，
		// 读它会永远 false）；非统一读 Dock 自己的值。0=全透明（UI 100%）。
		double effective = C.Prefs.GlobalStyle.Unify ? C.Prefs.GlobalStyle.OpacityPercent : C.Prefs.Dock.OpacityPercent;
		long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
		if (nowMs - reflJudgeMs > 1000)
		{
			reflJudgeMs = nowMs;
			Store.Trace("REFL judge effective=" + effective.ToString("F1") + " unify=" + (C.Prefs.GlobalStyle.Unify ? "true" : "false") + " dock=" + C.Prefs.Dock.OpacityPercent.ToString("F1") + " globalRaw=" + C.Prefs.GlobalStyle.OpacityPercent.ToString("F1") + " globalClamped=" + (C.Prefs.OpacityPercent.HasValue ? C.Prefs.OpacityPercent.Value.ToString("F1") : "null"));
		}
		return effective <= 0.5;
	}

	// 图标倒影：位图管线镜像（TransformedBitmap 翻转+裁剪）+ 自上而下渐隐。
	// 不用 VisualBrush（按钮 BitmapCache 下会空白）、不用 RenderTransform 翻转（与 ClipToBounds 组合在分层窗口渲染异常）。
	private void AddIconReflection(StackPanel sp, System.Windows.Controls.Image img, double iconSize)
	{
		double reflHeight = Math.Max(6.0, Math.Floor(iconSize * 0.50));
		LinearGradientBrush reflMask = new LinearGradientBrush
		{
			StartPoint = new System.Windows.Point(0.5, 0.0),
			EndPoint = new System.Windows.Point(0.5, 1.0)
		};
		// 倒影蒙版按环境明暗取色：浅色主题（亮背景）用黑色渐隐；深色主题/暗壁纸用白色渐隐，
		// 否则黑色倒影叠在暗背景上完全不可见（用户反馈「全透明时看不到倒影」的视觉根因之一）。
		byte maskC = 0; // 倒影统一深色（macOS 风格），白色倒影在亮色壁纸上不可见
		reflMask.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(128, maskC, maskC, maskC), 0.0));
		reflMask.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, maskC, maskC, maskC), 1.0));
		System.Windows.Controls.Image reflImg = new System.Windows.Controls.Image
		{
			Width = iconSize,
			Height = reflHeight,
			Stretch = Stretch.Fill,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0),
			IsHitTestVisible = false,
			OpacityMask = reflMask,
			Tag = "icon-reflection"
		};
		// 绑定源图标：异步回填/换源时倒影自动重算（一次性 Convert 在源为空或未就绪时会永久缺失）。
		System.Windows.Data.Binding reflBind = new System.Windows.Data.Binding("Source")
		{
			Source = img,
			Converter = new FlipReflectConverter(0.50)
		};
		reflImg.SetBinding(System.Windows.Controls.Image.SourceProperty, reflBind);
		sp.Children.Add(reflImg);
	}

	// 矢量元素（Path/Canvas 等内置线条图标）渲染为位图：
	// 4x 超采样（96 画布渲染 24 系图标）再缩放，线条更饱满清晰，且能走位图倒影管线。
	private System.Windows.Controls.Image Bitmapize(FrameworkElement el, double size)
	{
		try
		{
			// 用元素自身设计尺寸渲染（Glyph 返回 size×size 的 Grid+Viewbox），
			// 之前固定 24 会把整套内容压成 24px 再摊到 96 画布上，显示出来只有 1/4 大小。
			double ew = double.IsNaN(el.Width) ? size : el.Width;
			double eh = double.IsNaN(el.Height) ? size : el.Height;
			el.Measure(new System.Windows.Size(ew, eh));
			el.Arrange(new Rect(0.0, 0.0, ew, eh));
			RenderTargetBitmap rtb = new RenderTargetBitmap((int)Math.Ceiling(ew), (int)Math.Ceiling(eh), 96.0, 96.0, PixelFormats.Pbgra32);
			rtb.Render(el);
			rtb.Freeze();
			System.Windows.Controls.Image image = new System.Windows.Controls.Image
			{
				Source = rtb,
				Width = size,
				Height = size,
				Stretch = Stretch.Uniform
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
			return image;
		}
		catch
		{
			return null;
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
		// 有倒影时按钮加高：图标+倒影（图标 50%）总高度超过 iconSize+16，
		// StackPanel 溢出会把图标挤偏/裁边（用户反馈「裁剪原有图标」）。
		double reflPad = (o.Reflections && !o.ShowLabels && DockGlassFullyTransparent()) ? (Math.Max(6.0, Math.Floor(iconSize * 0.50)) + 1.0) : 0.0;
		button.Height = iconSize + 16.0 + (double)(o.ShowLabels ? 18 : 0) + reflPad;
		button.Margin = new Thickness(o.Spacing / 2.0, 0.0, o.Spacing / 2.0, 0.0);
		// 悬浮提示：无背景纯文字名称（跟随玻璃文字颜色），不带气泡底板
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
		// 点击后按钮会获得焦点，WPF 默认焦点视觉会画一个虚线方块；dock 图标不需要它。
		button.FocusVisualStyle = null;
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
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),
				FontSize = Math.Round(iconSize * 0.72),
				Width = iconSize,
				Height = iconSize,
				TextAlignment = TextAlignment.Center,
				Foreground = TextBrush,
				VerticalAlignment = VerticalAlignment.Center,
				// 整数像素对齐 + Display 度量：避免字形图标亚像素发虚/裁边
				UseLayoutRounding = true
			};
			TextOptions.SetTextFormattingMode(element, TextFormattingMode.Display);
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
		// 矢量系统图标（Feather/线条 Path）渲染为位图：获得与应用图标一致的倒影管线，
		// 且 4x 超采样后放大更饱满清晰（细线图标在全透明亮壁纸上不再"隐形"）。
		if (element != null && !(element is System.Windows.Controls.Image) && !(element is TextBlock))
		{
			System.Windows.Controls.Image bmpEl = Bitmapize(element, iconSize);
			if (bmpEl != null)
			{
				element = bmpEl;
			}
		}
		StackPanel stackPanel = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center
		};
		stackPanel.Children.Add(element);
		double runReflPad = reflPad;
		if (reflPad > 0.0)
		{
			// 有倒影时运行指示贴着图标下缘（倒影之上），不与倒影重叠
			if (element is System.Windows.Controls.Image img)
			{
				AddIconReflection(stackPanel, img, iconSize);
			}
		}
		TextBlock dockLabel = null;
		if (o.ShowLabels)
		{
			// 名称自适应显示：基础 12px，放不下时逐级缩小字号（最低 9px、小字号加粗）优先显示完整名称；
			// 宽度上限 = 相邻图标中心距(图标宽+间距) - 2px：间距越大字号越大、名称越清晰，相邻标签间至少 2px 空隙、永不重叠。
			string labelText = text switch
			{
				"···" => "更多", 
				"⚙" => "设置", 
				"▦" => "应用", 
				"computer" => "此电脑", 
				"control" => "控制中心", 
				_ => tip, 
			};
			double capWidth = iconSize + 16.0 + o.Spacing - 2.0;
			dockLabel = new TextBlock
			{
				Text = labelText,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),
				FontSize = 12.0,
				Foreground = new System.Windows.Media.SolidColorBrush(IconPacks.AccentColor(C.Prefs.Dock.Accent)),
				TextAlignment = TextAlignment.Center,
				TextTrimming = TextTrimming.CharacterEllipsis,
				MaxWidth = double.PositiveInfinity,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				Margin = new Thickness(0.0, 4.0, 0.0, 0.0),
				// 整数像素对齐：避免亚像素偏移让文字发虚
				UseLayoutRounding = true
			};
			TextOptions.SetTextFormattingMode(dockLabel, TextFormattingMode.Display);
			// 测量时必须放开 MaxWidth，否则 MaxWidth 会把 DesiredSize 截断到上限，字号自适应永不生效。
			for (double fontSize = 12.0; fontSize >= 9.0; fontSize -= 1.0)
			{
				dockLabel.FontSize = fontSize;
				dockLabel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
				if (dockLabel.DesiredSize.Width <= capWidth)
				{
					break;
				}
			}
			if (dockLabel.FontSize <= 9.0)
			{
				// 9px 是下限（再小看不清）；小字号加粗保证笔画完整
				dockLabel.FontWeight = FontWeights.SemiBold;
			}
			dockLabel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
			dockLabel.Width = Math.Ceiling(Math.Min(dockLabel.DesiredSize.Width, capWidth));
			dockLabel.MaxWidth = capWidth;
			stackPanel.Children.Add(dockLabel);
		}
		Grid grid2 = new Grid
		{
			IsHitTestVisible = false
		};
		grid2.Children.Add(stackPanel);
		if (o.IconRipple)
		{
			// 点击波纹：按下图标时从鼠标点击点向外扩散的圆形水波，约束在内容区域内；
			// 走 Preview 通道不拦截 Click，按钮行为完全不变。波纹颜色取图标前景色。
			Border rippleHost = new Border
			{
				IsHitTestVisible = false,
				ClipToBounds = true
			};
			grid2.Children.Add(rippleHost);
			System.Windows.Media.Brush rippleBrush = new SolidColorBrush((TextBrush is SolidColorBrush tb2) ? tb2.Color : System.Windows.Media.Color.FromArgb(110, byte.MaxValue, byte.MaxValue, byte.MaxValue));
			button.PreviewMouseLeftButtonDown += (object s, System.Windows.Input.MouseButtonEventArgs e) =>
			{
				// 用户按下图标=准备启动/激活，立即取消悬停预览计时器并关闭已弹出的预览卡，
				// 避免"点图标时预览窗闪现"（悬停 380ms 后预览开火，点击瞬间又关闭）。
				if (previewWinTimer != null)
				{
					previewWinTimer.Stop();
					previewWinTimer = null;
				}
				if (previewWin != null)
				{
					CloseEntryPreview();
				}
				if (dockIconDragging || !MotionEnabled || !o.IconRipple)
				{
					return;
				}
				System.Windows.Point p = e.GetPosition(rippleHost);
				// 波纹直径封顶在按钮内容宽度内：超过会被 ClipToBounds 裁成方块，
				// 动效强度拉得越高越明显。封顶后始终呈现完整圆形水波。
				double waveSize = Math.Min(iconSize * 2.2 * Math.Max(0.6, MotionLevel), iconSize + 12.0);
				System.Windows.Shapes.Ellipse wave = new System.Windows.Shapes.Ellipse
				{
					Width = waveSize,
					Height = waveSize,
					HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
					VerticalAlignment = VerticalAlignment.Top,
					Margin = new Thickness(p.X - waveSize / 2.0, p.Y - waveSize / 2.0, 0.0, 0.0),
					Fill = rippleBrush,
					Opacity = 0.32,
					IsHitTestVisible = false,
					RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
					RenderTransform = new ScaleTransform(0.01, 0.01)
				};
				rippleHost.Child = wave;
				CubicEase ease = new CubicEase
				{
					EasingMode = EasingMode.EaseOut
				};
				DoubleAnimation grow = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(340.0))
				{
					EasingFunction = ease
				};
				DoubleAnimation fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(300.0))
				{
					BeginTime = TimeSpan.FromMilliseconds(140.0)
				};
				grow.Completed += delegate
				{
					// 扩散结束后清理波纹；新一轮点击会先覆盖 Child 再播动画
					if (ReferenceEquals(rippleHost.Child, wave))
					{
						rippleHost.Child = null;
					}
				};
				wave.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
				wave.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
				wave.BeginAnimation(UIElement.OpacityProperty, fadeOut);
			};
		}
		if (entry != null)
		{
			button.Tag = entry;
			// 内容 Grid 只按图标/可选标签的高度居中；Bottom 对齐时正底边距会把标记往上推，
			// 因此用负底边距把它延伸到内容块下方，并在图标或标签后留出 RunMarkerGap。
			// 倒影开启时按钮整体变高、内容块居中，指示点自动落在倒影正下方，无需额外补偿。
			System.Windows.Media.Color markerColor = RunMarkerColor(o, C.Prefs.Light);
			runMarkerColorCache = markerColor;
			runMarkerBrush = new SolidColorBrush(markerColor);
			grid2.Children.Add(new Border
			{
				Tag = "running",
				Width = RunDotWidth,
				Height = RunDotHeight,
				CornerRadius = new CornerRadius(RunDotRadius),
				Background = runMarkerBrush,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Bottom,
				Margin = new Thickness(0.0, 0.0, 0.0, RunMarkerBottomMargin + runReflPad),
				RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
				RenderTransform = new ScaleTransform(1.0, 1.0),
				Visibility = Visibility.Collapsed
			});
			// 活动窗口指示条：当前前台窗口所属应用显示长胶囊（覆盖圆点），颜色与圆点一致（背景明暗对比色）
			grid2.Children.Add(new Border
			{
				Tag = "active",
				Width = RunActiveWidth,
				Height = RunActiveHeight,
				CornerRadius = new CornerRadius(RunActiveRadius),
				Background = runMarkerBrush,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Bottom,
				Margin = new Thickness(0.0, 0.0, 0.0, RunMarkerBottomMargin + runReflPad),
				RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
				RenderTransform = new ScaleTransform(1.0, 1.0),
				Visibility = Visibility.Collapsed
			});
			if (RenderDiagnostics.Verbose)
			{
				// 指示器创建诊断：确认圆点/胶囊进了可视树且尺寸正常
				RenderDiagnostics.Trace("RUN marker created entry=" + entry.Path + " dot=" + RunDotWidth + "x" + RunDotHeight + " capsule=" + RunActiveWidth + "x" + RunActiveHeight + " bottomMargin=" + RunMarkerBottomMargin + " iconSize=" + iconSize + " color=" + markerColor);
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
		// 悬停只放大、不上浮：上浮会把按钮移离光标，光标停在图标下方（标签下缘）时
		// MouseEnter/MouseLeave 互相触发形成死循环，图标会不停跳动。
		// 放大锚点偏下（0.8）：顶部溢出由窗口上方的透明延展区（zoomExtra）吸收。
		// 变换组 = 先缩放（绕自身锚点）后水平推开（Shift），推开量不受缩放影响。
		TranslateTransform shift = new TranslateTransform(0.0, 0.0);
		TransformGroup zoomTf = new TransformGroup();
		zoomTf.Children.Add(scale);
		zoomTf.Children.Add(shift);
		button.RenderTransform = zoomTf;
		button.RenderTransformOrigin = new System.Windows.Point(0.5, 0.8);
		button.UseLayoutRounding = true;
		// 不再用 BitmapCache：RenderTransform 缩放时 WPF 每帧按新分辨率重新光栅化纹理，
		// 快速来回划动时 GPU 纹理反复重建/上传，与视频壁纸的合成带宽互相争抢 → 渐进式卡顿
		// （移出 Dock 动画停表后恢复）。当前帧驱动 + 指数平滑下 CPU 光栅缩放成本可忽略。
		button.CacheMode = null;
		// 苹果式邻近放大：注册进帧驱动引擎（OnDockZoomMove 记录光标，OnZoomFrameTick 统一驱动）
		zoomNodes.Add(new ZoomNode
		{
			Button = button,
			Scale = scale,
			Shift = shift
		});
		if (entry != null)
		{
			// 悬浮预览：悬停 380ms 后弹出任务栏式实时缩略图（DWM Thumbnail 管线，与任务模式按钮同一套）。
			// 系统内置图标只用普通名称提示（ToolTip），不弹卡片。
			AttachPreview(button, entry);
			BindDockIcon(button, entry);
		}
		return button;
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

	// —— 苹果式邻近放大：帧驱动引擎 ——
	// MouseMove 只负责记录光标 X 并唤醒帧循环；所有缩放计算、平滑插值、
	// 水平推开与赋值都在 15ms 帧循环里批量完成（与渲染节奏同步）。
	private void OnDockZoomMove(object s, System.Windows.Input.MouseEventArgs e)
	{
		if (!MotionEnabled || DockInputBusy || dockPressActive || dragging || dockIconDragging || externalDockDrag)
		{
			return;
		}
		double amp = Math.Min(1.0, (C.Prefs.Dock.Zoom - 1.0) * MotionLevel);
		if (amp <= 0.001)
		{
			return;
		}
		if (zoomLeaveTimer != null)
		{
			zoomLeaveTimer.Stop();
		}
		zoomHoverActive = true;
		zoomMouseX = e.GetPosition(Items).X;
		EnsureZoomFrame();
	}

	private bool zoomRenderingHooked;
	private bool zoomAnimating;
	private bool zoomShadowStripped;
	// 恢复被缩放动画摘掉的软渲染阴影（与 SetGlassOpacity 的判定条件保持一致）
	private void RestoreZoomShadow()
	{
		if (!zoomShadowStripped)
		{
			return;
		}
		zoomShadowStripped = false;
		ApplySurfaceShadow(true);
	}
	private void EnsureZoomFrame()
	{
		// CompositionTarget.Rendering：每帧渲染前恰好回调一次，与渲染管线严格同步
		// （DispatcherTimer 会与渲染帧错位：一帧内 tick 两次或整帧错过=浪费 pass/掉帧）。
		if (!zoomRenderingHooked)
		{
			zoomRenderingHooked = true;
			CompositionTarget.Rendering += OnZoomFrameTick;
		}
		zoomAnimating = true;
		zoomClock.Restart();
		zoomLastSec = 0.0;
	}

	// 帧循环：smoothstep 计算目标 → 指数平滑插值 → 缩放 + 水平推开 → 收敛自停。
	private void OnZoomFrameTick(object sender, EventArgs e)
	{
		if (!zoomAnimating)
		{
			return;
		}
		double now = ((RenderingEventArgs)e).RenderingTime.TotalSeconds;
		double dt = Math.Min(0.05, Math.Max(0.001, now - zoomLastSec));
		zoomLastSec = now;
		if (!MotionEnabled || zoomNodes.Count == 0)
		{
			zoomAnimating = false;
			return;
		}
		if (DockInputBusy || dockPressActive || dragging || dockIconDragging || externalDockDrag)
		{
			return;
		}
		// 动画期间摘掉软渲染阴影（DropShadowEffect 每帧 CPU 模糊整窗，是划动掉帧的最大来源）；
		// 收敛后恢复。阴影摘挂一次性的，只在开关沿触发。
		if (!zoomShadowStripped)
		{
			zoomShadowStripped = true;
			ApplySurfaceShadow(false);
		}
		double amp = Math.Min(1.0, (C.Prefs.Dock.Zoom - 1.0) * MotionLevel);
		double radius = Math.Max(40.0, C.Prefs.Dock.IconSize * 1.6);
		// 1) 目标缩放：smoothstep（s²(3-2s)）曲线——macOS Dock 同款手感，比余弦更自然
		// 每帧重捕获 BaseCenter：按钮位置在窗口宽度动画/布局未稳时会漂移，
		// 一次性捕获会把瞬态坐标当永久锚点（悬停左边缘空白却放大第一个图标）。
		// TranslatePoint 开销可忽略，自愈一切瞬态位置问题。
		foreach (ZoomNode n in zoomNodes)
		{
			double w = n.Button.ActualWidth;
			if (w <= 0.0)
			{
				continue;
			}
			n.BaseCenter = n.Button.TranslatePoint(new System.Windows.Point(w * 0.5, 0.0), Items).X - n.Shift.X;
			double t = 1.0;
			if (zoomHoverActive && !double.IsNaN(zoomMouseX) && amp > 0.001)
			{
				double dx = Math.Abs(zoomMouseX - n.BaseCenter);
				double u = Math.Max(0.0, 1.0 - dx / radius);
				double fall = u * u * (3.0 - 2.0 * u);
				t = 1.0 + amp * fall;
			}
			n.Target = t;
		}
		// 2) 指数平滑 + 速度上限：k 随真实帧间隔自适应（时间常数约 30ms——跟手且无台阶）。
		// 只钳制“放大”方向（8 倍/秒）：鼠标瞬间快速移动时，图标以恒定速度平滑升起，
		// 而不是指数猛冲形成“甩鞭”式的突然快跳。
		// “缩小”方向不钳制：指数衰减本身就是平滑的（时间连续、无台阶），快速落下
		// 是自然的重力感——鼠标快速滑过后图标立即回落，而不是悬空一会儿再慢慢降。
		double k = 1.0 - Math.Exp(-dt / 0.030);
		double maxD = dt * 14.0;
		foreach (ZoomNode n in zoomNodes)
		{
			double d = (n.Target - n.Current) * k;
			if (d > 0.0 && d > maxD)
			{
				d = maxD;
			}
			n.Current += d;
		}
		// 2.5) 视觉顺序校正：Refresh 重建按钮时按构建顺序注册，而 ArrangeDockItems
		// 随后按用户布局重排 Items 子元素——注册顺序 ≠ 视觉顺序，按注册序推邻居
		// 会推到视觉上不相邻的图标（悬浮最左会推开最右的图标，反之亦然）。
		// 所有节点捕获完 BaseCenter（视觉 X 坐标）后按它重排一次，邻接即真实邻接。
		if (zoomReorderPending)
		{
			bool ready = true;
			foreach (ZoomNode n in zoomNodes)
			{
				if (double.IsNaN(n.BaseCenter))
				{
					ready = false;
					break;
				}
			}
			if (ready)
			{
				zoomNodes.Sort((ZoomNode a, ZoomNode b) => a.BaseCenter.CompareTo(b.BaseCenter));
				zoomReorderPending = false;
			}
		}
		// 3) 邻居让位已移除：悬停放大时只有被悬停的图标放大（ZIndex 提升压在邻居上方），
		// 其它图标保持原位不动——用户反馈"悬停左边图标时右边图标跟着移动"很别扭。
		// 放大图标向两侧的溢出量 = (zoom-1)*iconSize/2，zoom≤1.5 时不重叠邻居（间距含 16px 空隙），
		// 更大 zoom 时靠 ZIndex 覆盖保证可读性。Shift 恒 0，仅为保持结构保留平滑循环。
		double kShift = 1.0 - Math.Exp(-dt / 0.055);
		foreach (ZoomNode n in zoomNodes)
		{
			n.Shift.X += (0.0 - n.Shift.X) * kShift;
			// ZIndex 仅在穿越阈值时更新一次，避免每帧 27 次 SetZIndex 引发不必要的子元素重排
			int z = (n.Current > 1.05) ? 1 : 0;
			if (n.Z != z)
			{
				n.Z = z;
				System.Windows.Controls.Panel.SetZIndex(n.Button, z);
			}
		}
		bool settle = true;
		foreach (ZoomNode n in zoomNodes)
		{
			if (Math.Abs(n.Target - n.Current) > 0.0015)
			{
				settle = false;
			}
			if (Math.Abs(n.Shift.X - n.ShiftTargetX) > 0.25)
			{
				settle = false;
			}
			if (Math.Abs(n.Scale.ScaleX - n.Current) > 0.0005)
			{
				n.Scale.ScaleX = n.Current;
				n.Scale.ScaleY = n.Current;
			}
		}
		// 4) 全部稳定 → 停帧（常驻 Rendering 订阅靠 flag 早退，零空闲开销）；下次 MouseMove 自动唤醒
		if (settle)
		{
			zoomAnimating = false;
			zoomHoverActive = false;
			RestoreZoomShadow();
		}
		// —— 诊断落盘（默认关闭）：zoom-diag.on 存在时每帧写一行 ——
		if (now - zoomDiagCheckedSec > 0.5)
		{
			zoomDiagCheckedSec = now;
			zoomDiagOn = System.IO.File.Exists(zoomDiagFlag);
		}
		if (zoomDiagOn)
		{
			try
			{
				System.Text.StringBuilder sb = new System.Text.StringBuilder();
				sb.Append((int)Math.Round(now * 1000.0)).Append('|').Append((int)Math.Round(dt * 1000.0)).Append('|').Append((int)Math.Round(zoomMouseX));
				foreach (ZoomNode n in zoomNodes)
				{
					sb.Append('|').Append(n.Current.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('@');
					System.Windows.Point sc = n.Button.PointToScreen(new System.Windows.Point(n.Button.ActualWidth * 0.5, n.Button.ActualHeight * 0.5));
					sb.Append((int)Math.Round(sc.X));
				}
				System.IO.File.AppendAllText(zoomDiagCsv, sb.AppendLine().ToString());
			}
			catch (System.Exception ex)
			{
				zoomDiagOn = false;
				Store.Trace("ZM diag FAIL " + ex.Message);
			}
		}
	}

	// 光标离开 Dock：120ms 宽限后仍不在窗口内才收回（吸收视觉树刷新造成的伪离场）。
	private void OnDockZoomLeave(object s, System.Windows.Input.MouseEventArgs e)
	{
		if (!MotionEnabled)
		{
			return;
		}
		if (zoomLeaveTimer == null)
		{
			zoomLeaveTimer = new System.Windows.Threading.DispatcherTimer
			{
				// 60ms：只吸收视觉树刷新造成的伪离场（真离场时 IsMouseOver 检查兜底），
				// 不再用 120ms——真离开时图标会“悬空”太久才落下，观感是卡。
				Interval = TimeSpan.FromMilliseconds(60.0)
			};
			zoomLeaveTimer.Tick += (object s2, EventArgs e2) =>
			{
				zoomLeaveTimer.Stop();
				if (IsMouseOver || Items.IsMouseOver)
				{
					return;
				}
				zoomHoverActive = false;
				foreach (ZoomNode n in zoomNodes)
				{
					// 鼠标离开后恢复文字气泡（点击时被禁用防常驻）。
					ToolTipService.SetIsEnabled(n.Button, true);
				}
				EnsureZoomFrame();
			};
		}
		zoomLeaveTimer.Stop();
		zoomLeaveTimer.Start();
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
				if (array.Length == 0)
				{
					Store.Trace("DOCK drop empty formats=" + DropFormats(e.Data));
				}
				if (array.Length != 0)
				{
					e.Handled = true;
					e.Effects = System.Windows.DragDropEffects.None;
					try
					{
						// 回收站命中检测：文件（FileDrop）与入口（面板磁贴等 Entry 格式）都生效。
						// 命中按钮区域时：文件送入系统回收站；入口从面板与 Dock 移除（可撤销）。
						System.Windows.Controls.Button recycle = Items.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault((System.Windows.Controls.Button b) => string.Equals(ItemKey(b), RecycleBin.Clsid, StringComparison.OrdinalIgnoreCase) || string.Equals(ItemKey(b), "glassboard://dock/recycle", StringComparison.OrdinalIgnoreCase));
						if (recycle != null)
						{
							System.Windows.Point point = e.GetPosition(recycle);
							if (point.X >= 0.0 && point.Y >= 0.0 && point.X <= recycle.ActualWidth && point.Y <= recycle.ActualHeight)
							{
								ClearExternalDockDrag();
								string[] files = (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? ((string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)) : null);
								if (files != null && files.Length != 0)
								{
									int deleted = C.DeleteToRecycleBin(files);
									ShowTaskHint((deleted > 0) ? ("已删除 " + deleted + " 项到回收站") : "未找到可删除的文件");
								}
								else
								{
									int removed = C.RemoveEntryPathsForRecycle(array);
									ShowTaskHint((removed > 0) ? ("已移除 " + removed + " 个入口 · 可撤销") : "该入口已不在面板");
								}
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
			ClosePreview();
			CloseEntryPreview();
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
			// 进入运行列表时强制取新快照；缓存窗口内直接 ReadTaskGroups 会先显示上一拍的程序，
			// 随后再被后台刷新替换，看起来就是“每次结果不一样”。
			RunningWindows.Scan(force: true);
			RunningWindows.EnsureTrayReady();
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

	// 托盘/后台进程常见 exe 后缀：同产品的「主程序 + Tray/Core/Helper…」据此归并。
	private static readonly string[] ExeSuffixes = new string[15]
	{
		"TrayIcon", "Tray", "Agent", "Helper", "Service", "Daemon", "Worker", "Core",
		"Main", "Background", "Bg", "Notifier", "Watcher", "Guard", "Updater"
	};

	// 去掉 exe 主名末尾的托盘/后台后缀：JianyingProTray→JianyingPro、FlClashCore→FlClash。
	// 只去一层（常见场景），并保证结果至少 3 个字符，避免过度归并。
	private static string StripExeSuffix(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return "";
		}
		foreach (string suffix in ExeSuffixes)
		{
			if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
			{
				string stripped = name.Substring(0, name.Length - suffix.Length);
				if (stripped.Length >= 3)
				{
					return stripped;
				}
			}
		}
		return name;
	}

	// 分组键：按「安装目录 + 去后缀基名」聚合，而非完整路径。
	// 同一产品的多个 exe（主程序 + Tray/后台）归为一个图标，对齐任务栏按 AppUserModelID 聚合同一产品的行为。
	private static string TaskKey(RunningWindow window)
	{
		string path = window.Path ?? "";
		string fileName = Path.GetFileName(path);
		if (string.IsNullOrEmpty(path) || fileName.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
		{
			return "window:" + window.Handle.ToInt64();
		}
		string dir = Path.GetDirectoryName(path) ?? "";
		string baseName = StripExeSuffix(Path.GetFileNameWithoutExtension(path));
		return "dir:" + dir.ToLowerInvariant() + "|" + baseName.ToLowerInvariant();
	}

	private void ReadTaskGroups()
	{
		// 运行列表只收「有可见窗口」或「确实占用系统托盘」的程序。
		// SnapshotWithBackground 还会把无窗口的后台进程（服务、更新器、常驻引擎等）算进来，
		// 与任务栏托盘的可见程序严重不一致。
		taskGroups = RunningWindows.Snapshot().GroupBy(TaskKey, StringComparer.OrdinalIgnoreCase).OrderBy((IGrouping<string, RunningWindow> g) => g.Key, StringComparer.OrdinalIgnoreCase)
			.Select((IGrouping<string, RunningWindow> g) =>
			{
				// 主程序窗口排最前（非 Tray 优先，再按有无句柄）：名称与激活都取主程序，
				// 避免 Tray 进程抢占显示名（JianyingProTray 应显示成 JianyingPro）。
				// 同组内主窗口按面积优先：透明标题栏/小浮层即使漏进来，也不会抢走程序名和图标。
				List<RunningWindow> windows = g.OrderBy((RunningWindow w) => w.Tray ? 1 : 0).ThenByDescending((RunningWindow w) => w.Area).ThenBy((RunningWindow w) => (w.Handle == IntPtr.Zero) ? 1 : 0).ThenBy((RunningWindow w) => w.Handle.ToInt64()).ToList();
				string fileNameWithoutExtension = StripExeSuffix(Path.GetFileNameWithoutExtension(windows[0].Path ?? ""));
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
		return string.Join("|", from w in RunningWindows.Snapshot()
			orderby w.Handle.ToInt64()
			select w.Handle.ToInt64() + ":" + w.Path + ":" + w.Title);
	}

	private void RefreshTaskSnapshot()
	{
		if (taskWheel.Running && !DockInputBusy && !externalDockDrag && !dragging && (C.Board == null || !C.Board.ModalBusy) && (ContextMenu == null || !ContextMenu.IsOpen) && (previewPopup == null || !previewPopup.IsOpen) && !Items.Children.OfType<System.Windows.Controls.Button>().Any((System.Windows.Controls.Button b) => b.IsMouseOver || (b.ContextMenu != null && b.ContextMenu.IsOpen)) && !(taskSignature == TaskSignature()))
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
					Tag = "running",
					Width = ((item.Windows.Count > 1) ? 12 : 5),
					Height = 4.0,
					CornerRadius = new CornerRadius(2.0),
					Background = new SolidColorBrush(RunMarkerColor(C.Prefs.Dock, C.Prefs.Light)),
					HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Bottom,
					Margin = new Thickness(0.0, 0.0, 0.0, RunMarkerBottomMargin),
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
			C.Board?.CollapseSmooth();
			return;
		}
		if (target.Tray)
		{
			// 纯托盘程序（无可见窗口）：直接重新启动
			try
			{
				Process.Start(target.Path);
			}
			catch
			{
			}
			ShowTaskHint("正在启动 " + group.Name);
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
			C.Board?.CollapseSmooth();
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

	private static string DropFormats(System.Windows.IDataObject data)
	{
		try
		{
			return string.Join("|", data.GetFormats(false));
		}
		catch
		{
			return "";
		}
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
		if (e.RoutedEvent == System.Windows.DragDrop.PreviewDragEnterEvent && DockPayload(e.Data).Length == 0)
		{
			Store.Trace("DOCK over rejected formats=" + DropFormats(e.Data));
		}
		if (DockInputBusy || DockPayload(e.Data).Length == 0)
		{
			e.Effects = System.Windows.DragDropEffects.None;
			e.Handled = true;
			return;
		}
		e.Handled = true;
		System.Windows.Controls.Button recycle = Items.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault((System.Windows.Controls.Button b) => string.Equals(ItemKey(b), RecycleBin.Clsid, StringComparison.OrdinalIgnoreCase) || string.Equals(ItemKey(b), "glassboard://dock/recycle", StringComparison.OrdinalIgnoreCase));
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
			ApplyTopmost();
		}
	}

	private void ApplyTopmost()
	{
		bool on2 = desktopRaised || C.Prefs.Dock.AlwaysOnTop || C.Prefs.Dock.AutoHide || boardPinDragging;
		if (Topmost != on2)
		{
			Store.Trace("DOCK topmost on=" + on2 + " raised=" + desktopRaised);
			Topmost = on2;
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

	// 文件夹聚合：打开≥1 个资源管理器窗口时自动出现在 Dock 尾部，全关即消失。
	// 单击=在打开的文件夹窗口间轮换（任务栏语义），悬停=窗口实时预览（TaskGroup 管线）。
	private void AddFolderButton()
	{
		List<RunningWindow> folders = RunningWindows.ExplorerFolders();
		if (folders.Count == 0)
		{
			return;
		}
		Store.Trace("FOLDER button add count=" + folders.Count);
		TaskGroup group = new TaskGroup
		{
			Key = "folder:",
			Name = "文件夹",
			Windows = folders
		};
		System.Windows.Controls.Button button = DockButton(null, CustomIcons.Folder(), delegate
		{
			ActivateFolder(folders);
		}, (folders.Count > 1) ? ("文件夹 · " + folders.Count + " 个窗口") : "文件夹");
		button.SetValue(LayoutKey, "glassboard://dock/folder");
		button.Tag = group;
		AttachPreview(button, group);
		Items.Children.Add(button);
	}

	private void ActivateFolder(List<RunningWindow> folders)
	{
		if (folders.Count == 0)
		{
			return;
		}
		IntPtr fg = RunningWindows.EffectiveForeground();
		int idx = -1;
		for (int i = 0; i < folders.Count; i++)
		{
			if (folders[i].Handle == fg)
			{
				idx = i;
				break;
			}
		}
		// 前台已是文件夹窗口 → 轮换到下一个；否则激活最近的一个（枚举顺序=Z 序反序）。
		RunningWindow target = folders[(idx >= 0) ? ((idx + 1) % folders.Count) : 0];
		RunningWindows.ActivateHandle(target.Handle);
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
		// 每次打开都取新快照，并确保托盘索引已就绪：可见窗口和托盘常驻程序
		// 必须来自同一拍数据，否则会出现“这次 7 个、下次 2 个”。
		RunningWindows.Scan(force: true);
		RunningWindows.EnsureTrayReady();
		ReadTaskGroups();
		List<TaskGroup> groups = taskGroups.OrderBy((TaskGroup g) => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
		Store.Trace("RUNNINGLIST groups=" + groups.Count);
		(System.Windows.Media.Color bg, System.Windows.Media.Color ink, System.Windows.Media.Color line, System.Windows.Media.Color hover) = MenuTheme.Palette(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		// 图标网格（仿 Windows 原生「隐藏图标」溢出面板）：每个运行程序一格，
		// 左键=切回窗口，右键=任务栏式菜单，悬停 ToolTip 显示程序名。
		WrapPanel panel = new WrapPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			ItemWidth = 44.0,
			ItemHeight = 44.0,
			MaxWidth = 264.0,
			Margin = new Thickness(3.0)
		};
		foreach (TaskGroup g in groups)
		{
			TaskGroup selected = g;
			string suffix = selected.Windows.All((RunningWindow w) => w.Tray) ? "（托盘）" : ((selected.Windows.Count > 1) ? ("（" + selected.Windows.Count + " 个窗口）") : "");
			System.Windows.Controls.Border row = new System.Windows.Controls.Border
			{
				Width = 38.0,
				Height = 38.0,
				Margin = new Thickness(3.0),
				CornerRadius = new CornerRadius(9.0),
				Background = System.Windows.Media.Brushes.Transparent,
				Cursor = System.Windows.Input.Cursors.Hand,
				ToolTip = selected.Name + suffix
			};
			System.Windows.Controls.Image rowIcon = new System.Windows.Controls.Image
			{
				Width = 24.0,
				Height = 24.0,
				Stretch = Stretch.Uniform,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
			row.Child = rowIcon;
			Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
			{
				try
				{
					rowIcon.Source = RunningWindows.Icon(selected.Windows[0]);
				}
				catch
				{
				}
			}));
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
				// 右键 = GlassBoard 自己的菜单（不唤起系统溢出面板）：任务栏式窗口操作 + 退出。
				System.Windows.Controls.ContextMenu rowMenu = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
				System.Windows.Controls.MenuItem AddMenu(string header, Action act)
				{
					System.Windows.Controls.MenuItem mi = new System.Windows.Controls.MenuItem
					{
						Header = header
					};
					mi.Click += (object ss, RoutedEventArgs ee) =>
					{
						close();
						act();
					};
					rowMenu.Items.Add(mi);
					return mi;
				}
				RunningWindow primary = selected.Windows.FirstOrDefault((RunningWindow w) => w.Handle != IntPtr.Zero) ?? selected.Windows[0];
				if (selected.Windows.Count == 1)
				{
					AddMenu("显示窗口", () => RunningWindows.ActivateHandle(primary.Handle));
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
				if (primary.Handle != IntPtr.Zero)
				{
					rowMenu.Items.Add(new Separator());
					if (RunningWindows.IsMinimized(primary.Handle))
					{
						AddMenu("还原", () => RunningWindows.Restore(primary.Handle));
					}
					else
					{
						AddMenu("最小化", () => RunningWindows.Minimize(primary.Handle));
					}
					if (RunningWindows.IsMaximized(primary.Handle))
					{
						AddMenu("还原", () => RunningWindows.Restore(primary.Handle));
					}
					else
					{
						AddMenu("最大化", () => RunningWindows.Maximize(primary.Handle));
					}
					AddMenu("关闭窗口", () => RunningWindows.Close(primary.Handle));
				}
				rowMenu.Items.Add(new Separator());
				AddMenu("退出程序", () => KillTask(selected));
				row.ContextMenu = rowMenu;
				panel.Children.Add(row);
			}
		FrameworkElement shellContent;
		if (groups.Count == 0)
		{
			shellContent = new TextBlock
			{
				Text = "暂无正在运行的程序",
				Foreground = new System.Windows.Media.SolidColorBrush(ink),
				FontSize = 12.5,
				Margin = new Thickness(16.0, 12.0, 16.0, 12.0)
			};
		}
		else
		{
			ScrollViewer scroller = new ScrollViewer
			{
				VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
				MaxHeight = 380.0,
				Content = panel
			};
			shellContent = scroller;
		}
		Border shell = new Border
		{
			CornerRadius = new CornerRadius(12.0),
			Background = new System.Windows.Media.SolidColorBrush(bg),
			BorderBrush = new System.Windows.Media.SolidColorBrush(line),
			BorderThickness = new Thickness(1.0),
			Child = shellContent,
			MinWidth = 48.0
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
		// 点击浮层外任意位置（含 Dock 本体）收起：StaysOpen=false 只对失焦可靠，
		// 点 Dock 本身等场景会漏收，这里统一走全局鼠标钩子。
		PanelDismissGuard.TrackPopup(popup, close);
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
		List<int> pids = group.Windows.Select((RunningWindow w) => (int)w.ProcessId).Where((int p) => p > 0).Distinct().ToList();
		Task.Run(delegate
		{
			// 1) 优雅关闭：对每个窗口句柄发 WM_CLOSE。托盘程序的隐藏窗口也能收到（比 CloseMainWindow
			//    只认主窗口更可靠），程序有机会走正常退出流程清理自己的子进程/服务。
			foreach (RunningWindow w in group.Windows)
			{
				try
				{
					if (w.Handle != IntPtr.Zero)
					{
						RunningWindows.Close(w.Handle);
					}
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			}
			// 给优雅退出留时间
			System.Threading.Thread.Sleep(500);
			// 2) 强杀进程树：taskkill /F /T 连子进程一起杀（Steam→steamwebhelper 这类父子守护）。
			//    失败（权限不足/服务进程）记日志，不再静默吞。
			foreach (int pid in pids)
			{
				ForceKill(pid);
			}
			Dispatcher.BeginInvoke(new Action(delegate
			{
				RunningWindows.Scan();
				ReadTaskGroups();
				Refresh(capture: false);
			}));
		});
	}

	// 强杀单个进程及其子进程树；失败（权限不足/服务进程）记日志，不静默吞。
	private static void ForceKill(int pid)
	{
		try
		{
			ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/F /T /PID " + pid)
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				WindowStyle = ProcessWindowStyle.Hidden
			};
			using (Process t = Process.Start(psi))
			{
				if (t == null)
				{
					return;
				}
				t.WaitForExit(6000);
				if (t.ExitCode != 0)
				{
					Store.Trace("KILL taskkill failed pid=" + pid + " exit=" + t.ExitCode);
				}
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
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
			"glassboard://dock/desktop-icons" => "桌面图标",
			"glassboard://dock/gamemode" => "游戏模式",
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
			"glassboard://dock/desktop-icons" => "desktop",
			"glassboard://dock/gamemode" => "gamepad",
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
				Background = new System.Windows.Media.SolidColorBrush(IconPacks.AccentColor(C.Prefs.Dock.Accent)),
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
		// 文件夹聚合按钮固定排在 Dock 最右（用户语义：整合打开的文件夹窗口，跟随运行列表按钮之后）
		List<System.Windows.Controls.Button> tail = list.Where((System.Windows.Controls.Button b) => string.Equals(ItemKey(b), "glassboard://dock/folder", StringComparison.OrdinalIgnoreCase)).ToList();
		int num = list2.FindLastIndex((System.Windows.Controls.Button b) => !DockLayoutRules.SystemItem(ItemKey(b)));
		list2.InsertRange((num < 0) ? Math.Min(2, list2.Count) : (num + 1), collection);
		list2.AddRange(tail);
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
			if (owner is Entry entry)
			{
				ShowEntryPreview(button, entry);
				return;
			}
			if (owner is TaskGroup group)
			{
				ShowGroupPreview(button, group);
				return;
			}
			Store.Trace("PREVIEW mouseenter");
			PreviewHover(button, owner);
		};
		button.MouseLeave += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			if (owner is Entry || owner is TaskGroup)
			{
				EntryPreviewLeave();
				return;
			}
			PreviewLeave();
		};
	}

	// ==================== 普通应用图标预览：独立顶层窗口 + DWM 缩略图 ====================
	// Popup 管线（任务模式）在此环境实测从未完成 Arrange（slot 永远 0x0），因此普通图标改用
	// Window 承载卡片（Window 显示后布局必然完成），缩略图宿主用独立顶层小窗覆盖槽位。
	private void ShowEntryPreview(System.Windows.Controls.Button button, Entry entry)
	{
		if (DockInputBusy || dockIconDragging || externalDockDrag)
		{
			Store.Trace("PREVIEW entry enter blocked busy=" + DockInputBusy + " drag=" + dockIconDragging + " ext=" + externalDockDrag);
			return;
		}
		if (previewWin != null && previewWin.IsVisible && previewWinAnchor == button)
		{
			if (previewWinCloseTimer != null)
			{
				previewWinCloseTimer.Stop();
			}
			return;
		}
		CloseEntryPreview();
		previewWinAnchor = button;
		// 每次新建计时器：闭包必须捕获本次的 entry（字段缓存 timer 会把第一个图标带进所有后续悬停）
		System.Windows.Threading.DispatcherTimer t = new System.Windows.Threading.DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(380.0)
		};
		t.Tick += (object s, EventArgs e) =>
		{
			t.Stop();
			System.Windows.Controls.Button b = previewWinAnchor;
			Store.Trace("PREVIEW entry timer fire b=" + (b != null) + " over=" + (b != null && b.IsMouseOver));
			if (b != null && b.IsMouseOver)
			{
				OpenEntryPreview(b, entry);
			}
		};
		previewWinTimer = t;
		t.Start();
	}

	private void EntryPreviewLeave()
	{
		if (previewWinTimer != null)
		{
			previewWinTimer.Stop();
		}
		if (previewWin == null || !previewWin.IsVisible)
		{
			previewWinAnchor = null;
			return;
		}
		// 光标仍停在图标或卡片上时保持打开
		if ((previewWinAnchor != null && previewWinAnchor.IsMouseOver) || previewWinKeep)
		{
			return;
		}
		if (previewWinCloseTimer == null)
		{
			previewWinCloseTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(300.0)
			};
			previewWinCloseTimer.Tick += (object s, EventArgs e) =>
			{
				previewWinCloseTimer.Stop();
				if (!previewWinKeep && (previewWinAnchor == null || !previewWinAnchor.IsMouseOver))
				{
					CloseEntryPreview();
				}
			};
		}
		previewWinCloseTimer.Stop();
		previewWinCloseTimer.Start();
	}

	private void OpenEntryPreview(System.Windows.Controls.Button button, Entry entry)
	{
		CloseEntryPreview();
		List<RunningWindow> wins = new List<RunningWindow>();
		try
		{
			RunningWindows.Scan();
			// 过滤 Handle=0 的"后台进程"条目与隐藏窗口（关闭到托盘的微信主窗等：
			// DWM 对隐藏窗口缩略图不更新，点击也落空；恢复走 Dock 图标点击路径）
			wins = RunningWindows.For(entry).Where((RunningWindow w) => w.Handle != IntPtr.Zero && IsWindowVisible(w.Handle)).ToList();
		}
		catch (Exception ex)
		{
			Store.Trace("PREVIEW entry scan failed: " + ex.Message);
		}
		Store.Trace("PREVIEW entry open " + entry.Name + " wins=" + wins.Count);
		if (wins.Count == 0)
		{
			return;
		}
		BuildPreviewWindow(button, entry.Name, wins, entry);
	}

	// 分组按钮预览（文件夹聚合/运行列表 TaskGroup）：与普通图标同一条独立 Window 管线。
	private void ShowGroupPreview(System.Windows.Controls.Button button, TaskGroup group)
	{
		if (DockInputBusy || dockIconDragging || externalDockDrag)
		{
			Store.Trace("PREVIEW group enter blocked busy=" + DockInputBusy + " drag=" + dockIconDragging + " ext=" + externalDockDrag);
			return;
		}
		if (previewWin != null && previewWin.IsVisible && previewWinAnchor == button)
		{
			if (previewWinCloseTimer != null)
			{
				previewWinCloseTimer.Stop();
			}
			return;
		}
		CloseEntryPreview();
		previewWinAnchor = button;
		System.Windows.Threading.DispatcherTimer t = new System.Windows.Threading.DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(380.0)
		};
		t.Tick += (object s, EventArgs e) =>
		{
			t.Stop();
			System.Windows.Controls.Button b = previewWinAnchor;
			if (b != null && b.IsMouseOver)
			{
				OpenGroupPreview(b, group);
			}
		};
		previewWinTimer = t;
		t.Start();
	}

	private void OpenGroupPreview(System.Windows.Controls.Button button, TaskGroup group)
	{
		CloseEntryPreview();
		List<RunningWindow> wins;
		try
		{
			if (group.Key == "folder:")
			{
				// 文件夹集合用实时扫描结果（悬停时窗口数可能与建按钮时不同）
				wins = RunningWindows.ExplorerFolders().Where((RunningWindow w) => w.Handle != IntPtr.Zero && IsWindowVisible(w.Handle)).ToList();
			}
			else
			{
				wins = group.Windows.Where((RunningWindow w) => w.Handle != IntPtr.Zero && IsWindowVisible(w.Handle)).ToList();
			}
		}
		catch (Exception ex)
		{
			Store.Trace("PREVIEW group scan failed: " + ex.Message);
			return;
		}
		Store.Trace("PREVIEW group open " + group.Name + " wins=" + wins.Count);
		if (wins.Count == 0)
		{
			return;
		}
		BuildPreviewWindow(button, group.Name, wins, null);
	}

	private void BuildPreviewWindow(System.Windows.Controls.Button button, string fallbackTitle, List<RunningWindow> wins, Entry entry)
	{
		(System.Windows.Media.Color bg, System.Windows.Media.Color ink, System.Windows.Media.Color line, System.Windows.Media.Color hover) = MenuTheme.Palette(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		previewRows = new System.Windows.Controls.StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		System.Windows.Controls.Border shell = new System.Windows.Controls.Border
		{
			Background = new SolidColorBrush(bg),
			BorderBrush = new SolidColorBrush(line),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(10.0),
			Padding = new Thickness(6.0),
			Child = previewRows
		};
		previewWin = new System.Windows.Window
		{
			WindowStyle = System.Windows.WindowStyle.None,
			AllowsTransparency = true,
			ShowInTaskbar = false,
			ShowActivated = false,
			ResizeMode = System.Windows.ResizeMode.NoResize,
			SizeToContent = System.Windows.SizeToContent.WidthAndHeight,
			Topmost = true,
			Background = System.Windows.Media.Brushes.Transparent,
			Content = shell
		};
		previewWin.MouseEnter += delegate
		{
			previewWinKeep = true;
		};
		previewWin.MouseLeave += delegate
		{
			previewWinKeep = false;
			EntryPreviewLeave();
		};
		List<KeyValuePair<System.Windows.Controls.Border, IntPtr>> thumbSlots = new List<KeyValuePair<System.Windows.Controls.Border, IntPtr>>();
		int shown = 0;
		foreach (RunningWindow w in wins)
		{
			if (shown >= 8)
			{
				break;
			}
			shown++;
			string title = string.IsNullOrWhiteSpace(w.Title) ? fallbackTitle : w.Title;
			// 槽位：DWM 缩略图由覆盖其上的顶层宿主窗绘制，这里只提供背景与尺寸
			System.Windows.Controls.Border thumbArea = new System.Windows.Controls.Border
			{
				Width = 184.0,
				Height = 104.0,
				Background = new SolidColorBrush(bg),
				CornerRadius = new CornerRadius(6.0)
			};
			System.Windows.Controls.Grid cardGrid = new System.Windows.Controls.Grid();
			cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(104.0) });
			cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			cardGrid.Children.Add(thumbArea);
			// 标题行：标题 + 右上角关闭按钮（等效 Windows 任务栏缩略图右上角 ×）
			System.Windows.Controls.Grid titleRow = new System.Windows.Controls.Grid();
			titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
			titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			TextBlock titleBlock = new TextBlock
			{
				Text = title,
				FontSize = 12.0,
				Foreground = new SolidColorBrush(ink),
				MaxWidth = 160.0,
				TextTrimming = TextTrimming.CharacterEllipsis,
				TextAlignment = TextAlignment.Left,
				Margin = new Thickness(4.0, 5.0, 2.0, 2.0)
			};
			titleRow.Children.Add(titleBlock);
			System.Windows.Controls.Button closeBtn = new System.Windows.Controls.Button
			{
				Content = "✕",
				Width = 22.0,
				Height = 22.0,
				FontSize = 11.0,
				Foreground = new SolidColorBrush(ink),
				Background = System.Windows.Media.Brushes.Transparent,
				BorderThickness = new Thickness(0.0),
				Padding = new Thickness(0.0),
				Cursor = System.Windows.Input.Cursors.Hand,
				ToolTip = "关闭窗口",
				Margin = new Thickness(0.0, 1.0, 2.0, 0.0),
				VerticalAlignment = System.Windows.VerticalAlignment.Center
			};
			System.Windows.Controls.Grid.SetColumn(closeBtn, 1);
			titleRow.Children.Add(closeBtn);
			System.Windows.Controls.Grid.SetRow(titleRow, 1);
			cardGrid.Children.Add(titleRow);
			System.Windows.Controls.Border card = new System.Windows.Controls.Border
			{
				CornerRadius = new CornerRadius(8.0),
				Margin = new Thickness(3.0),
				Background = new SolidColorBrush(hover),
				Child = cardGrid,
				Cursor = System.Windows.Input.Cursors.Hand,
				ToolTip = title
			};
			IntPtr handle = w.Handle;
			card.PreviewMouseLeftButtonUp += delegate(object s2, System.Windows.Input.MouseButtonEventArgs e)
			{
				// 点在关闭按钮上时不触发卡片激活：Preview 隧道先于按钮 Click 到达卡片，
				// 若这里关窗+激活，✕ 的 Click 就永远等不到。
				if (e.OriginalSource is System.Windows.DependencyObject src && IsVisualChild(closeBtn, src))
				{
					return;
				}
				CloseEntryPreview();
				// 与 dock 图标统一：该窗口已在前台时再点=最小化，否则提到最上层。
				if (RunningWindows.IsForeground(handle))
				{
					RunningWindows.Minimize(handle);
				}
				else if (!RunningWindows.ActivateHandle(handle))
				{
					// 窗口已销毁/句柄失效：走通用激活链兜底（重新扫描、可见过滤、launch 跟踪）
					if (entry != null)
					{
						RunningWindows.Activate(entry);
					}
				}
			};
			int slotIndex = thumbSlots.Count;
			previewRows.Children.Add(card);
			thumbSlots.Add(new KeyValuePair<System.Windows.Controls.Border, IntPtr>(thumbArea, handle));
			closeBtn.Click += delegate
			{
				Store.Trace("PREVIEW close click hwnd=" + handle.ToInt64() + " title=" + title);
				// 优雅关闭目标窗口（等效标题栏 ×，WM_CLOSE 让应用走自己的退出流程）
				System.Threading.Tasks.Task.Run(delegate
				{
					try
					{
						RunningWindows.Close(handle);
					}
					catch (Exception ex)
					{
						Store.Log(ex);
					}
				});
				try
				{
					// 移除该卡片对应的缩略图宿主与缩略图，再从面板移除卡片
					if (slotIndex < previewWinHosts.Count)
					{
						System.Windows.Interop.HwndSource host = previewWinHosts[slotIndex];
						previewWinHosts.RemoveAt(slotIndex);
						if (slotIndex < previewWinThumbs.Count)
						{
							IntPtr th = previewWinThumbs[slotIndex];
							previewWinThumbs.RemoveAt(slotIndex);
							if (th != IntPtr.Zero)
							{
								DwmUnregisterThumbnail(th);
							}
						}
						if (host != null)
						{
							host.Dispose();
						}
					}
					if (slotIndex < thumbSlots.Count)
					{
						thumbSlots.RemoveAt(slotIndex);
					}
					previewRows.Children.Remove(card);
					if (previewRows.Children.Count == 0)
					{
						CloseEntryPreview();
						return;
					}
					// 布局更新后把剩余缩略图宿主移到新位置
					previewWin.UpdateLayout();
					RelayoutPreviewThumbs(thumbSlots);
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			};
		}
		if (shown < wins.Count)
		{
			TextBlock more = new TextBlock
			{
				Text = "+" + (wins.Count - shown),
				FontSize = 13.0,
				Foreground = new SolidColorBrush(ink),
				VerticalAlignment = System.Windows.VerticalAlignment.Center,
				Margin = new Thickness(8.0, 0.0, 8.0, 0.0)
			};
			previewRows.Children.Add(more);
		}
		// 定位：图标上方居中。先 0 透明度显示，布局完成后按实际尺寸对齐、注册缩略图，再显示（避免位置闪跳）。
		previewWin.Opacity = 0.0;
		previewWin.Show();
		Store.Trace("PREVIEW entry shown size=" + previewWin.ActualWidth + "x" + previewWin.ActualHeight);
		System.Windows.Point anchorDip = ButtonAnchorDip(button);
		System.Windows.Window win = previewWin;
		previewWin.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(delegate
		{
			if (previewWin == null || win != previewWin)
			{
				return;
			}
			double w = previewWin.ActualWidth;
			double h = previewWin.ActualHeight;
			if (w <= 0.0 || h <= 0.0)
			{
				return;
			}
			previewWin.Left = anchorDip.X + button.ActualWidth / 2.0 - w / 2.0;
			previewWin.Top = anchorDip.Y - h - 8.0;
			previewWin.UpdateLayout();
			// 每个槽位建独立顶层宿主窗：DWM 把源窗口实时画面合成进宿主窗整个客户区
			try
			{
				foreach (KeyValuePair<System.Windows.Controls.Border, IntPtr> slot in thumbSlots)
				{
					System.Windows.Controls.Border area = slot.Key;
					IntPtr src = slot.Value;
					int hx;
					int hy;
					int hw;
					int hh;
					if (!LocatePreviewSlot(area, win, out hx, out hy, out hw, out hh))
					{
						continue;
					}
					System.Windows.Interop.HwndSource host = new System.Windows.Interop.HwndSource(0, unchecked((int)(0x80000000 | 0x10000000)), 0x00000008 | 0x08000000 | 0x00000080, hx, hy, hw, hh, "gbthumb", IntPtr.Zero);
					// 关键：不能设全透明——WPF 会给全透明 HwndSource 挂 WS_EX_TRANSPARENT，鼠标消息直接穿透，
					// 缩略图就点不到。画一层 alpha=1 的填充（肉眼不可见）让窗口有"内容"，DWM 缩略图仍绘制在其上。
					DrawingVisual thumbVisual = new DrawingVisual();
					using (DrawingContext dc = thumbVisual.RenderOpen())
					{
						dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0)), null, new Rect(0.0, 0.0, (double)hw, (double)hh));
					}
					host.RootVisual = thumbVisual;
					IntPtr thumb;
					int result = DwmRegisterThumbnail(host.Handle, src, out thumb);
					Store.Trace("PREVIEW attach src=" + src.ToInt64() + " reg=" + result + " rect=" + hx + "," + hy + " " + hw + "x" + hh);
					if (result == 0)
					{
						DWM_THUMBNAIL_PROPERTIES props = default(DWM_THUMBNAIL_PROPERTIES);
						props.dwFlags = 1 | 4 | 8 | 16;
						props.opacity = byte.MaxValue;
						props.fVisible = true;
						props.fSourceClientAreaOnly = true;
						props.rcDestination = new PreviewRect
						{
							Left = 0,
							Top = 0,
							Right = hw,
							Bottom = hh
						};
						DwmUpdateThumbnailProperties(thumb, ref props);
						previewWinThumbs.Add(thumb);
					}
					IntPtr handle = src;
					host.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
					{
						if (msg == 512)
						{
							// WM_MOUSEMOVE：光标在缩略图上时保持预览不关闭
							Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(delegate
							{
								previewWinKeep = true;
								if (previewWinCloseTimer != null)
								{
									previewWinCloseTimer.Stop();
								}
							}));
						}
						else if (msg == 675)
						{
							// WM_MOUSELEAVE：离开缩略图，走正常延迟关闭
							Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(delegate
							{
								previewWinKeep = false;
								EntryPreviewLeave();
							}));
						}
						else if (msg == 514)
						{
							Dispatcher.BeginInvoke(new Action(delegate
							{
								Store.Trace("PREVIEW click src=" + handle.ToInt64() + " visible=" + IsWindowVisible(handle) + " iconic=" + IsIconic(handle));
								CloseEntryPreview();
								if (RunningWindows.IsForeground(handle))
								{
									RunningWindows.Minimize(handle);
								}
								else if (!RunningWindows.ActivateHandle(handle))
								{
									if (entry != null)
									{
										RunningWindows.Activate(entry);
									}
								}
							}));
							handled = true;
						}
						return IntPtr.Zero;
					});
					previewWinHosts.Add(host);
				}
			}
			catch (Exception)
			{
			}
			if (previewWin != null)
			{
				previewWin.Opacity = 1.0;
			}
		}));
	}

	// 视觉树包含判断（点在关闭按钮上时不触发卡片激活）
	private static bool IsVisualChild(System.Windows.DependencyObject parent, System.Windows.DependencyObject child)
	{
		while (child != null)
		{
			if (child == parent)
			{
				return true;
			}
			child = System.Windows.Media.VisualTreeHelper.GetParent(child);
		}
		return false;
	}

	// 计算缩略图槽位在屏幕物理像素系的位置尺寸（宿主窗 x,y 是屏幕物理坐标）
	private static bool LocatePreviewSlot(System.Windows.Controls.Border area, System.Windows.Window win, out int hx, out int hy, out int hw, out int hh)
	{
		hx = 0;
		hy = 0;
		hw = 0;
		hh = 0;
		try
		{
			System.Windows.Point dipOffset = area.TranslatePoint(new System.Windows.Point(0.0, 0.0), win);
			System.Windows.Media.Matrix toDevice = System.Windows.Media.Matrix.Identity;
			try
			{
				PresentationSource ps = PresentationSource.FromVisual(area);
				if (ps != null && ps.CompositionTarget != null)
				{
					toDevice = ps.CompositionTarget.TransformToDevice;
				}
			}
			catch (Exception)
			{
			}
			System.Windows.Point px = toDevice.Transform(dipOffset);
			System.Windows.Point pxSize = toDevice.Transform(new System.Windows.Point(area.ActualWidth, area.ActualHeight));
			double winPxX;
			double winPxY;
			try
			{
				PresentationSource winPs = PresentationSource.FromVisual(win);
				System.Windows.Media.Matrix wt = (winPs != null && winPs.CompositionTarget != null) ? winPs.CompositionTarget.TransformToDevice : System.Windows.Media.Matrix.Identity;
				System.Windows.Point wp = wt.Transform(new System.Windows.Point(win.Left, win.Top));
				winPxX = wp.X;
				winPxY = wp.Y;
			}
			catch (Exception)
			{
				winPxX = win.Left;
				winPxY = win.Top;
			}
			hx = (int)Math.Round(winPxX + px.X);
			hy = (int)Math.Round(winPxY + px.Y);
			hw = (int)Math.Round(pxSize.X);
			hh = (int)Math.Round(pxSize.Y);
			return hw >= 2 && hh >= 2;
		}
		catch (Exception)
		{
			return false;
		}
	}

	// 关闭某张预览卡后，把剩余缩略图宿主窗移到卡片的新位置
	private void RelayoutPreviewThumbs(List<KeyValuePair<System.Windows.Controls.Border, IntPtr>> slots)
	{
		if (previewWin == null)
		{
			return;
		}
		System.Windows.Window win = previewWin;
		for (int i = 0; i < previewWinHosts.Count && i < slots.Count; i++)
		{
			if (previewWinHosts[i] == null)
			{
				continue;
			}
			int hx;
			int hy;
			int hw;
			int hh;
			if (LocatePreviewSlot(slots[i].Key, win, out hx, out hy, out hw, out hh))
			{
				SetWindowPos(previewWinHosts[i].Handle, IntPtr.Zero, hx, hy, hw, hh, 0x0014);
			}
		}
	}

	private void CloseEntryPreview()
	{
		if (previewWinTimer != null)
		{
			previewWinTimer.Stop();
		}
		if (previewWinCloseTimer != null)
		{
			previewWinCloseTimer.Stop();
		}
		previewWinKeep = false;
		System.Collections.Generic.List<IntPtr> thumbs = new System.Collections.Generic.List<IntPtr>(previewWinThumbs);
		previewWinThumbs.Clear();
		if (thumbs.Count > 0)
		{
			Task.Run(delegate
			{
				try
				{
					foreach (IntPtr thumb in thumbs)
					{
						DwmUnregisterThumbnail(thumb);
					}
				}
				catch
				{
				}
			});
		}
		foreach (System.Windows.Interop.HwndSource host in previewWinHosts)
		{
			try
			{
				host.Dispose();
			}
			catch
			{
			}
		}
		previewWinHosts.Clear();
		if (previewWin != null)
		{
			try
			{
				previewWin.Close();
			}
			catch (Exception)
			{
			}
			previewWin = null;
			previewRows = null;
		}
		previewWinAnchor = null;
	}

	/// <summary>按钮左上角的屏幕坐标换算成 DIP（Window.Left/Top 坐标系）。</summary>
	private System.Windows.Point ButtonAnchorDip(FrameworkElement el)
	{
		try
		{
			System.Windows.Point device = el.PointToScreen(new System.Windows.Point(0.0, 0.0));
			System.Windows.Media.Matrix m = PresentationSource.FromVisual(el).CompositionTarget.TransformFromDevice;
			return m.Transform(device);
		}
		catch (Exception)
		{
			return new System.Windows.Point(Left, Top);
		}
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
		if (windows == null || windows.Count == 0)
		{
			Store.Trace("PREVIEW skip count=0");
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
		// 缩略图上限 4 张：Electron/多进程应用窗口很多，每个 DWM 缩略图注册都是一次同步 IPC，
		// 全量注册会让 UI 线程阻塞上百 ms（跨图标切换预览时卡顿的主因）。
		int previewCap = 0;
		foreach (RunningWindow window in windows)
		{
			if (previewCap >= 4)
			{
				break;
			}
			previewCap++;
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
			// Loaded 优先级在布局完成之后执行：ContextIdle 太低，Popup 首次布局尚未安排时
			// 重试回调会在一帧内空转到底，永远等不到 slot 拿到尺寸。
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(SetupPreviewThumbs));
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
			if (++tries > 60)
			{
				Store.Trace("PREVIEW thumbs setup gave up");
				return;
			}
			if (tries == 1 || tries == 10 || tries == 20 || tries == 60)
			{
				string extra = (previewSlots != null && previewSlots.Count > 0) ? (" slot0=" + (int)previewSlots[0].ActualWidth + "x" + (int)previewSlots[0].ActualHeight) : " slots=null";
				Store.Trace("PREVIEW poll tries=" + tries + extra + " child=" + ((card.RenderSize.Width > 1.0) ? ((int)card.RenderSize.Width + "x" + (int)card.RenderSize.Height) : "0x0"));
			}
			// 真正让出 100ms 再重试：Popup 布局在 Dispatcher 空转队列里永远没有机会执行
			System.Threading.Tasks.Task.Run(async delegate
			{
				await System.Threading.Tasks.Task.Delay(100);
				Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, trySetup);
			});
		};
		Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, trySetup);
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
		// 布局完成信号：Loaded 在元素完成首次布局并接入渲染树后触发，此时 ActualWidth 必然有效
		img.Loaded += (object s, System.Windows.RoutedEventArgs e) =>
		{
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(SetupPreviewThumbs));
		};
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
			// 注销挪到后台线程：每个 DwmUnregisterThumbnail 都是同步 IPC，全量注销会阻塞 UI 线程几十 ms
			System.Collections.Generic.List<IntPtr> oldThumbs = new System.Collections.Generic.List<IntPtr>(previewThumbs);
			previewThumbs.Clear();
			previewThumbs = null;
			if (oldThumbs.Count > 0)
			{
				Task.Run(delegate
				{
					try
					{
						foreach (IntPtr thumb in oldThumbs)
						{
							DwmUnregisterThumbnail(thumb);
						}
					}
					catch
					{
					}
				});
			}
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
				// 布局未完成：真正让出 100ms 再重试（Dispatcher 队列空转永远等不到 Popup 布局）
				System.Threading.Tasks.Task.Run(async delegate
				{
					await System.Threading.Tasks.Task.Delay(100);
					Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(SetupPreviewThumbs));
				});
				return;
			}
			previewSetupTries = 0;
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
				int width = (int)Math.Round(bottomRight.X - slotScreen.X);
				int height = (int)Math.Round(bottomRight.Y - slotScreen.Y);
				if (width < 2 || height < 2)
				{
					Store.Trace("PREVIEW thumb skip size=" + width + "x" + height + " slotW=" + slot.ActualWidth + " slotH=" + slot.ActualHeight);
					continue;
				}
				// 缩略图目标必须是顶层窗口（WS_CHILD 会让 DwmRegisterThumbnail 返回 E_INVALIDARG）：
				// 每个卡片建一个独立无边框顶层小窗盖在卡片槽位上，DWM 把源窗口画面合成进它的整个客户区。
				// x/y 为屏幕物理像素；TOPMOST+NOACTIVATE 保证压在 Popup 上方且点击不抢焦点；TOOLWINDOW 不进 Alt+Tab。
				int hostX = (int)Math.Round(slotScreen.X);
				int hostY = (int)Math.Round(slotScreen.Y);
				System.Windows.Interop.HwndSource host = new System.Windows.Interop.HwndSource(0, unchecked((int)(0x80000000 | 0x10000000)), 0x00000008 | 0x08000000 | 0x00000080, hostX, hostY, width, height, "gbthumb", IntPtr.Zero);
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
					// rcDestination 相对目标窗口（host）客户区：铺满整窗
					props.rcDestination = new PreviewRect
					{
						Left = 0,
						Top = 0,
						Right = width,
						Bottom = height
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

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(IntPtr hWnd);

	[DllImport("dwmapi.dll")]
	private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

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
		bool known = LayoutKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
		if (!known && !DockLayoutRules.SystemItem(key))
		{
			// 运行任务快照的进程路径可能与固定条目路径不一致（Electron 多进程应用），
			// 固定应用本身应始终可交互，否则这类图标会失去按下反馈/拖拽。
			known = DockOrderRules.Ordered(C.Prefs).Any((Entry a) => string.Equals(a.Path, key, StringComparison.OrdinalIgnoreCase));
		}
		if (!IsVisible || !known)
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
		dockPointerPreviousBusy = (C.Board != null && C.Board.ModalBusy);
		if (C.Board != null) { C.Board.ModalBusy = true; }
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
		if (C.Board != null) { C.Board.ModalBusy = dockPointerPreviousBusy; }
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
