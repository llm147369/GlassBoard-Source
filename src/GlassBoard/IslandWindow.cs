using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GlassBoard;

// 灵动岛主窗口。继承 GlassWindow 复用毛玻璃底图（壁纸直读模糊）、材质与阴影管线。
// 三态：Collapsed（收起胶囊）/ Toast（短暂事件卡）/ Expanded（向下展开面板）。
// 关键行为：
//  - 收起/事件态用 WS_EX_NOACTIVATE + ShowActivated=false，被动提示绝不抢焦点；
//  - 展开态移除 NOACTIVATE 并激活，支持键盘导航；Esc 或点击外部收起；
//  - 进度变化只更新 UI，不改变模式；展开时事件不强制关闭面板（进列表）；
//  - 多显示器/DPI：工作区经 TransformFromDevice 换算；全屏应用按设置隐藏；
//  - 收起态内容全部按实际数据动态创建，无数据项不占任何布局空间。
public sealed class IslandWindow : GlassWindow
{
	public enum Mode { Hidden, Collapsed, Toast, Expanded }

	private const long WS_EX_NOACTIVATE = 0x08000000L;
	private const long WS_EX_TOOLWINDOW = 0x00000080L;
	private const long WS_EX_TOPMOST = 0x00000008L;
	private const int GWL_EXSTYLE = -20;
	private const int WM_MOUSEACTIVATE = 0x0021;

	private readonly Controller C;
	private IslandOptions O => C.Prefs.Island;
	private MusicEngine M => C.Music;

	private Mode mode = Mode.Hidden;
	private Mode modeBeforeHide = Mode.Collapsed;
	private bool draggingProgress;
	private bool suppressDeactivate;
	private bool wifiScanning;
	private bool btScanning;
// 文字/图标颜色按“实际渲染背景”自适应：true=背景偏亮用深色字，false=背景偏暗用浅色字�?
private bool textLight = true;

// 收起态控件
private Grid collapsedRoot;
	private DockPanel collapsedStack;
	private Border coverBox;
	private TextBlock collapsedTitle;
	private Border bellBadge;
	private Ellipse bellDot;
	private FrameworkElement equalizerBars;
	// 跳动条自愈看护：条被"点开→关闭"竞态弄丢或采集死亡时防抖重建；
	// sizeAnimStart 记录主过渡动画起始时刻，防动画令牌作废后 sizeAnimating 卡死（跳动条永久冻结）。
	private long lastEqHealTick;
	private long sizeAnimStart;
	private Border[] equalizerBarArr = new Border[7];
	private Ellipse[] eqCaps = new Ellipse[7];
	private double[] eqCap = new double[7];
	private double[] eqSpring = new double[7];
	private double[] eqVel = new double[7];
	private Path eqPath;
	private TextBlock collapsedSub;
	private DateTime lyricStartUtc = DateTime.MinValue;
	private string lastRealTrackKey = "";
	private bool lyricUntrusted;
	private TextBlock collapsedTime;
	private TextBlock collapsedDate;
	private TextBlock weatherText;
	private TextBlock refreshText;
	private DateTime lastClockTick = DateTime.MinValue;
	private DateTime lastSuggestionAt = DateTime.MinValue;
	private DateTime lastSuggestionPoolAt = DateTime.MinValue;
	private int suggestionIdx;
	private string[] suggestionPool = new string[0];
	private bool suggestTraced;

// —�?事件�?—�?
private Border toastCard;
	private Border toastIcon;
	private TextBlock toastIconText;
	private Border toastViewBtn;
	private string notifFilter;
	private string toastFilterKey;
	private WrapPanel notifCats;
	private TextBlock toastApp;
	private TextBlock toastTitle;
	private TextBlock toastBody;

// —�?展开�?—�?
private Grid expandedRoot;
	private StackPanel tabBar;
	private Grid pageHost;
	private Border closeBtn;
	private TextBlock pageHeading;
	private Border backBtn;
	private Border headerDivider;
	private Dictionary<int, Func<Brush, FrameworkElement>> tabIcons = new Dictionary<int, Func<Brush, FrameworkElement>>();

	private Grid musicPage, notificationsPage, clipboardPage, controlPage, wifiPage, btPage, notesPage;
	private Border clipToast;
	private int clipToastToken;
	private Border bigCover;
	private TextBlock musicTitle, musicArtist, musicStatus;
	private Slider progressSlider;
	private TextBlock positionText, durationText;
	private Border prevBtn, playBtn, nextBtn;
	private Grid musicProgressRow;
	private StackPanel musicButtonsRow;
	private StackPanel notifList, clipList;
	private Border wifiTile, btTile;
	private TextBlock wifiStatus, btStatus, wifiTileName, btTileName;
	private Border pcBatCard, mouseBatCard, kbBatCard;
	private TextBlock pcBatText, mouseBatText, kbBatText;
	private TextBlock outputLabel, controlNote;
	// 随手记：列表页 + 独立编辑页 + 查看页（与桌面小组件共用同一份 notes.json，两边编辑实时互通）
	private Grid noteEditPage, noteViewPage;
	private StackPanel noteList;
	private TextBox noteTitle, noteBody;
	private TextBlock noteStatus, editStatus, remindLabel;
	private Border remindPanel, saveBtn, editDelBtn;
	private TextBox remindCustom;
	private bool editingNotes;
	private NoteItem currentNote;
	private bool noteNewMode;
	private long pendingRemind = 0; // 新建模式下的提醒暂存（保存时写入新笔记）
	private string noteHeading = "随手记";
	private TextBlock noteViewTitle, noteViewBody;
	private TextBlock noteViewStatus;
	private int subBack = 3;
	private Slider volumeSlider, brightnessSlider;
	private TextBlock volumeValue, brightnessValue;
	private Border volumeIconHost, brightnessIconHost, outputIconHost;
	private ComboBox outputCombo;
	private StackPanel networkList, btDeviceList;
	private PasswordBox passwordBox;
	private Border connectBtn;

	private readonly DispatcherTimer toastTimer;
	private readonly DispatcherTimer progressTimer;
	private readonly DispatcherTimer fullscreenTimer;
	private DispatcherTimer wallpaperRedo;

	private DateTime lastMusicTick;
	private string lastTrackKey = "";
	private bool lastPlaying;
	private bool expandEntrance;
	private string lastSessionKey = "";
	private bool lowBatteryArmed = true;
	private int currentPage = -1;
	private bool volumeSyncing, brightnessSyncing;
	private bool _isClosed;
	// 显示桌面守护临时置顶标记：desktopRaised 为真时强制置顶（Win+D 免疫），
	// 平时跟随 O.AlwaysOnTop（默认不保持置顶，与其他窗口正常叠放）。
	private bool desktopRaised;
// 展开态高度随当前页面内容自适应；sizeAnimating 防止动画进行中被“无变化”判定提前终�?
private double expandedHeight = 420.0;
	private double expandedWidth = 380.0;
	private bool sizeAnimating;
	private double animTargetW = double.NaN;
	private double animTargetH = double.NaN;
	private double animTargetL = double.NaN;
	private int syncToken;
// 丝滑过渡的代数令牌：新过渡开始即自增，旧过渡的完成回调凭令牌失效（防交错收尾）�?
private int transitionToken;
	private bool backdropRetryArmed;
	private int backdropRetryAttempts;
	private DispatcherTimer backdropRetryTimer;
	// 控制页可见期间的轻量同步定时器：只在展开且停留在控制页时运行，收起/切页/隐藏立即停止（不影响使用速度）。
	private DispatcherTimer controlSyncTimer;
	private bool volumeMutedShown;
	// 展开态底图脏标记：收起期间若基类管线重捕（钩子/壁纸广播），展开区底图即被替换/过期，
	// 下次展开前必须重捕展开区域（含当时遮挡窗口的残留画面），否则残留随展开放大成"脏层"。
	private bool expandedBackdropStale = true;
	public bool Expanded => mode == Mode.Expanded;
// 自愈看护用：窗口已被关闭（无法再 Show�?
public bool IsClosedWindow => _isClosed;
	// 全屏策略主动隐藏（看护不得强行恢复）
	public bool HiddenByPolicy => mode == Mode.Hidden;

	public IslandWindow(Controller c) : base(20.0)
	{
		C = c;
		ShowActivated = false;
		// 计时器必须先�?BuildUi 创建：BuildUi 末尾�?ApplyStyle→CheckFullscreen 可能
// 触发 RestoreFromFullscreen→ShowCollapsed，�?ShowCollapsed �?Stop 这些计时器�?

toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
		toastTimer.Tick += (object s, EventArgs e) => { toastTimer.Stop(); CollapseFromToast(); };
		progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
		progressTimer.Tick += (object s, EventArgs e) => UpdateProgressTick();
		fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
		fullscreenTimer.Tick += (object s, EventArgs e) =>
		{
			CheckFullscreen();
			CheckForegroundCleanup();
		};
		BuildUi();
		SourceInitialized += OnSourceInitialized;
		SizeChanged += (object s, SizeChangedEventArgs e) => RefreshClip();
// 音频采集回调：真实节奏驱动跳动条（构造期订阅，Closed 里退订）�?

IslandAudio.AudioLevels += OnAudioLevels;
		// 展开态点击外部收起改为鼠标钩子驱动（不再依赖失焦）：Win+D 无鼠标点击，永不误收。
		// suppressDeactivate（GlassBoard 自己的设置窗口打开时置位）继续保护不收起。
		PanelDismissGuard.Track(this, () =>
		{
			if (mode == Mode.Expanded && !suppressDeactivate && !IsActive)
			{
				Store.Trace("ISLAND outside click");
				Collapse();
			}
		});
		// 显示桌面守护：AlwaysOnTop 关闭时，Win+D 会让 explorer 把桌面窗口抬到普通层顶部
		// 盖住小岛。注册后 DesktopCoverGuard 在桌面抬升期间临时置顶、恢复后复位。
		DesktopCoverGuard.Track(this, (bool raised) =>
		{
			desktopRaised = raised;
			ApplyTopmost();
		});
		PreviewKeyDown += (object s, KeyEventArgs e) =>
		{
			if (e.Key == Key.Escape && mode == Mode.Expanded)
			{
				Collapse();
				e.Handled = true;
			}
		};
		Closed += (object s, EventArgs e) =>
		{
			toastTimer.Stop();
			progressTimer.Stop();
			fullscreenTimer.Stop();
			IslandAudio.AudioLevels -= OnAudioLevels;
			StopEqualizer();
			_isClosed = true;
			Store.Trace("ISLAND window closed");
		};
	}

	// ———————————————————�?构建 UI ———————————————————�?
	private void BuildUi()
	{
// Transparent 背景参与命中测试（null 不参与）：否则点击收起条/展开面板的空白区�?

// 会直接穿透到小岛下面的应�?�?小岛失焦 �?Deactivated 误判为“点击外部”而自动收起�?

collapsedRoot = new Grid { Background = Brushes.Transparent };
		collapsedStack = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
		collapsedRoot.Children.Add(collapsedStack);
		collapsedRoot.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) => Expand();
		collapsedRoot.Cursor = Cursors.Hand;

		// 展开面板（默认隐藏：收起态下必须不可见，否则会和收起态内容重叠）
		expandedRoot = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
		expandedRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
		expandedRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		headerDivider = new Border { BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = LineBrush() };
		Grid.SetRow(headerDivider, 0);
		Grid headerGrid = new Grid();
		headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		tabBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 6, 0) };
		headerGrid.Children.Add(tabBar);
		closeBtn = IconButton(IslandIcons.Close(IconBrush(), 12), delegate { Collapse(); });
		closeBtn.Width = 26;
		closeBtn.Height = 26;
		closeBtn.CornerRadius = new CornerRadius(13);
		closeBtn.Margin = new Thickness(0, 0, 12, 0);
		Grid.SetColumn(closeBtn, 1);
		headerGrid.Children.Add(closeBtn);
		headerDivider.Child = headerGrid;
		expandedRoot.Children.Add(headerDivider);

		pageHost = new Grid { Margin = new Thickness(14, 6, 14, 14) };
		Grid.SetRow(pageHost, 1);
		expandedRoot.Children.Add(pageHost);

		musicPage = BuildMusicPage();
		notificationsPage = BuildNotificationsPage();
		clipboardPage = BuildClipboardPage();
		controlPage = BuildControlPage();
		wifiPage = BuildWifiPage();
		btPage = BuildBtPage();
		notesPage = BuildNotesPage();
		noteEditPage = BuildNoteEditPage();
		noteViewPage = BuildNoteViewPage();
		foreach (Grid page in new[] { musicPage, notificationsPage, clipboardPage, controlPage, wifiPage, btPage, notesPage, noteEditPage, noteViewPage })
		{
			page.Visibility = Visibility.Collapsed;
			pageHost.Children.Add(page);
		}
		pageHeading = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(56, 10, 24, 6), VerticalAlignment = VerticalAlignment.Top };
		pageHeading.Visibility = Visibility.Collapsed;
		pageHost.Children.Add(pageHeading);
		backBtn = IconButton(IslandIcons.Chevron(IconBrush(), 12), delegate { SetPage(subBack); });
		backBtn.Width = 26;
		backBtn.Height = 26;
		backBtn.CornerRadius = new CornerRadius(13);
		backBtn.Margin = new Thickness(14, 6, 0, 0);
		backBtn.HorizontalAlignment = HorizontalAlignment.Left;
		backBtn.VerticalAlignment = VerticalAlignment.Top;
		backBtn.Visibility = Visibility.Collapsed;
		pageHost.Children.Add(backBtn);

// 事件卡：图标 | 居中标题/内容 | 右侧“查看�?

toastCard = new Border();
		Grid toastGrid = new Grid();
		toastGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		toastGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		toastGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		toastIcon = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 10, 0) };
		toastIconText = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		toastIcon.Child = toastIconText;
		toastGrid.Children.Add(toastIcon);
		StackPanel toastTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
		toastApp = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
		toastTitle = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
		toastBody = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, MaxHeight = 20, Margin = new Thickness(0, 1, 0, 0) };
		toastTextStack.Children.Add(toastApp);
		toastTextStack.Children.Add(toastTitle);
		toastTextStack.Children.Add(toastBody);
		Grid.SetColumn(toastTextStack, 1);
		toastGrid.Children.Add(toastTextStack);
		toastViewBtn = TextButton("查看", delegate
		{
			toastTimer.Stop();
			notifFilter = toastFilterKey;
			Expand(1);
		});
		toastViewBtn.VerticalAlignment = VerticalAlignment.Center;
		toastViewBtn.Margin = new Thickness(0, 0, 14, 0);
		Grid.SetColumn(toastViewBtn, 2);
		toastGrid.Children.Add(toastViewBtn);
		toastCard.Child = toastGrid;
		toastCard.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			toastTimer.Stop();
			notifFilter = toastFilterKey;
Expand(1); // 点击事件卡→跳到该应用的筛选通知�?

};
		toastCard.Cursor = Cursors.Hand;
		toastCard.Visibility = Visibility.Collapsed;

		ContentGrid.Children.Add(collapsedRoot);
		ContentGrid.Children.Add(toastCard);
		ContentGrid.Children.Add(expandedRoot);
		ApplyStyle(O);
	}

	// —�?收起态内�?—�?
	private void RebuildCollapsed()
	{
		collapsedStack.Children.Clear();
		collapsedTime = null;
		collapsedDate = null;
		weatherText = null;
		refreshText = null;
		bellBadge = null;
		bellDot = null;
		bool light = LightTheme;
		Brush text = TextBrush;
		Brush muted = MutedBrush;
		double iconSize = O.IconSize;
		double gap = O.ItemGap;

// 音乐（封面靠左，标题最后添加→填充中间全部剩余宽度，不再居中留大空隙）�?

// 收起态内容开关已从设置移除：正在播放音乐就直接显示音乐内容（封面/跳动�?曲名），
// 通知不受影响（事件卡照常弹出、通知页照常记录）；不播放时显示时�?刷新�?天气布局�?

bool playing = M != null && M.Playing;
		if (playing)
		{
			// 封面随收起高度联动缩放（内容区高 = CollapsedHeight - 28 根边�?- 8 上下边距）；
// 吸附顶部时窗口已补偿 -14px，封面基数同样扣�?14，保证与浮动模式完全一致�?


// 正方形封面永远完整显示、不会被裁成长方形；圆角跟随“形状与布局”的圆角设置�?


double coverBase = SnapTop() ? O.CollapsedHeight - 14.0 : O.CollapsedHeight;
			double coverSize = Math.Min(96.0, Math.Max(28.0, coverBase - 36.0));
			double coverRadius = Math.Min(O.CornerRadius, coverSize / 2.0);
			coverBox = new Border
			{
				Width = coverSize,
				Height = coverSize,
				CornerRadius = new CornerRadius(coverRadius),
				Margin = new Thickness(0, 0, 2, 0),
				VerticalAlignment = VerticalAlignment.Center,
				ClipToBounds = true
			};
			if (M.Artwork != null)
			{
				Image img = new Image { Source = M.Artwork, Stretch = Stretch.UniformToFill, Width = coverSize, Height = coverSize };
				img.Clip = new RectangleGeometry(new Rect(0, 0, coverSize, coverSize), coverRadius, coverRadius);
				coverBox.Child = img;
			}
			else
			{
				coverBox.Child = IslandIcons.MusicPlaceholder(text, ChipBrush(), coverSize, coverRadius);
			}
			DockPanel.SetDock(coverBox, Dock.Left);
			collapsedStack.Children.Add(coverBox);
		}

// 播放中：收起态进入“播放模式”——只显示 封面 + 跳动�?+ 标题�?

// 电量、通知铃铛、展开把手全部隐藏（用户要求）；不播放时恢复完整收起布局�?

if (!playing)
		{
			StopEqualizer();

			// 无音乐可显示时的默认布局：左�?当前时间/日期，右�?天气（最右，先于电量添加），
// 中部=实时屏幕刷新率（计时器驱动刷新）。全部不新增设置项�?


StackPanel timeStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
			collapsedTime = new TextBlock { Text = DateTime.Now.ToString("HH:mm"), FontSize = O.FontSize + 1.0, FontWeight = FontWeights.SemiBold, Foreground = text };
			collapsedDate = new TextBlock { Text = DateTime.Now.ToString("M月d日 ddd"), FontSize = Math.Max(10.0, O.FontSize - 3.0), Foreground = muted, Margin = new Thickness(0, 1, 0, 0) };
			timeStack.Children.Add(collapsedTime);
			timeStack.Children.Add(collapsedDate);
			DockPanel.SetDock(timeStack, Dock.Left);
			collapsedStack.Children.Add(timeStack);

			WeatherSnapshot ws = null;
			try
			{
				ws = C.Weather?.Snapshot;
			}
			catch
			{
			}
			if (ws != null && ws.Current != null)
			{
				string desc = WeatherPresentation.Description(ws.Current.Code, ws.Current.Description);
				string wtext = ws.Current.Temperature.ToString("0") + "° " + desc;
				weatherText = new TextBlock { Text = wtext, FontSize = Math.Max(10.0, O.FontSize - 1.5), Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(gap, 0, 0, 0), MaxWidth = 132, TextTrimming = TextTrimming.CharacterEllipsis };
				DockPanel.SetDock(weatherText, Dock.Right);
				collapsedStack.Children.Add(weatherText);
			}
// 收起态已按用户要求精简：电量、通知铃铛、展开把手全部移除�?


// 点击小岛本体即可展开；电量低仍会弹事件卡；通知仍会弹事件卡并进通知页�?

}
		else
		{
			// 跳动条（音乐均衡器）：WASAPI 回环采集 + FFT 五档能量驱动五根圆角柱，
// 真实跟随系统正在播放的音乐节奏，放在收起条最右侧；整体不参与命中测试�?


// 点击照常冒泡到收起根节点打开展开面板。只有收起态可见时才开采集�?


equalizerBars = BuildEqualizer();
			DockPanel.SetDock(equalizerBars, Dock.Right);
			collapsedStack.Children.Add(equalizerBars);
			if (mode == Mode.Collapsed)
			{
				StartEqualizer();
			}
		}

		// 中部：曲�?+ 实时歌词（无歌词时显示歌手），最后添加作�?DockPanel 填充项，
// 占满封面与跳动条之间的全部剩余宽度，消除中间大空档�?

bool haveTitle = playing;
		if (haveTitle)
		{
			string sub = IslandLyrics.ActiveText;
			if (sub.Length == 0)
			{
				sub = string.IsNullOrEmpty(M.Artist) ? "" : M.Artist;
			}
			StackPanel mid = new StackPanel
			{
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(8, 0, 6, 0)
			};
			collapsedTitle = new TextBlock
			{
				Text = M.Title ?? "",
				FontSize = O.FontSize,
				FontWeight = FontWeights.Medium,
				Foreground = text,
				TextAlignment = O.MusicAlignCenter ? TextAlignment.Center : TextAlignment.Left,
				TextTrimming = TextTrimming.CharacterEllipsis
			};
			mid.Children.Add(collapsedTitle);
			collapsedSub = new TextBlock
			{
				Text = sub,
				FontSize = Math.Max(10.0, O.FontSize - 2.5),
				Foreground = muted,
				TextAlignment = O.MusicAlignCenter ? TextAlignment.Center : TextAlignment.Left,
				TextTrimming = TextTrimming.CharacterEllipsis,
				Margin = new Thickness(0, 1, 0, 0)
			};
			mid.Children.Add(collapsedSub);
			collapsedStack.Children.Add(mid);
		}
		else
		{
			// 中部填充：动态建议轮播（时间问候 + 天气建议 + 屏幕刷新率 + 今日随手记），
			// 由 500ms 计时器每 8 秒淡入淡出切换；无音乐时始终有内容，不占空白。
			suggestionPool = IslandSuggestions.Build(SafeWeather(), CurrentRefreshRate(), SafeNotes(), SafeUnread());
			lastSuggestionPoolAt = DateTime.Now;
			lastSuggestionAt = DateTime.Now;
			suggestionIdx = 0;
			string firstSuggestion = (suggestionPool.Length == 0) ? "" : suggestionPool[0];
			refreshText = new TextBlock { Text = firstSuggestion, FontSize = Math.Max(10.0, O.FontSize - 1.5), Foreground = muted, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
			collapsedStack.Children.Add(refreshText);
		}
		collapsedStack.LastChildFill = haveTitle || refreshText != null;

// 全部内容开关关闭或都没有有效数据：显示最小占位图标，避免出现空白胶囊�?

if (collapsedStack.Children.Count == 0)
		{
			collapsedStack.Children.Add(IslandIcons.Gear(muted, iconSize));
			collapsedStack.LastChildFill = false;
		}
	}

	private bool AnyPageEnabled() => O.ShowMusicPage || O.ShowNotificationsPage || O.ShowControlPage;

	private WeatherSnapshot SafeWeather()
	{
		try
		{
			return C.Weather?.Snapshot;
		}
		catch
		{
			return null;
		}
	}

	private NoteBook SafeNotes()
	{
		try
		{
			return C.Notes?.Book;
		}
		catch
		{
			return null;
		}
	}

	private int SafeUnread()
	{
		try
		{
			return IslandNotifications.Recent().Count;
		}
		catch
		{
			return 0;
		}
	}

// —— 音乐跳动条（均衡器动画）——
// 数据来自 IslandAudio �?WASAPI 回环采集 + FFT 24 频段（真实跟随音乐节奏，
// 不是合成正弦动画）；播放停止/离开收起态时停采集，不产生后台开销�?
// 内置 5 套外观（O.EqualizerStyle），形态与运动方式各不相同�?
//   0=霓虹辉光�? 柱强调色渐变+强辉光）
//   1=彩虹�? 柱各一�?同色辉光�?
//   2=极光描边�? 柱描�?独立“峰值帽”二次运动）
	//   3=玻璃弹簧�? 柱物理弹簧，带弹性过冲）
	//   4=频谱曲线�?4 频段平滑贝塞尔曲�?底部渐变填充，随频谱起伏�?
	private static Color RainbowColor(int i)
	{
		Color[] palette = {
			Color.FromRgb(255, 107, 107),
			Color.FromRgb(255, 201, 87),
			Color.FromRgb(107, 203, 119),
			Color.FromRgb(77, 150, 255),
			Color.FromRgb(185, 131, 255),
			Color.FromRgb(255, 128, 200),
			Color.FromRgb(90, 220, 200)
		};
		return palette[i % palette.Length];
	}

	private static Color LightenColor(Color c, float f)
	{
		return Color.FromRgb(
			(byte)Math.Min(255, c.R + (255 - c.R) * f),
			(byte)Math.Min(255, c.G + (255 - c.G) * f),
			(byte)Math.Min(255, c.B + (255 - c.B) * f));
	}

	private FrameworkElement BuildEqualizer()
	{
		Color accent = IconPacks.AccentColor(O.Accent);
		int style = Math.Max(0, Math.Min(4, O.EqualizerStyle));

// —�?样式 4：频谱曲�?—�?

if (style == 4)
		{
			Canvas canvas = new Canvas
			{
				Width = 96,
				Height = 18,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(10, 0, 0, 0),
				IsHitTestVisible = false,
				ClipToBounds = true
			};
			PathGeometry geometry = new PathGeometry();
			// 初始水平基线：采集尚未就绪或第一帧数据到达前也有一条可见的线，
			// 避免样式 4（频谱曲线）在启动/重启瞬间“凭空消失”。
			PathFigure flatLine = new PathFigure { StartPoint = new Point(1.0, 16.5), IsClosed = false };
			flatLine.Segments.Add(new LineSegment(new Point(95.0, 16.5), true));
			geometry.Figures.Add(flatLine);
			eqPath = new Path
			{
				Data = geometry,
				Stroke = new LinearGradientBrush(accent, LightenColor(accent, 0.55f), 0),
				StrokeThickness = 2,
				StrokeStartLineCap = PenLineCap.Round,
				StrokeEndLineCap = PenLineCap.Round,
				StrokeLineJoin = PenLineJoin.Round,
				Effect = new DropShadowEffect { Color = accent, BlurRadius = 7, ShadowDepth = 0, Opacity = 0.6 },
				IsHitTestVisible = false
			};
			canvas.Children.Add(eqPath);
			equalizerBarArr = null;
			return canvas;
		}

// —�?样式 0-3：柱�?—�?

int count = style == 1 ? 7 : 5;
		equalizerBarArr = new Border[count];
		eqCaps = new Ellipse[count];
		StackPanel bars = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			IsHitTestVisible = false
		};
		for (int i = 0; i < count; i++)
		{
			Brush fill;
			Brush borderBrush = null;
			Effect glow;
			switch (style)
			{
				case 1: // 彩虹：七柱各一�?+ 同色辉光
				{
					Color c = RainbowColor(i);
					fill = new LinearGradientBrush(c, LightenColor(c, 0.45f), 90);
					glow = new DropShadowEffect { Color = c, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6 };
					break;
				}
				case 2: // 极光描边：半透明填充 + 实色描边（中间提亮）
				{
					Color c = (i == 2) ? LightenColor(accent, 0.5f) : accent;
					fill = new SolidColorBrush(Color.FromArgb(30, c.R, c.G, c.B));
					borderBrush = new SolidColorBrush(Color.FromArgb((byte)(i == 2 ? 255 : 205), c.R, c.G, c.B));
					glow = new DropShadowEffect { Color = c, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 };
					break;
				}
case 3: // 玻璃简约：随明暗的灰白�?+ 通透渐变，无辉�?



{
					Color c0 = textLight ? Color.FromArgb(200, 25, 28, 34) : Color.FromArgb(215, 255, 255, 255);
					Color c1 = textLight ? Color.FromArgb(150, 92, 96, 105) : Color.FromArgb(140, 208, 212, 220);
					fill = new LinearGradientBrush(c0, c1, 90);
					glow = null;
					break;
				}
default: // 0 霓虹辉光：强调色纵向渐变（由深到浅）+ 强辉�?



{
					Color c = LightenColor(accent, i * 0.14f);
					fill = new LinearGradientBrush(accent, c, 90);
					glow = new DropShadowEffect { Color = accent, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.65 };
					break;
				}
			}
			Border bar = new Border
			{
				Width = 4,
				Height = 18,
				CornerRadius = new CornerRadius(2),
				Background = fill,
				BorderThickness = new Thickness(style == 2 ? 1.5 : 0),
				BorderBrush = borderBrush,
				Margin = new Thickness(2, 0, 2, 0),
				VerticalAlignment = VerticalAlignment.Bottom,
				RenderTransformOrigin = new Point(0.5, 1),
				RenderTransform = new ScaleTransform(1.0, 0.35),
				Effect = glow
			};
			bars.Children.Add(bar);
			equalizerBarArr[i] = bar;
		}

		// 样式 2：叠加峰值帽层（每根柱顶一�?4px 圆点，随柱瞬间顶起、缓慢回落）
		if (style == 2)
		{
			Canvas caps = new Canvas
			{
				Width = count * 8,
				Height = 22,
				VerticalAlignment = VerticalAlignment.Bottom,
				IsHitTestVisible = false
			};
			for (int i = 0; i < count; i++)
			{
				Ellipse dot = new Ellipse
				{
					Width = 4,
					Height = 4,
					Fill = new SolidColorBrush(i == 2 ? LightenColor(accent, 0.5f) : accent),
					Effect = new DropShadowEffect { Color = accent, BlurRadius = 5, ShadowDepth = 0, Opacity = 0.7 }
				};
				Canvas.SetLeft(dot, i * 8 + 2);
				Canvas.SetBottom(dot, 2);
				caps.Children.Add(dot);
				eqCaps[i] = dot;
			}
			Grid root = new Grid
			{
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(10, 0, 0, 0),
				IsHitTestVisible = false
			};
			root.Children.Add(bars);
			root.Children.Add(caps);
			root.Width = count * 8;
			return root;
		}

		bars.Margin = new Thickness(10, 0, 0, 0);
		return bars;
	}

	private void StartEqualizer()
	{
		try
		{
			IslandAudio.Start();
		}
		catch
		{
		}
	}

	private void StopEqualizer()
	{
		try
		{
			IslandAudio.Stop();
		}
		catch
		{
		}
	}

// 采集线程回调（IslandAudio.AudioLevels�?4 频段）→ UI 线程按当前样式驱动动画�?
// 各样式运动不同：柱状平滑伸缩 / 峰值帽二次回落 / 弹簧物理过冲 / 频谱曲线起伏�?
private void OnAudioLevels(double[] levels)
	{
		if (levels == null || levels.Length < IslandAudio.BandCount)
		{
			return;
		}
		try
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				if (mode != Mode.Collapsed || M == null || !M.Playing)
				{
					return;
				}
				// —— 跳动条看护：点开→关闭的竞态可能把跳动条移出可视树、采集被停掉没重启，
				// 或动画令牌作废让 sizeAnimating 卡死。音频回调约每 100ms 一次，防抖 2s 重建一次；
				// RebuildCollapsed 会重建跳动条并重新 StartEqualizer 复活采集。 ——
				long nowTicks = Environment.TickCount;
				bool barsAttached = equalizerBars != null && collapsedStack.Children.Contains(equalizerBars);
				if (sizeAnimating && nowTicks - sizeAnimStart > 1500)
				{
					sizeAnimating = false; // 主过渡最长 600ms；超时说明完成回调被令牌作废，强制复位防永久冻结
				}
				if (!barsAttached || !IslandAudio.IsRunning)
				{
					if (nowTicks - lastEqHealTick > 2000)
					{
						lastEqHealTick = nowTicks;
						RebuildCollapsed();
					}
					return;
				}
				if (sizeAnimating)
				{
					return; // 过渡动画期间冻结跳动条：90ms 的柱体动画会与主过渡抢渲染帧，制造“抽帧”感
				}
				int style = Math.Max(0, Math.Min(4, O.EqualizerStyle));
				if (style == 4)
				{
					UpdateSpectrumPath(levels);
					return;
				}
				int count = style == 1 ? 7 : 5;
				double[] folded = FoldBands(levels, count);
				if (style == 3)
				{
// 玻璃弹簧：速度积分 + 欠阻�?�?弹性过�?




for (int i = 0; i < count; i++)
					{
						double target = 0.35 + 0.65 * folded[i];
						eqVel[i] = eqVel[i] * 0.80 + (target - eqSpring[i]) * 0.32;
						eqSpring[i] = Math.Max(0.35, Math.Min(1.0, eqSpring[i] + eqVel[i]));
						SetBarScale(i, eqSpring[i]);
					}
					return;
				}
				for (int i = 0; i < count; i++)
				{
					double target = 0.35 + 0.65 * folded[i];
					SetBarScale(i, target);
					if (style == 2 && eqCaps != null && i < eqCaps.Length && eqCaps[i] != null)
					{
// 峰值帽：瞬间顶到当前高度，随后缓慢回落（与柱体不同的第二层运动�?





eqCap[i] = Math.Max(folded[i], eqCap[i] - 0.045);
						Canvas.SetBottom(eqCaps[i], 2.0 + 16.0 * eqCap[i]);
					}
				}
			});
		}
		catch
		{
		}
	}

	private void SetBarScale(int i, double target)
	{
		if (equalizerBarArr == null || i >= equalizerBarArr.Length || !(equalizerBarArr[i] is Border bar) || !(bar.RenderTransform is ScaleTransform st))
		{
			return;
		}
		if (!O.AnimationEnabled)
		{
			st.ScaleY = target;
		}
		else
		{
			st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(st.ScaleY, target, TimeSpan.FromMilliseconds(55)));
		}
	}

// �?24 频段折叠�?N 根柱的能量（分组平均）�?
private static double[] FoldBands(double[] levels, int count)
	{
		double[] folded = new double[count];
		double acc = 0.0;
		int n = 0;
		int group = 0;
		for (int i = 0; i < levels.Length; i++)
		{
			int g = Math.Min(count - 1, i * count / levels.Length);
			if (g != group)
			{
				if (n > 0)
				{
					folded[group] = acc / n;
				}
				group = g;
				acc = 0.0;
				n = 0;
			}
			acc += levels[i];
			n++;
		}
		if (n > 0)
		{
			folded[group] = acc / n;
		}
		return folded;
	}

// 样式 4�?4 频段平滑曲线（中点二次贝塞尔），起伏完全跟随频谱形状�?
private void UpdateSpectrumPath(double[] levels)
	{
		if (eqPath == null)
		{
			return;
		}
		try
		{
			double w = 96.0, h = 18.0;
			Point[] pts = new Point[IslandAudio.BandCount];
			for (int i = 0; i < IslandAudio.BandCount; i++)
			{
				double x = 1.0 + (w - 2.0) * i / (IslandAudio.BandCount - 1);
				double v = Math.Max(0.0, Math.Min(1.0, levels[i]));
				double y = h - 1.0 - (h - 2.0) * (0.15 + 0.85 * v);
				pts[i] = new Point(x, y);
			}
			PathFigure fig = new PathFigure { StartPoint = pts[0], IsClosed = false };
			for (int i = 1; i < pts.Length - 1; i++)
			{
				Point mid = new Point((pts[i].X + pts[i + 1].X) / 2.0, (pts[i].Y + pts[i + 1].Y) / 2.0);
				fig.Segments.Add(new QuadraticBezierSegment(pts[i], mid, true));
			}
			fig.Segments.Add(new LineSegment(pts[pts.Length - 1], true));
			PathGeometry geometry = new PathGeometry();
			geometry.Figures.Add(fig);
			geometry.Freeze();
			eqPath.Data = geometry;
		}
		catch
		{
		}
	}

	private FrameworkElement BatteryChip(string kind, double level, bool charging, Brush text, Brush muted, double iconSize, double gap)
	{
		StackPanel chip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(gap, 0, 0, 0) };
		Brush bar = IslandChrome.ParseColor("", LightTheme ? "#333A45" : "#DDE3EC") is Color c ? new SolidColorBrush(c) : text;
		Brush lowBrush = new SolidColorBrush(Color.FromRgb(229, 72, 77));
		bool low = level <= O.LowBatteryThreshold;
		FrameworkElement icon = kind == "pc"
			? IslandIcons.Battery(level / 100.0, charging, low ? lowBrush : text, bar, iconSize)
			: IslandIcons.Battery(level / 100.0, false, low ? lowBrush : text, bar, iconSize);
		chip.Children.Add(icon);
		TextBlock pct = new TextBlock
		{
			Text = ((int)Math.Round(level)) + "%",
			FontSize = Math.Max(10.5, O.FontSize - 1.5),
			Foreground = low ? lowBrush : text,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(3, 0, 0, 0)
		};
		chip.Children.Add(pct);
		return chip;
	}

	// —�?展开面板页面 —�?
	private Grid BuildMusicPage()
	{
		Grid g = new Grid();
		StackPanel root = new StackPanel
		{
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0, 6, 0, 0)
		};
		g.Children.Add(root);

		// 首行：封面靠左，曲目信息靠右
		Grid head = new Grid();
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

		Border coverShadow = new Border
		{
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			CornerRadius = new CornerRadius(16),
			Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.24, Color = Colors.Black }
		};
		bigCover = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(Math.Min(O.CornerRadius, 48.0)), ClipToBounds = true, Clip = new RectangleGeometry(new Rect(0, 0, 96, 96), Math.Min(O.CornerRadius, 48.0), Math.Min(O.CornerRadius, 48.0)) };
		coverShadow.Child = bigCover;
		Grid.SetColumn(coverShadow, 0);
		head.Children.Add(coverShadow);

		StackPanel info = new StackPanel
		{
			Margin = new Thickness(16, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center
		};
		musicTitle = new TextBlock
		{
			FontSize = 17,
			FontWeight = FontWeights.SemiBold,
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextAlignment = TextAlignment.Left,
			HorizontalAlignment = HorizontalAlignment.Left,
			MaxWidth = 240
		};
		musicArtist = new TextBlock
		{
			FontSize = 12,
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextAlignment = TextAlignment.Left,
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(0, 6, 0, 0),
			MaxWidth = 240
		};
		musicStatus = new TextBlock
		{
			FontSize = 11,
			TextAlignment = TextAlignment.Left,
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(0, 6, 0, 0)
		};
		info.Children.Add(musicTitle);
		info.Children.Add(musicArtist);
		info.Children.Add(musicStatus);
		Grid.SetColumn(info, 1);
		head.Children.Add(info);
		root.Children.Add(head);

// 进度行：横跨内容区，两端时间 + 大厂风细进度条（小圆点，悬停/拖动放大�?

Grid progressRow = new Grid { Margin = new Thickness(0, 18, 0, 0) };
		progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		positionText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MinWidth = 36, TextAlignment = TextAlignment.Left };
		durationText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MinWidth = 36, TextAlignment = TextAlignment.Right };
		progressSlider = new Slider { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), Style = BuildProgressSliderStyle() };
		progressSlider.PreviewMouseLeftButtonDown += (object s, MouseButtonEventArgs e) => { draggingProgress = true; };
		progressSlider.PreviewMouseLeftButtonUp += (object s, MouseButtonEventArgs e) => { draggingProgress = false; SeekProgress(); };
		progressSlider.LostMouseCapture += (object s, MouseEventArgs e) => { if (draggingProgress) { draggingProgress = false; SeekProgress(); } };
		Grid.SetColumn(positionText, 0);
		Grid.SetColumn(progressSlider, 1);
		Grid.SetColumn(durationText, 2);
		progressRow.Children.Add(positionText);
		progressRow.Children.Add(progressSlider);
		progressRow.Children.Add(durationText);
		musicProgressRow = progressRow;
		root.Children.Add(progressRow);

// 控制按钮：三枚同尺寸�?6px）等间距；播放键为高对比实心视觉中心，上一�?下一首较�?

StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
		prevBtn = IconButton(IslandIcons.Previous(IconBrush(), 18), delegate { M?.Action("previous"); });
		playBtn = IconButton(IslandIcons.Play(PlayIconBrush(), 20), delegate
		{
			if (M == null)
			{
				return;
			}
			if (M.Local && !M.Session.Available && !M.Playing)
			{
				M.Pick(this);
			}
			M.Action("toggle");
		});
		nextBtn = IconButton(IslandIcons.Next(IconBrush(), 18), delegate { M?.Action("next"); });
		foreach (Border b in new[] { prevBtn, playBtn, nextBtn })
		{
			b.Width = 46;
			b.Height = 46;
			b.CornerRadius = new CornerRadius(23);
		}
		prevBtn.Margin = new Thickness(0, 0, 7, 0);
		playBtn.Margin = new Thickness(7, 0, 7, 0);
		nextBtn.Margin = new Thickness(7, 0, 0, 0);
		playBtn.Background = PlayButtonBrush();
		playBtn.Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.22, Color = Colors.Black };
		buttons.Children.Add(prevBtn);
		buttons.Children.Add(playBtn);
		buttons.Children.Add(nextBtn);
		musicButtonsRow = buttons;
		root.Children.Add(buttons);
		return g;
	}

	private Brush PlayButtonBrush()
	{
		return new SolidColorBrush(textLight ? Color.FromArgb(235, 32, 36, 46) : Color.FromArgb(245, 255, 255, 255));
	}

	private Brush PlayIconBrush()
	{
		return new SolidColorBrush(textLight ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 40, 44, 52));
	}

// 大厂风细进度条：3px 圆头轨道 + 8px 小圆点；悬停放大 1.6×、拖动放�?2.4×，点击任意位置跳�?
// �?XamlReader 构建（Track 未实�?IAddChild，FrameworkElementFactory 无法 AppendChild�?
private Style BuildProgressSliderStyle()
	{
		try
		{
			string fill = textLight ? "#96000000" : "#E1FFFFFF";
			string track = textLight ? "#24000000" : "#30FFFFFF";
			string thumb = textLight ? "#FF282C34" : "#FFFFFFFF";
			string xaml =
				"<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"Slider\">" +
				"<Setter Property=\"Background\" Value=\"Transparent\"/>" +
				"<Setter Property=\"FocusVisualStyle\" Value=\"{x:Null}\"/>" +
				"<Setter Property=\"Template\"><Setter.Value>" +
				"<ControlTemplate TargetType=\"Slider\"><Grid Height=\"16\">" +
				"<Track x:Name=\"PART_Track\" VerticalAlignment=\"Center\" Value=\"{TemplateBinding Value}\" Maximum=\"{TemplateBinding Maximum}\" Minimum=\"{TemplateBinding Minimum}\" IsDirectionReversed=\"{TemplateBinding IsDirectionReversed}\">" +
				"<Track.DecreaseRepeatButton><RepeatButton Command=\"Slider.DecreaseLarge\"><RepeatButton.Template>" +
				"<ControlTemplate TargetType=\"RepeatButton\"><Border Background=\"" + fill + "\" Height=\"3\" CornerRadius=\"1.5\"/></ControlTemplate>" +
				"</RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>" +
				"<Track.Thumb><Thumb RenderTransformOrigin=\"0.5,0.5\"><Thumb.Template>" +
				"<ControlTemplate TargetType=\"Thumb\"><Ellipse Width=\"8\" Height=\"8\" Fill=\"" + thumb + "\"/></ControlTemplate>" +
				"</Thumb.Template><Thumb.Style><Style TargetType=\"Thumb\"><Style.Triggers>" +
				"<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter Property=\"RenderTransform\"><Setter.Value><ScaleTransform ScaleX=\"1.6\" ScaleY=\"1.6\"/></Setter.Value></Setter></Trigger>" +
				"<Trigger Property=\"IsDragging\" Value=\"True\"><Setter Property=\"RenderTransform\"><Setter.Value><ScaleTransform ScaleX=\"2.4\" ScaleY=\"2.4\"/></Setter.Value></Setter></Trigger>" +
				"</Style.Triggers></Style></Thumb.Style></Thumb></Track.Thumb>" +
				"<Track.IncreaseRepeatButton><RepeatButton Command=\"Slider.IncreaseLarge\"><RepeatButton.Template>" +
				"<ControlTemplate TargetType=\"RepeatButton\"><Border Background=\"" + track + "\" Height=\"3\" CornerRadius=\"1.5\"/></ControlTemplate>" +
				"</RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>" +
				"</Track></Grid></ControlTemplate>" +
				"</Setter.Value></Setter></Style>";
			return (Style)XamlReader.Parse(xaml);
		}
		catch (Exception ex)
		{
			Store.Trace("ISLAND slider style failed: " + ex.GetType().Name + " " + ex.Message);
			return new Style(typeof(Slider));
		}
	}

	private void SeekProgress()
	{
		if (M == null || !M.Session.Available || !M.CanSeek || progressSlider.Maximum <= 0)
		{
			return;
		}
		M.Seek(progressSlider.Value);
	}

	// 输出设备下拉框样式：跟随灵动岛深浅配色（textLight），圆角胶囊+圆角弹层，
	// 替换掉默认的 Aero 灰底 ComboBox（之前输出设备行是未样式化的原生控件，与岛风格割裂）。
	private Style BuildComboStyle()
	{
		try
		{
			string text = textLight ? "#FF282C34" : "#FFFFFFFF";
			string muted = textLight ? "#FF8A95A8" : "#FFC2CCDE";
			string card = textLight ? "#30000000" : "#24FFFFFF";
			string line = textLight ? "#28000000" : "#26FFFFFF";
			string popupBg = textLight ? "#F7FAFF" : "#222B3D";
			string xaml =
				"<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"ComboBox\">" +
				"<Setter Property=\"Foreground\" Value=\"" + text + "\"/>" +
				"<Setter Property=\"FocusVisualStyle\" Value=\"{x:Null}\"/>" +
				"<Setter Property=\"Template\"><Setter.Value>" +
				"<ControlTemplate TargetType=\"ComboBox\"><Grid>" +
				"<ToggleButton Focusable=\"False\" IsChecked=\"{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}\">" +
				"<ToggleButton.Template><ControlTemplate TargetType=\"ToggleButton\"><Border Background=\"" + card + "\" BorderBrush=\"" + line + "\" BorderThickness=\"1\" CornerRadius=\"7\"/></ControlTemplate></ToggleButton.Template>" +
				"</ToggleButton>" +
				"<ContentPresenter Content=\"{TemplateBinding SelectionBoxItem}\" ContentTemplate=\"{TemplateBinding SelectionBoxItemTemplate}\" Margin=\"10,3,24,3\" VerticalAlignment=\"Center\" IsHitTestVisible=\"False\"/>" +
				"<TextBlock Text=\"⌄\" Foreground=\"" + muted + "\" FontSize=\"12\" HorizontalAlignment=\"Right\" VerticalAlignment=\"Center\" Margin=\"0,0,9,0\" IsHitTestVisible=\"False\"/>" +
				"<Popup x:Name=\"PART_Popup\" Placement=\"Bottom\" AllowsTransparency=\"True\" StaysOpen=\"False\" PopupAnimation=\"Fade\" IsOpen=\"{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}\">" +
				"<Border Background=\"" + popupBg + "\" BorderBrush=\"" + line + "\" BorderThickness=\"1\" CornerRadius=\"10\" Margin=\"0,4,0,4\" MinWidth=\"170\">" +
				"<ScrollViewer MaxHeight=\"240\" VerticalScrollBarVisibility=\"Hidden\" HorizontalScrollBarVisibility=\"Disabled\"><ItemsPresenter/></ScrollViewer>" +
				"</Border></Popup>" +
				"</Grid></ControlTemplate>" +
				"</Setter.Value></Setter></Style>";
			return (Style)XamlReader.Parse(xaml);
		}
		catch (Exception ex)
		{
			Store.Trace("ISLAND combo style failed: " + ex.GetType().Name + " " + ex.Message);
			return new Style(typeof(ComboBox));
		}
	}

	private Style BuildComboItemStyle()
	{
		try
		{
			string text = textLight ? "#FF282C34" : "#FFFFFFFF";
			string muted = textLight ? "#FF7C8799" : "#FFB9C4D8";
			string itemHover = textLight ? "#D9E7FF" : "#3A5074";
			string xaml =
				"<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"ComboBoxItem\">" +
				"<Setter Property=\"Foreground\" Value=\"" + text + "\"/>" +
				"<Setter Property=\"Padding\" Value=\"12,7,12,7\"/>" +
				"<Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\"/>" +
				"<Setter Property=\"Template\"><Setter.Value>" +
				"<ControlTemplate TargetType=\"ComboBoxItem\"><Border x:Name=\"Bd\" Background=\"Transparent\" CornerRadius=\"8\" Margin=\"3,1,3,1\" Padding=\"{TemplateBinding Padding}\"><ContentPresenter/></Border>" +
				"<ControlTemplate.Triggers>" +
				"<Trigger Property=\"IsHighlighted\" Value=\"True\"><Setter TargetName=\"Bd\" Property=\"Background\" Value=\"" + itemHover + "\"/></Trigger>" +
				"<Trigger Property=\"IsSelected\" Value=\"True\"><Setter Property=\"FontWeight\" Value=\"SemiBold\"/></Trigger>" +
				"</ControlTemplate.Triggers></ControlTemplate>" +
				"</Setter.Value></Setter></Style>";
			return (Style)XamlReader.Parse(xaml);
		}
		catch (Exception ex)
		{
			Store.Trace("ISLAND combo item style failed: " + ex.GetType().Name + " " + ex.Message);
			return new Style(typeof(ComboBoxItem));
		}
	}

	private Grid BuildNotificationsPage()
	{
		Grid g = new Grid();
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		Grid head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		TextBlock headTitle = new TextBlock { Text = "通知", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center };
		Border clearBtn = TextButton("清空", delegate { IslandNotifications.Clear(); RefreshNotificationsPage(); });
		Grid.SetColumn(clearBtn, 1);
		head.Children.Add(headTitle);
		head.Children.Add(clearBtn);
		Grid.SetRow(head, 0);
		g.Children.Add(head);
		notifCats = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
		Grid.SetRow(notifCats, 1);
		g.Children.Add(notifCats);
		ScrollViewer sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 320 };
		notifList = new StackPanel();
		sv.Content = notifList;
		Grid.SetRow(sv, 2);
		g.Children.Add(sv);
		return g;
	}

	private Grid BuildClipboardPage()
	{
		Grid g = new Grid();
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		StackPanel head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6) };
		Border pauseBtn = TextButton(IslandClipboard.IsPaused ? "继续" : "暂停", delegate
		{
			IslandClipboard.SetPaused(!IslandClipboard.IsPaused);
			(head.Children[0] as Border).Child = new TextBlock { Text = IslandClipboard.IsPaused ? "继续" : "暂停", FontSize = 12, Foreground = TextBrush, Margin = new Thickness(10, 4, 10, 4) };
		});
		Border clearBtn = TextButton("清空", delegate { IslandClipboard.Clear(); RefreshClipboardPage(); });
		head.Children.Add(pauseBtn);
		head.Children.Add(clearBtn);
		clipToast = new Border
		{
			CornerRadius = new CornerRadius(7),
			Background = CardBrush(),
			Padding = new Thickness(10, 3, 10, 3),
			Margin = new Thickness(8, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = Visibility.Collapsed,
			Child = new TextBlock { Text = "复制成功 ✓", FontSize = 11, Foreground = TextBrush }
		};
		head.Children.Add(clipToast);
		Grid.SetRow(head, 0);
		g.Children.Add(head);
		ScrollViewer sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 320 };
		clipList = new StackPanel();
		sv.Content = clipList;
		Grid.SetRow(sv, 1);
		g.Children.Add(sv);
		return g;
	}

	// 剪贴板页顶部短暂显示“复制成功”提示（1.4 秒后自动消失）。
	private void ShowClipToast()
	{
		if (clipToast == null)
		{
			return;
		}
		clipToast.Visibility = Visibility.Visible;
		int token = ++clipToastToken;
		DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
		t.Tick += delegate
		{
			t.Stop();
			if (token == clipToastToken)
			{
				clipToast.Visibility = Visibility.Collapsed;
			}
		};
		t.Start();
	}

	private Grid BuildNotesPage()
	{
		Grid g = new Grid();
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		// 头行：全部随手记计数 + 新建按钮
		Grid head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		TextBlock count = new TextBlock
		{
			FontSize = 12,
			Foreground = MutedBrush,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(count, 0);
		head.Children.Add(count);
		noteStatus = count;
		Border addBtn = TextButton("＋ 新建", delegate
		{
			NotesRepository repo = C.Notes;
			if (repo == null)
			{
				return;
			}
			OpenNoteEditor(null, true);
		});
		addBtn.Margin = new Thickness(8, 0, 0, 0);
		addBtn.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(addBtn, 1);
		head.Children.Add(addBtn);
		Grid.SetRow(head, 0);
		g.Children.Add(head);

		// 所有随手记列表
		ScrollViewer scroll = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			MaxHeight = 320,
			Focusable = false
		};
		noteList = new StackPanel();
		scroll.Content = noteList;
		Grid.SetRow(scroll, 1);
		g.Children.Add(scroll);
		return g;
	}

	private Border NoteRow(NoteItem item)
	{
		Border card = new Border
		{
			CornerRadius = new CornerRadius(10),
			Background = CardBrush(),
			Padding = new Thickness(10, 8, 8, 8),
			Margin = new Thickness(0, 0, 0, 6),
			Cursor = Cursors.Hand
		};
		Grid g = new Grid();
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		StackPanel left = new StackPanel();
		TextBlock t = new TextBlock
		{
			Text = item.DisplayTitle,
			FontSize = 13.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = TextBrush,
			TextWrapping = TextWrapping.Wrap,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		left.Children.Add(t);
		string remindText = item.RemindAt > 0 ? "提醒 " + FormatRemind(item.RemindAt) : "无提醒";
		string updateText = new DateTime(item.Updated, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm");
		left.Children.Add(new TextBlock
		{
			Text = remindText + " · 更新 " + updateText,
			FontSize = 10.5,
			Foreground = MutedBrush,
			Margin = new Thickness(0, 3, 0, 0)
		});
		Grid.SetColumn(left, 0);
		g.Children.Add(left);
		Border del = new Border
		{
			CornerRadius = new CornerRadius(8),
			Background = ChipBrush(),
			Cursor = Cursors.Hand,
			Margin = new Thickness(10, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center
		};
		del.Child = new TextBlock { Text = "删除", FontSize = 11.5, Foreground = TextBrush, Margin = new Thickness(8, 3, 8, 3) };
		del.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			e.Handled = true; // 阻止冒泡到卡片：删除的同时绝不打开编辑页
			NotesRepository repo = C.Notes;
			if (repo == null)
			{
				return;
			}
			if (!(del.Child is TextBlock tb))
			{
				return;
			}
			if (tb.Text != "确认删除？")
			{
				tb.Text = "确认删除？";
				DispatcherTimer tmr = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
				tmr.Tick += delegate
				{
					tmr.Stop();
					if (tb.Text == "确认删除？")
					{
						tb.Text = "删除";
					}
				};
				tmr.Start();
				return;
			}
			repo.Select(item.Id);
			repo.Remove();
			RefreshNotesPage();
		};
		Grid.SetColumn(del, 1);
		g.Children.Add(del);
		card.Child = g;
		card.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) => OpenNoteViewer(item);
		return card;
	}

	private static string FormatRemind(long utcTicks)
	{
		DateTime t = new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();
		if (t.Date == DateTime.Today)
		{
			return "今天 " + t.ToString("HH:mm");
		}
		if (t.Date == DateTime.Today.AddDays(1))
		{
			return "明天 " + t.ToString("HH:mm");
		}
		return t.ToString("MM-dd HH:mm");
	}

	// 查看页（sub page 13）：点列表项先看标题与内容，右上角"编辑"才进编辑页。
	private Grid BuildNoteViewPage()
	{
		Grid g = new Grid();
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		Grid titleRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
		titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		noteViewTitle = new TextBlock
		{
			FontSize = 17,
			FontWeight = FontWeights.SemiBold,
			Foreground = TextBrush,
			TextWrapping = TextWrapping.Wrap,
			VerticalAlignment = VerticalAlignment.Center
		};
		titleRow.Children.Add(noteViewTitle);
		Border viewEditBtn = TextButton("编辑", delegate
		{
			if (currentNote != null)
			{
				OpenNoteEditor(currentNote, false);
			}
		});
		viewEditBtn.Margin = new Thickness(10, 0, 0, 0);
		viewEditBtn.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(viewEditBtn, 1);
		titleRow.Children.Add(viewEditBtn);
		Grid.SetRow(titleRow, 0);
		g.Children.Add(titleRow);

		noteViewBody = new TextBlock
		{
			FontSize = 13,
			Foreground = TextBrush,
			TextWrapping = TextWrapping.Wrap,
			LineHeight = 20,
			Margin = new Thickness(0, 0, 0, 10)
		};
		Grid.SetRow(noteViewBody, 1);
		g.Children.Add(noteViewBody);

		noteViewStatus = new TextBlock { FontSize = 11, Foreground = MutedBrush };
		Grid.SetRow(noteViewStatus, 2);
		g.Children.Add(noteViewStatus);
		return g;
	}

	private void OpenNoteViewer(NoteItem item)
	{
		NotesRepository repo = C.Notes;
		if (repo == null || item == null)
		{
			return;
		}
		currentNote = item;
		noteNewMode = false;
		noteViewTitle.Text = string.IsNullOrWhiteSpace(item.Title) ? item.DisplayTitle : item.Title;
		noteViewBody.Text = string.IsNullOrWhiteSpace(item.Body) ? "（空内容）" : item.Body;
		noteViewStatus.Text = "更新 " + new DateTime(item.Updated, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm") + (item.RemindAt > 0 ? " · 提醒 " + FormatRemind(item.RemindAt) : "");
		subBack = 4;
		SetPage(13);
	}

	// 编辑/新建随手记的独立页面（sub page 12）。标题与内容都不是必填项：
	// 空标题自动记为“未命名”，空内容也允许保存。
	private Grid BuildNoteEditPage()
	{
		Grid g = new Grid();
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		// 提醒行：状态 + 设置按钮
		Grid remindRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
		remindRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		remindRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		StackPanel remindInfo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		remindInfo.Children.Add(new TextBlock { Text = "提醒  ", FontSize = 11.5, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center });
		remindLabel = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center, Text = "未设置" };
		remindInfo.Children.Add(remindLabel);
		Grid.SetColumn(remindInfo, 0);
		remindRow.Children.Add(remindInfo);
		Border remindToggle = TextButton("设置", delegate
		{
			if (remindPanel == null)
			{
				return;
			}
			remindPanel.Visibility = remindPanel.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
			SyncExpandedSize();
		});
		remindToggle.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(remindToggle, 1);
		remindRow.Children.Add(remindToggle);
		Grid.SetRow(remindRow, 0);
		g.Children.Add(remindRow);

		// 提醒设置面板（默认收起）
		remindPanel = new Border
		{
			Visibility = Visibility.Collapsed,
			CornerRadius = new CornerRadius(10),
			Background = CardBrush(),
			Padding = new Thickness(10, 8, 10, 8),
			Margin = new Thickness(0, 0, 0, 8)
		};
		StackPanel remindBody = new StackPanel();
		StackPanel presets = new StackPanel { Orientation = Orientation.Horizontal };
		Border p30 = TextButton("30 分钟后", delegate { SetRemind(DateTime.Now.AddMinutes(30)); });
		p30.Margin = new Thickness(0, 0, 4, 0);
		Border p60 = TextButton("1 小时后", delegate { SetRemind(DateTime.Now.AddHours(1)); });
		p60.Margin = new Thickness(0, 0, 4, 0);
		Border p20 = TextButton("今晚 20:00", delegate
		{
			DateTime t2 = DateTime.Today.AddHours(20);
			if (t2 <= DateTime.Now)
			{
				t2 = t2.AddDays(1);
			}
			SetRemind(t2);
		});
		p20.Margin = new Thickness(0, 0, 4, 0);
		Border p9 = TextButton("明天 09:00", delegate { SetRemind(DateTime.Today.AddDays(1).AddHours(9)); });
		p9.Margin = new Thickness(0, 0, 4, 0);
		presets.Children.Add(p30);
		presets.Children.Add(p60);
		presets.Children.Add(p20);
		presets.Children.Add(p9);
		remindBody.Children.Add(presets);
		StackPanel quick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
		Border q5 = TextButton("5 分钟后", delegate { SetRemind(DateTime.Now.AddMinutes(5)); });
		q5.Margin = new Thickness(0, 0, 4, 0);
		Border q15 = TextButton("15 分钟后", delegate { SetRemind(DateTime.Now.AddMinutes(15)); });
		q15.Margin = new Thickness(0, 0, 4, 0);
		Border q45 = TextButton("45 分钟后", delegate { SetRemind(DateTime.Now.AddMinutes(45)); });
		q45.Margin = new Thickness(0, 0, 4, 0);
		Border q2h = TextButton("2 小时后", delegate { SetRemind(DateTime.Now.AddHours(2)); });
		q2h.Margin = new Thickness(0, 0, 4, 0);
		quick.Children.Add(q5);
		quick.Children.Add(q15);
		quick.Children.Add(q45);
		quick.Children.Add(q2h);
		remindBody.Children.Add(quick);
		Grid custom = new Grid { Margin = new Thickness(0, 8, 0, 0) };
		custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		custom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		remindCustom = RoundTextBox("自定义时间（HH:mm）");
		remindCustom.Width = 76;
		remindCustom.MaxLength = 5;
		remindCustom.FontSize = 12.5;
		remindCustom.VerticalContentAlignment = VerticalAlignment.Center;
		Grid.SetColumn(remindCustom, 0);
		custom.Children.Add(remindCustom);
		TextBlock hint = new TextBlock { Text = "  如 21:30", FontSize = 11.5, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center };
		Grid.SetColumn(hint, 1);
		custom.Children.Add(hint);
		Border setCustom = TextButton("设提醒", delegate
		{
			DateTime parsed;
			if (!DateTime.TryParseExact((remindCustom.Text ?? "").Trim(), "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsed))
			{
				remindCustom.Text = "";
				return;
			}
			DateTime target = DateTime.Today.Add(parsed.TimeOfDay);
			if (target <= DateTime.Now)
			{
				target = target.AddDays(1);
			}
			SetRemind(target);
			remindCustom.Text = "";
		});
		setCustom.Margin = new Thickness(10, 0, 0, 0);
		Grid.SetColumn(setCustom, 2);
		custom.Children.Add(setCustom);
		Border clearRemind = TextButton("清除", delegate { ClearRemind(); });
		clearRemind.Margin = new Thickness(6, 0, 0, 0);
		Grid.SetColumn(clearRemind, 3);
		custom.Children.Add(clearRemind);
		remindBody.Children.Add(custom);
		remindPanel.Child = remindBody;
		Grid.SetRow(remindPanel, 1);
		g.Children.Add(remindPanel);

		// 标题（可留空）
		noteTitle = RoundTextBox("标题（可留空）");
		noteTitle.FontSize = 14;
		noteTitle.MaxLength = 120;
		noteTitle.FontWeight = FontWeights.SemiBold;
		noteTitle.MinHeight = 34;
		noteTitle.TextWrapping = TextWrapping.Wrap;
		noteTitle.VerticalContentAlignment = VerticalAlignment.Center;
		noteTitle.Margin = new Thickness(0, 0, 0, 6);
		noteTitle.TextChanged += (object s, TextChangedEventArgs e) => NoteEdited();
		Grid.SetRow(noteTitle, 2);
		g.Children.Add(noteTitle);

		// 正文（可留空）
		noteBody = RoundTextBox("内容（可留空）");
		noteBody.Height = 170;
		noteBody.VerticalContentAlignment = VerticalAlignment.Top;
		noteBody.AcceptsReturn = true;
		noteBody.AcceptsTab = true;
		noteBody.TextWrapping = TextWrapping.Wrap;
		noteBody.FontSize = 13;
		noteBody.TextChanged += (object s, TextChangedEventArgs e) => NoteEdited();
		Grid.SetRow(noteBody, 3);
		g.Children.Add(noteBody);

		// 按钮行：保存（新建模式）/ 待办 / 导出 / 删除（编辑模式）
		StackPanel btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
		saveBtn = TextButton("保存", delegate { SaveNewNote(); });
		saveBtn.Margin = new Thickness(0, 0, 4, 0);
		saveBtn.Visibility = Visibility.Collapsed;
		btns.Children.Add(saveBtn);
		btns.Children.Add(TextButton("☐ 待办", delegate
		{
			if (noteBody == null)
			{
				return;
			}
			noteBody.SelectedText = "☐ ";
			noteBody.Focus();
		}));
		btns.Children.Add(TextButton("导出", delegate { ExportCurrentNote(); }));
		Border delBtn = null;
		delBtn = TextButton("删除", delegate
		{
			NotesRepository repo = C.Notes;
			if (repo == null || currentNote == null)
			{
				return;
			}
			if (!(delBtn.Child is TextBlock tb))
			{
				return;
			}
			if (tb.Text != "确认删除？")
			{
				tb.Text = "确认删除？";
				DispatcherTimer tmr = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
				tmr.Tick += delegate
				{
					tmr.Stop();
					if (tb.Text == "确认删除？")
					{
						tb.Text = "删除";
					}
				};
				tmr.Start();
				return;
			}
			repo.Select(currentNote.Id);
			repo.Remove();
			RefreshNotesPage();
			SetPage(4);
		});
		delBtn.Visibility = Visibility.Collapsed;
		editDelBtn = delBtn;
		btns.Children.Add(delBtn);
		Grid.SetRow(btns, 4);
		g.Children.Add(btns);

		editStatus = new TextBlock
		{
			FontSize = 10.5,
			Foreground = MutedBrush,
			Margin = new Thickness(0, 8, 0, 0)
		};
		Grid.SetRow(editStatus, 5);
		g.Children.Add(editStatus);
		return g;
	}

	private void SetRemind(DateTime local)
	{
		NotesRepository repo = C.Notes;
		if (repo == null)
		{
			return;
		}
		long ticks = local.ToUniversalTime().Ticks;
		if (currentNote != null)
		{
			currentNote.RemindAt = ticks;
			repo.Changed();
		}
		else
		{
			pendingRemind = ticks; // 新建模式：保存时才落到新笔记
		}
		UpdateRemindLabel();
		remindPanel.Visibility = Visibility.Collapsed;
		SyncExpandedSize();
		if (editStatus != null)
		{
			editStatus.Text = repo.Status + " · 提醒 " + FormatRemind(ticks);
		}
	}

	private void ClearRemind()
	{
		NotesRepository repo = C.Notes;
		if (repo == null)
		{
			return;
		}
		if (currentNote != null)
		{
			currentNote.RemindAt = 0;
			repo.Changed();
		}
		pendingRemind = 0;
		UpdateRemindLabel();
		remindPanel.Visibility = Visibility.Collapsed;
		SyncExpandedSize();
		if (editStatus != null)
		{
			editStatus.Text = repo.Status + " · 已清除提醒";
		}
	}

	private void UpdateRemindLabel()
	{
		if (remindLabel == null)
		{
			return;
		}
		long ticks = currentNote != null ? currentNote.RemindAt : pendingRemind;
		remindLabel.Text = ticks > 0 ? FormatRemind(ticks) : "未设置";
	}

	private void SaveNewNote()
	{
		NotesRepository repo = C.Notes;
		if (repo == null || !noteNewMode)
		{
			return;
		}
		repo.Add();
		currentNote = repo.Book.Selected;
		currentNote.Title = (noteTitle.Text ?? "").Trim();
		currentNote.Body = noteBody.Text ?? "";
		currentNote.RemindAt = pendingRemind;
		pendingRemind = 0;
		repo.Changed();
		repo.Flush();
		RefreshNotesPage();
		SetPage(4);
	}

	private void ExportCurrentNote()
	{
		NotesRepository repo = C.Notes;
		if (repo == null || currentNote == null)
		{
			return;
		}
		repo.Flush();
		try
		{
			string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			string safe = (currentNote.DisplayTitle ?? "随手记").Replace("\\", "_").Replace("/", "_").Replace(":", "_").Replace("*", "_").Replace("?", "_").Replace("\"", "_").Replace("<", "_").Replace(">", "_").Replace("|", "_");
			string dest = System.IO.Path.Combine(desktop, safe + "-" + DateTime.Now.ToString("MMdd-HHmm") + ".txt");
			repo.Export(dest);
			if (editStatus != null)
			{
				editStatus.Text = "已导出到桌面：" + System.IO.Path.GetFileName(dest);
			}
		}
		catch (Exception ex)
		{
			if (editStatus != null)
			{
				editStatus.Text = "导出失败：" + ex.Message;
			}
		}
	}

	// 打开编辑页：isNew=true 时进入新建模式（标题/内容都可不填，保存才落盘）。
	private void OpenNoteEditor(NoteItem item, bool isNew = false)
	{
		NotesRepository repo = C.Notes;
		if (repo == null)
		{
			return;
		}
		noteNewMode = isNew;
		currentNote = isNew ? null : item;
		pendingRemind = isNew ? 0 : item.RemindAt;
		noteHeading = isNew ? "新建随手记" : "编辑随手记";
		editingNotes = true;
		noteTitle.Text = isNew ? "" : item.Title;
		noteBody.Text = isNew ? "" : item.Body;
		editingNotes = false;
		UpdateRemindLabel();
		remindPanel.Visibility = Visibility.Collapsed;
		saveBtn.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
		editDelBtn.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
		subBack = 4;
		SetPage(12);
	}

	public void RefreshNotesPage()
	{
		NotesRepository repo = C.Notes;
		if (repo == null || noteList == null)
		{
			return;
		}
		noteList.Children.Clear();
		foreach (NoteItem item in repo.Book.Items.AsEnumerable().Reverse())
		{
			noteList.Children.Add(NoteRow(item));
		}
		if (noteStatus != null)
		{
			noteStatus.Text = "全部 " + repo.Book.Items.Count + " 条 · " + repo.Status;
		}
	}

	private void NoteEdited()
	{
		NotesRepository repo = C.Notes;
		if (repo == null || editingNotes || currentNote == null || noteNewMode)
		{
			return;
		}
		currentNote.Title = noteTitle.Text;
		currentNote.Body = noteBody.Text;
		repo.Changed();
		if (editStatus != null)
		{
			editStatus.Text = repo.Status;
		}
	}

	private Grid BuildControlPage()
	{
		Grid g = new Grid();
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		Grid tiles = new Grid();
		tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		wifiTile = SmallTile("Wi-Fi", b => IslandIcons.PathIcon("M11.8 20 h0.4 M2 8.82 a15 15 0 0 1 20 0 M5 12.86 a10 10 0 0 1 14 0 M8.5 16.43 a5 5 0 0 1 7 0", b, 18, fill: false, strokeWidth: 2.0), out wifiStatus, out wifiTileName, delegate { ToggleRadio("wifi"); }, delegate { SetPage(10); });
		btTile = SmallTile("蓝牙", b => IslandIcons.PathIcon("m7 7 l10 10 l-5 5 V2 l5 5 L7 17", b, 18, fill: false, strokeWidth: 2.0), out btStatus, out btTileName, delegate { ToggleRadio("bt"); }, delegate { SetPage(11); });
		Grid.SetColumn(wifiTile, 0);
		Grid.SetColumn(btTile, 1);
		wifiTile.Margin = new Thickness(0, 0, 5, 0);
		btTile.Margin = new Thickness(5, 0, 0, 0);
		tiles.Children.Add(wifiTile);
		tiles.Children.Add(btTile);
		Grid.SetRow(tiles, 0);
		g.Children.Add(tiles);

		Grid volumeRow = new Grid { Margin = new Thickness(2, 10, 2, 0) };
		volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		// 音量图标放在最前面，并兼作静音按钮：点击切换静音，静音时图标变暗。
		volumeIconHost = new Border
		{
			Width = 30,
			Height = 24,
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.Hand,
			ToolTip = "点击静音 / 取消静音",
			Child = IslandIcons.Speaker(TextBrush, 16)
		};
		volumeIconHost.MouseLeftButtonUp += delegate
		{
			AudioCore.TryGetVolume(out float vol, out bool m);
			AudioCore.TrySetMute(!m);
			RefreshControlQuick();
		};
		Grid.SetColumn(volumeIconHost, 0);
		volumeRow.Children.Add(volumeIconHost);
		volumeSlider = new Slider { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), Style = BuildProgressSliderStyle() };
		volumeSlider.ValueChanged += delegate
		{
			if (volumeSyncing)
			{
				return;
			}
			// 修正音量刻度：滑杆 0-100，WASAPI 接口收 0-1
			AudioCore.TrySetVolume((float)(volumeSlider.Value / 100.0));
			volumeValue.Text = ((int)Math.Round(volumeSlider.Value)).ToString();
		};
		Grid.SetColumn(volumeSlider, 1);
		volumeRow.Children.Add(volumeSlider);
		volumeValue = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MinWidth = 26, TextAlignment = TextAlignment.Right };
		Grid.SetColumn(volumeValue, 2);
		volumeRow.Children.Add(volumeValue);
		Grid.SetRow(volumeRow, 1);
		g.Children.Add(volumeRow);

		Grid outputRow = new Grid { Margin = new Thickness(2, 10, 2, 0) };
		outputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		outputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		outputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		// 输出设备行图标：与音量行同宽的扬声器轮廓，保证各行图标左对齐（此前该行无图标）
		outputIconHost = new Border { Width = 30, Height = 24, VerticalAlignment = VerticalAlignment.Center, Child = IslandIcons.PathIcon("M4 9.5 V14.5 H7.5 L12.5 19 V5 L7.5 9.5 Z M15.5 8.8 A3.8 3.8 0 0 1 15.5 15.2 M17.5 6.8 A6.5 6.5 0 0 1 17.5 17.2", MutedBrush, 16, fill: false, strokeWidth: 1.6) };
		Grid.SetColumn(outputIconHost, 0);
		outputRow.Children.Add(outputIconHost);
		TextBlock outLabel = new TextBlock { Text = "输出设备", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Foreground = TextBrush };
		outputLabel = outLabel;
		Grid.SetColumn(outLabel, 1);
		outputRow.Children.Add(outLabel);
		outputCombo = new ComboBox { VerticalAlignment = VerticalAlignment.Center, Height = 28, Style = BuildComboStyle(), ItemContainerStyle = BuildComboItemStyle(), Focusable = false };
		outputCombo.SelectionChanged += delegate
		{
			if (outputCombo.SelectedItem is AudioCore.AudioDevice d && !d.IsDefault)
			{
				AudioCore.TrySetDefault(d.Id);
			}
		};
		Grid.SetColumn(outputCombo, 2);
		outputRow.Children.Add(outputCombo);
		Grid.SetRow(outputRow, 2);
		g.Children.Add(outputRow);

		Grid brightnessRow = new Grid { Margin = new Thickness(2, 10, 2, 0) };
		brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		brightnessIconHost = new Border { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center, Child = IslandIcons.PathIcon("M8 12 a4 4 0 1 1 8 0 a4 4 0 1 1 -8 0 M12 2 v2 M12 20 v2 M4.93 4.93 l1.41 1.41 M17.66 17.66 l1.41 1.41 M2 12 h2 M20 12 h2 M6.34 17.66 l-1.41 1.41 M19.07 4.93 l-1.41 1.41", TextBrush, 16, fill: false, strokeWidth: 1.6) };
		Grid.SetColumn(brightnessIconHost, 0);
		brightnessRow.Children.Add(brightnessIconHost);
		brightnessSlider = new Slider { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), Style = BuildProgressSliderStyle() };
		brightnessSlider.ValueChanged += delegate
		{
			if (brightnessSyncing)
			{
				return;
			}
			BrightnessCore.TrySet((int)Math.Round(brightnessSlider.Value));
			brightnessValue.Text = ((int)Math.Round(brightnessSlider.Value)).ToString();
		};
		Grid.SetColumn(brightnessSlider, 1);
		brightnessRow.Children.Add(brightnessSlider);
		brightnessValue = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MinWidth = 26, TextAlignment = TextAlignment.Right };
		Grid.SetColumn(brightnessValue, 2);
		brightnessRow.Children.Add(brightnessValue);
		Grid.SetRow(brightnessRow, 3);
		g.Children.Add(brightnessRow);

		// 电量区：电脑 / 鼠标 / 键盘——只显示检测到有效电量的项目，无数据整卡隐藏。
		Grid batteryRow = new Grid { Margin = new Thickness(2, 12, 2, 0) };
		WrapPanel batteryWrap = new WrapPanel();
		pcBatCard = BuildBatteryCard(out pcBatText);
		mouseBatCard = BuildBatteryCard(out mouseBatText);
		kbBatCard = BuildBatteryCard(out kbBatText);
		batteryWrap.Children.Add(pcBatCard);
		batteryWrap.Children.Add(mouseBatCard);
		batteryWrap.Children.Add(kbBatCard);
		batteryRow.Children.Add(batteryWrap);
		Grid.SetRow(batteryRow, 4);
		g.Children.Add(batteryRow);

		Grid note = new Grid { Margin = new Thickness(2, 12, 2, 0) };
		controlNote = new TextBlock
		{
			Text = "点磁贴开/关 Wi-Fi 与蓝牙，点磁贴右侧箭头进入网络列表与蓝牙设备页。",
			FontSize = 11,
			TextWrapping = TextWrapping.Wrap,
			Foreground = MutedBrush
		};
		note.Children.Add(controlNote);
		Grid.SetRow(note, 5);
		g.Children.Add(note);
		return g;
	}

	// 电量信息卡：图标 + 文本，默认隐藏，读到有效数据才显示（无数据不留空位）。
	private Border BuildBatteryCard(out TextBlock label)
	{
		Border card = new Border
		{
			CornerRadius = new CornerRadius(10),
			Background = CardBrush(),
			Padding = new Thickness(10, 7, 12, 7),
			Margin = new Thickness(0, 0, 8, 8),
			Visibility = Visibility.Collapsed
		};
		StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };
		Border iconHost = new Border { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
		sp.Children.Add(iconHost);
		label = new TextBlock
		{
			FontSize = 11,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(7, 0, 0, 0),
			Foreground = TextBrush,
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = 190
		};
		sp.Children.Add(label);
		card.Child = sp;
		card.Tag = iconHost;
		return card;
	}

	private static readonly string[] MouseIconPath = { "M8 3 H16 A6 6 0 0 1 22 9 V15 A6 6 0 0 1 16 21 H8 A6 6 0 0 1 2 15 V9 A6 6 0 0 1 8 3 Z M12 8 V11" };
	private static readonly string[] KeyboardIconPath = { "M2 7 H22 V17 H2 Z M6 10.2 H6.01 M10 10.2 H10.01 M14 10.2 H14.01 M18 10.2 H18.01 M6 13.8 H6.01 M10 13.8 H14 M18 13.8 H18.01" };

	private void FillBatteryCard(Border card, TextBlock label, double? level, bool charging, string deviceLabel, string iconPath)
	{
		card.Visibility = level.HasValue ? Visibility.Visible : Visibility.Collapsed;
		if (!level.HasValue || label == null)
		{
			return;
		}
		if (card.Tag is Border iconHost)
		{
			iconHost.Child = string.IsNullOrEmpty(iconPath)
				? IslandIcons.Battery(level.Value, charging, IconBrush(), MutedBrush, 16)
				: IslandIcons.PathIcon(iconPath, IconBrush(), 16, fill: false);
		}
		string text = string.IsNullOrWhiteSpace(deviceLabel) ? "" : deviceLabel + "  ";
		text += Math.Round(level.Value) + "%";
		if (charging)
		{
			text += "（充电中）";
		}
		label.Text = text;
	}

	// 控制页电量区刷新：仅在有有效数据时显示对应卡片。
	private void RefreshBatteryRow()
	{
		try
		{
			BatteryCore.BatteryState st = BatteryCore.Current;
			FillBatteryCard(pcBatCard, pcBatText, st.PcLevel, st.PcCharging == true, "电脑", null);
			string mouseLabel = string.IsNullOrWhiteSpace(st.MouseName) || st.MouseName == "HID 设备" ? "鼠标" : "鼠标 · " + st.MouseName;
			FillBatteryCard(mouseBatCard, mouseBatText, st.MouseLevel, false, mouseLabel, MouseIconPath[0]);
			string kbLabel = string.IsNullOrWhiteSpace(st.KeyboardName) || st.KeyboardName == "HID 设备" ? "键盘" : "键盘 · " + st.KeyboardName;
			FillBatteryCard(kbBatCard, kbBatText, st.KeyboardLevel, false, kbLabel, KeyboardIconPath[0]);
		}
		catch
		{
		}
	}

	private Border SmallTile(string title, Func<Brush, FrameworkElement> iconFactory, out TextBlock status, out TextBlock name, MouseButtonEventHandler click, Action onArrow = null)
	{
		Border b = new Border { CornerRadius = new CornerRadius(14), Background = CardBrush(), Cursor = Cursors.Hand, Padding = new Thickness(12, 10, 12, 10) };
		Grid g = new Grid();
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		Grid row = new Grid();
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		Grid.SetRow(row, 0);
		Grid iconHost = new Grid();
		iconHost.Children.Add(iconFactory(IconBrush()));
		row.Children.Add(iconHost);
		name = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
		Grid.SetColumn(name, 1);
		row.Children.Add(name);
		if (onArrow != null)
		{
			// 右侧小箭头：点磁贴本体=开/关，点箭头=进二级页面（网络列表/蓝牙设备）。
			// 无背景底色，悬停只提亮箭头本身；按下/抬起都拦截事件，防止误触到磁贴本体把 Wi-Fi/蓝牙关了。
			FrameworkElement chev = IslandIcons.Chevron(MutedBrush, 12);
			Border arrow = new Border
			{
				Width = 28,
				Height = 24,
				Background = Brushes.Transparent,
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Right,
				Cursor = Cursors.Hand,
				Child = chev
			};
			arrow.MouseEnter += delegate
			{
				if (chev is Viewbox vb && vb.Child is Path cp)
				{
					cp.Stroke = TextBrush;
				}
			};
			arrow.MouseLeave += delegate
			{
				if (chev is Viewbox vb && vb.Child is Path cp)
				{
					cp.Stroke = MutedBrush;
				}
			};
			arrow.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
			{
				e.Handled = true;
			};
			arrow.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
			{
				e.Handled = true;
				try
				{
					onArrow();
				}
				catch
				{
				}
			};
			Grid.SetColumn(arrow, 2);
			row.Children.Add(arrow);
		}
		g.Children.Add(row);
		status = new TextBlock { FontSize = 11, Foreground = MutedBrush, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
		Grid.SetRow(status, 1);
		g.Children.Add(status);
		b.Child = g;
		b.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
		{
			// 防误触：箭头专属区（磁贴右侧 48px 内）的点击一律不触发开关，只交给箭头处理
			if (onArrow != null)
			{
				Point p = e.GetPosition(b);
				if (p.X >= Math.Max(0, b.ActualWidth - 48))
				{
					e.Handled = true;
					return;
				}
			}
			click(s, e);
		};
		return b;
	}

	private Grid BuildWifiPage()
	{
		Grid g = new Grid();
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		StackPanel head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6) };
		head.Children.Add(TextButton("刷新", delegate { LoadNetworks(true); }));
		Grid.SetRow(head, 0);
		g.Children.Add(head);
		ScrollViewer sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 320 };
		networkList = new StackPanel();
		sv.Content = networkList;
		Grid.SetRow(sv, 1);
		g.Children.Add(sv);
		return g;
	}

	private Grid BuildBtPage()
	{
		Grid g = new Grid();
		g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		StackPanel head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6) };
		head.Children.Add(TextButton("刷新", delegate { LoadBtDevices(true); }));
		Grid.SetRow(head, 0);
		g.Children.Add(head);
		ScrollViewer sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 320 };
		btDeviceList = new StackPanel();
		sv.Content = btDeviceList;
		Grid.SetRow(sv, 1);
		g.Children.Add(sv);
		return g;
	}

	// —�?小部件构�?—�?
	private Brush ChipBrush()
	{
		return new SolidColorBrush(textLight ? Color.FromArgb(26, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255));
	}

// 覆盖基类文字画刷：不再跟随主�?LightTheme，而是跟随“实际背景亮度”自适应对比度�?
private new Brush TextBrush => IslandChrome.TextBrush(textLight);

	private new Brush MutedBrush => IslandChrome.MutedTextBrush(textLight);

	private Brush CardBrush()
	{
		return new SolidColorBrush(textLight ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255));
	}

	// 输入框专用：背景与文字保持强对比——浅色面板用白底深字，深色面板用深底亮字。
	private Brush InputBrush()
	{
		return new SolidColorBrush(textLight ? Color.FromArgb(125, 255, 255, 255) : Color.FromArgb(95, 0, 0, 0));
	}

	private Brush InputBorderBrush()
	{
		return new SolidColorBrush(textLight ? Color.FromArgb(90, 0, 0, 0) : Color.FromArgb(90, 255, 255, 255));
	}

	// 圆角输入框工厂：自定义模板 = 圆角 Border 壳 + PART_ContentHost，无系统原生外观。
	private TextBox RoundTextBox(string tooltip)
	{
		var tb = new TextBox
		{
			Foreground = TextBrush,
			CaretBrush = TextBrush,
			BorderThickness = new Thickness(1),
			Background = InputBrush(),
			BorderBrush = InputBorderBrush(),
			ToolTip = tooltip
		};
		var template = new ControlTemplate(typeof(TextBox));
		var shell = new FrameworkElementFactory(typeof(Border));
		shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
		shell.SetValue(Border.BorderThicknessProperty, new Thickness(1));
		shell.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
		shell.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
		shell.SetValue(Border.PaddingProperty, new Thickness(9, 5, 9, 5));
		var host = new FrameworkElementFactory(typeof(ScrollViewer));
		host.Name = "PART_ContentHost";
		host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
		host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
		host.SetBinding(FrameworkElement.VerticalAlignmentProperty, new System.Windows.Data.Binding("VerticalContentAlignment") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
		shell.AppendChild(host);
		template.VisualTree = shell;
		tb.Template = template;
		return tb;
	}

	private Brush IconBrush()
	{
		return textLight ? (Brush)new SolidColorBrush(Color.FromRgb(40, 44, 52)) : (Brush)Brushes.White;
	}

// 页签区与主内容之间的分隔横线：极低透明度，深浅模式都有但都克制�?
private Brush LineBrush()
	{
		return new SolidColorBrush(textLight ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(32, 255, 255, 255));
	}

// 文字颜色跟随系统明暗主题：深色模式→白色文字，浅色模式→深色文字�?
// 用户已反馈“深色模式下文字还是黑色”即为旧逻辑（跟随岛�?Light 标记）所致；
// 岛内 Light 只控制底色，文字按系统主题走，保证深浅两种系统模式下都清晰�?
private void UpdateTextContrast()
	{
		try
		{
			textLight = C.Prefs.Light;
		}
		catch
		{
		}
	}

// 系统明暗切换时由 Hub 调用（即使岛不跟随系统主题，文字也跟随明暗）�?
public void RefreshTextTheme()
	{
		try
		{
			bool before = textLight;
			UpdateTextContrast();
			Foreground = TextBrush;
			if (textLight != before)
			{
				RecolorTree(ContentGrid);
			}
			RestyleStatic();
// 跳动条颜色由 RebuildCollapsed �?IconBrush 重建；明暗翻转时顺手重建收起条�?


if (textLight != before && mode == Mode.Collapsed)
			{
				RebuildCollapsed();
			}
			Store.Trace("ISLAND theme text light=" + textLight + " changed=" + (textLight != before));
		}
		catch
		{
		}
	}

// 对比度变化时整棵可视树重着色：只替换“旧主题画刷”颜色，保留低电量红点等语义色�?
private void RecolorTree(DependencyObject node)
	{
		try
		{
			if (node is TextBlock tb && tb.Foreground is SolidColorBrush tbSc)
			{
				Color c = tbSc.Color;
				if (c.A == 230 && Math.Abs(c.R - 15) < 12 && Math.Abs(c.G - 15) < 12 && Math.Abs(c.B - 20) < 12)
				{
					tb.Foreground = TextBrush;
				}
				else if (c.A == 235 && Math.Abs(c.R - 245) < 12 && Math.Abs(c.G - 245) < 12 && Math.Abs(c.B - 250) < 12)
				{
					tb.Foreground = TextBrush;
				}
				else if (c.A == 170 && Math.Abs(c.R - 60) < 16 && Math.Abs(c.G - 60) < 16 && Math.Abs(c.B - 70) < 16)
				{
					tb.Foreground = MutedBrush;
				}
				else if (c.A == 160 && Math.Abs(c.R - 190) < 16 && Math.Abs(c.G - 195) < 16 && Math.Abs(c.B - 210) < 16)
				{
					tb.Foreground = MutedBrush;
				}
			}
			else if (node is System.Windows.Shapes.Shape shape)
			{
				if (shape.Fill is SolidColorBrush f)
				{
					Color c = f.Color;
					if (c.A == 255 && ((Math.Abs(c.R - 40) < 10 && Math.Abs(c.G - 44) < 10 && Math.Abs(c.B - 52) < 10) || (c.R > 250 && c.G > 250 && c.B > 250)))
					{
						shape.Fill = IconBrush();
					}
				}
				if (shape.Stroke is SolidColorBrush s)
				{
					Color c = s.Color;
					if (c.A == 255 && ((Math.Abs(c.R - 40) < 10 && Math.Abs(c.G - 44) < 10 && Math.Abs(c.B - 52) < 10) || (c.R > 250 && c.G > 250 && c.B > 250)))
					{
						shape.Stroke = IconBrush();
					}
				}
			}
			int count = VisualTreeHelper.GetChildrenCount(node);
			for (int i = 0; i < count; i++)
			{
				RecolorTree(VisualTreeHelper.GetChild(node, i));
			}
		}
		catch
		{
		}
	}

	private Border IconButton(FrameworkElement icon, Action click)
	{
		Border b = new Border
		{
			Width = 34,
			Height = 34,
			CornerRadius = new CornerRadius(17),
			Background = ChipBrush(),
			Cursor = Cursors.Hand,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid g = new Grid();
		icon.VerticalAlignment = VerticalAlignment.Center;
		icon.HorizontalAlignment = HorizontalAlignment.Center;
		g.Children.Add(icon);
		b.Child = g;
		if (click != null)
		{
			b.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) => click();
		}
		return b;
	}

	private Border TextButton(string label, Action click)
	{
		Border b = new Border
		{
			CornerRadius = new CornerRadius(8),
			Background = ChipBrush(),
			Cursor = Cursors.Hand,
			Margin = new Thickness(4, 0, 0, 0)
		};
		b.Child = new TextBlock { Text = label, FontSize = 12, Foreground = TextBrush, Margin = new Thickness(10, 4, 10, 4) };
		b.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) => click();
		return b;
	}

	private Border TabButton(string text, int page)
	{
		// 适中的内边距�? 个页签完整显示（不裁切），又尽量占满标签栏宽度，
// 避免与右侧叉号之间留出大段空白�?

Border b = new Border { CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Padding = new Thickness(8, 5, 10, 5), Margin = new Thickness(0, 0, 1, 0), Tag = page };
// 页签按钮：Lucide 风格线条图标 + 文字（图标用 IconBrush 跟随明暗，文字用 TextBrush�?

// 深浅两种模式下都统一，不再出现黑色页签）�?

StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		sp.Children.Add(tabIcons[page](IconBrush()));
		TextBlock t = new TextBlock { Text = text, FontSize = 12.5, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
		sp.Children.Add(t);
		b.Child = sp;
		b.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) => SetPage(page);
		return b;
	}

	// —�?模式切换 —�?
	private void EnsureShown()
	{
		try
		{
// WPF 窗口首次显示必须调用 Show()；只设置 Visibility 不会让它出现�?


if (PresentationSource.FromVisual(this) == null)
			{
				Show();
			}
		}
		catch
		{
		}
	}

	public void ShowCollapsed()
	{
		sizeAnimating = false; // 自愈：新过渡开始先复位动画态，防止卡帧
		toastTimer.Stop();
		mode = Mode.Collapsed;
		Store.Trace("ISLAND anim showcollapsed");
		SetNoActivate(true);
// 三态互斥：收起态只显示收起条，事件卡与展开面板必须隐藏�?

// 否则多个根节点同时可见会全部重叠在一起�?

expandedRoot.Visibility = Visibility.Collapsed;
		toastCard.Visibility = Visibility.Collapsed;
		collapsedRoot.Visibility = Visibility.Visible;
		EnsureShown();
		Visibility = Visibility.Visible;
		progressTimer.Start();
		fullscreenTimer.Start();
		RebuildCollapsed();
		PositionAnimated(O.CollapsedWidth, O.CollapsedHeight);
		RefreshClip();
	}

	public void ShowToast(string app, string title, string body, string aumid = null)
	{
		if (mode == Mode.Expanded)
		{
			return; // 展开面板打开时事件不强行关闭面板
		}
		sizeAnimating = false; // 自愈：新过渡开始先复位动画态，防止卡帧
		bool hideBody = O.HideNotifyBody || IslandClipboard.PrivacyActive;
		StopEqualizer();
		toastIcon.Background = ChipBrush();
		ImageSource appIcon = AppIconResolver.Resolve(aumid, app);
		if (appIcon != null)
		{
			toastIcon.Child = new Image { Source = appIcon, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		}
		else
		{
			toastIconText.Text = FirstGlyph(app);
			toastIcon.Child = toastIconText;
		}
		toastApp.Text = app.Length > 0 ? app : "新通知";
		toastApp.Visibility = app.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		toastTitle.Text = title.Length > 0 ? title : "有新通知";
		toastTitle.Visibility = (title.Length > 0 || app.Length == 0) ? Visibility.Visible : Visibility.Collapsed;
		toastBody.Text = body;
		toastBody.Visibility = (!hideBody && body.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
		toastTitle.FontWeight = FontWeights.SemiBold;
		toastFilterKey = string.IsNullOrEmpty(aumid) ? "name:" + (string.IsNullOrEmpty(app) ? "其他" : app) : aumid.ToLowerInvariant();
		if (toastViewBtn != null)
		{
			toastViewBtn.Visibility = Visibility.Visible; // 点击“查看”→ 跳转该应用的筛选页
		}
		toastCard.Visibility = Visibility.Visible;
		FadeInContent(toastCard);
		// 事件卡独占显示：收起条与展开面板隐藏，避免半透明卡片下透出文字重叠
		collapsedRoot.Visibility = Visibility.Collapsed;
		expandedRoot.Visibility = Visibility.Collapsed;
		mode = Mode.Toast;
		SetNoActivate(true);
// 事件卡就是小岛本身“变身”：尺寸与收起条完全一致，不放大成独立卡片�?

double w = O.CollapsedWidth;
		double h = O.CollapsedHeight;
		PositionAnimated(w, h);
		toastTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, O.NotifyDurationSec));
		toastTimer.Stop();
		toastTimer.Start();
	}

	private static string FirstGlyph(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "?";
		}
		int i = 0;
		if (char.IsHighSurrogate(text[0]) && text.Length > 1 && char.IsLowSurrogate(text[1]))
		{
			i = 1;
		}
		return text.Substring(0, i + 1);
	}

// 内容淡入 + 轻微下滑：配合窗口尺寸动画，展开/事件卡出现不“一下弹出”�?
private void FadeInContent(FrameworkElement el)
	{
		if (!O.AnimationEnabled || !IsVisible)
		{
			el.BeginAnimation(OpacityProperty, null);
			el.Opacity = 1.0;
			el.RenderTransform = null;
			return;
		}
// 纯透明度淡入：窗口矩形动画已经提供位移动感，内容再叠加位移会互相打架，
// 每帧整块内容相对窗口边缘移动，视觉上反而发“肉”不丝滑
		double dur = Math.Max(200.0, Math.Min(420.0, 300.0 / Math.Max(0.5, O.ExpandSpeed)));
		el.BeginAnimation(OpacityProperty, null);
		el.Opacity = 0.0;
		el.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(dur))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
		});
	}

	public void ShowBatteryToast(string title, string body)
	{
		sizeAnimating = false; // 自愈：新过渡开始先复位动画态，防止卡帧
		StopEqualizer();
		toastIconText.Text = "🔋";
		toastIcon.Child = toastIconText;
		toastIcon.Background = ChipBrush();
		if (toastViewBtn != null)
		{
toastViewBtn.Visibility = Visibility.Collapsed; // 低电量卡无“查看”目�?

}
		toastApp.Visibility = Visibility.Collapsed;
		toastTitle.Text = title;
		toastTitle.Visibility = Visibility.Visible;
		toastTitle.FontWeight = FontWeights.SemiBold;
		toastBody.Text = body;
		toastBody.Visibility = Visibility.Visible;
		toastCard.Visibility = Visibility.Visible;
		FadeInContent(toastCard);
		collapsedRoot.Visibility = Visibility.Collapsed;
		expandedRoot.Visibility = Visibility.Collapsed;
		mode = Mode.Toast;
		SetNoActivate(true);
		double w = O.CollapsedWidth;
		double h = O.CollapsedHeight;
		PositionAnimated(w, h);
		toastTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, O.NotifyDurationSec));
		toastTimer.Stop();
		toastTimer.Start();
	}

	public void Expand()
	{
		Expand(-1);
	}

	public void Expand(int page)
	{
		sizeAnimating = false; // 自愈：新过渡开始先复位动画态，防止卡帧
		toastTimer.Stop();
		StopEqualizer();
		mode = Mode.Expanded;
		Store.Trace("ISLAND anim expand");
		// 先切到目标页再测尺寸：展开区域按目标页面的实际宽高截取，底图与面板 1:1 对应。
		SetPage(page >= 0 ? page : PickDefaultPage(), false);
		// 收起期间底图若被基类管线重捕过（窗口关闭/最小化/壁纸广播），展开底图已过期：
		// 面板显示前把展开区域重新截一遍，避免"过期残留/收起条小图拉伸"形成的脏层随展开放大。
		if (expandedBackdropStale)
		{
			RefreshExpandedBackdrop();
		}
		SetNoActivate(false);
		EnsureShown();
		collapsedRoot.Visibility = Visibility.Collapsed;
		toastCard.Visibility = Visibility.Collapsed;
		expandedRoot.Visibility = Visibility.Visible;
		if (bellDot != null)
		{
			bellDot.Visibility = Visibility.Collapsed;
		}
		RefreshMusicPage();
		RefreshControlQuick();
// 展开高度按当前页面内容自适应（SetPage 已先切换到目标页�?

expandEntrance = true;
		SyncExpandedSize();
		expandEntrance = false;
		suppressDeactivate = true;
		Activate();
		suppressDeactivate = false;
	}

	public void Collapse()
	{
		if (mode != Mode.Expanded)
		{
			return;
		}
		sizeAnimating = false; // 自愈：新过渡开始先复位动画态，防止卡帧
		mode = Mode.Collapsed;
		EnsureControlSyncTimer(); // 收起即停同步定时器
		Store.Trace("ISLAND anim collapse");
		SetNoActivate(true);
		toastCard.Visibility = Visibility.Collapsed;
		// 收起时保留展开面板可见：窗口向上收缩，当前分页像幕布一样从底部被裁掉，
		// 收缩完成后再切换成收起条（FinishCollapse）——不再出现“下面一大片空白”。
		if (Math.Abs(ActualWidth - O.CollapsedWidth) < 1.0 && Math.Abs(ActualHeight - O.CollapsedHeight) < 1.0)
		{
			FinishCollapse();
			return;
		}
		PositionAnimated(O.CollapsedWidth, O.CollapsedHeight, FinishCollapse);
	}

	private void FinishCollapse()
	{
		if (mode != Mode.Collapsed)
		{
			return;
		}
		expandedRoot.Visibility = Visibility.Collapsed;
		collapsedRoot.Visibility = Visibility.Visible;
		RebuildCollapsed();
		FadeInContent(collapsedRoot);
		RefreshClip();
	}

	private void CollapseFromToast()
	{
		if (mode != Mode.Toast)
		{
			return;
		}
		mode = Mode.Collapsed;
		toastTimer.Stop();
		toastCard.Visibility = Visibility.Collapsed;
		expandedRoot.Visibility = Visibility.Collapsed;
		collapsedRoot.Visibility = Visibility.Visible;
		FadeInContent(collapsedRoot);
		PositionAnimated(O.CollapsedWidth, O.CollapsedHeight);
		RebuildCollapsed();
		RefreshClip();
	}

	public void HideForFullscreen()
	{
		if (mode == Mode.Hidden)
		{
			return;
		}
		Store.Trace("ISLAND hidden fullscreen=1 prevMode=" + mode);
		StopEqualizer();
		modeBeforeHide = mode;
		mode = Mode.Hidden;
		EnsureControlSyncTimer(); // 隐藏即停同步定时器
		ExternalHideAllowed = true; // 主动隐藏通行证：显示桌面拦截按主动 Hide 放行
		Visibility = Visibility.Hidden;
		toastTimer.Stop();
	}

	public void RestoreFromFullscreen()
	{
		if (mode != Mode.Hidden || modeBeforeHide == Mode.Hidden)
		{
			return;
		}
		Store.Trace("ISLAND restored fullscreen=0");
		ShowCollapsed();
	}

// 打开对应 APP 后，把该应用的“已看”通知从列表清掉（前台连续两轮 tick�? 秒才算“打开”）�?
private string lastFgProc;
	private int lastFgTicks;

	private void CheckForegroundCleanup()
	{
		try
		{
			IntPtr fg = GetForegroundWindow();
			if (fg == IntPtr.Zero)
			{
				lastFgTicks = 0;
				return;
			}
			uint pid;
			GetWindowThreadProcessId(fg, out pid);
			string proc = "";
			try
			{
				proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
			}
			catch
			{
			}
			if (proc.Length == 0)
			{
				lastFgTicks = 0;
				return;
			}
			if (proc.Equals(lastFgProc, StringComparison.OrdinalIgnoreCase))
			{
				lastFgTicks++;
			}
			else
			{
				lastFgProc = proc;
				lastFgTicks = 1;
			}
			if (lastFgTicks < 2)
			{
				return;
			}
			if (proc.Equals("GlassBoard", StringComparison.OrdinalIgnoreCase))
			{
				return; // 灵动�?设置窗口自己
			}
			string fgProc = proc.ToLowerInvariant();
			List<string> removedKeys = new List<string>();
			int removed = IslandNotifications.RemoveMatching(delegate (IslandNotification n)
			{
				if (MatchProc(n, fgProc))
				{
					removedKeys.Add(KeyFor(n));
					return true;
				}
				return false;
			});
			lastFgTicks = 0;
			if (removed > 0)
			{
				Store.Trace("ISLAND notif cleared-by-foreground count=" + removed);
				RefreshNotificationsPage();
				if (toastFilterKey != null && removedKeys.Contains(toastFilterKey) && mode == Mode.Toast)
				{
					toastTimer.Stop();
					CollapseFromToast();
				}
				if (IslandNotifications.Recent().Count == 0)
				{
					bellDot.Visibility = Visibility.Collapsed;
				}
			}
		}
		catch
		{
		}
	}

	private static bool MatchProc(IslandNotification n, string fgProc)
	{
		string aumid = (n.Aumid ?? "").ToLowerInvariant();
		if (aumid.StartsWith("popup:") && aumid.Substring(6) == fgProc)
		{
			return true;
		}
		string app = (n.AppName ?? "").Trim();
		string appLower = app.ToLowerInvariant();
		if (appLower.Length > 0)
		{
			if (appLower == fgProc)
			{
				return true;
			}
			if (appLower.Contains(fgProc) || fgProc.Contains(appLower))
			{
				return true;
			}
			string alias = FgAlias(fgProc);
			if (alias != null && appLower.Contains(alias))
			{
				return true;
			}
		}
		if (aumid.Length > 0 && aumid.Contains(fgProc))
		{
			return true;
		}
		return false;
	}

	private static string FgAlias(string proc)
	{
		if (proc == "weixin" || proc == "wechat")
		{
			return "微信";
		}
		if (proc == "qq")
		{
			return "qq";
		}
		if (proc == "dingtalk")
		{
			return "钉钉";
		}
		if (proc == "feishu" || proc == "lark")
		{
			return "飞书";
		}
		if (proc == "cloudmusic")
		{
			return "网易云音乐";
		}
		if (proc == "wxwork" || proc == "wework")
		{
			return "企业微信";
		}
		if (proc == "telegram" || proc == "discord" || proc == "slack" || proc == "whatsapp")
		{
			return proc;
		}
		return null;
	}

	private void SetNoActivate(bool value)
	{
		try
		{
			IntPtr hwnd = new WindowInteropHelper(this).Handle;
			long style = GetWindowLongW(hwnd, GWL_EXSTYLE);
			style |= WS_EX_TOOLWINDOW;
			if (value)
			{
				style |= WS_EX_NOACTIVATE;
			}
			else
			{
				style &= ~WS_EX_NOACTIVATE;
			}
			SetWindowLongW(hwnd, GWL_EXSTYLE, (int)style);
			// 注意：这里绝不能传 HWND_TOPMOST(-1)——会把岛塞进置顶层且绕过 WPF 的
			// Topmost 属性（ApplyTopmost 检测不到，置顶位永远不恢复，灵动岛就一直
			// 浮在窗口最上方）。用 HWND_TOP(0) 只重排 Z，置顶与否交给 ApplyTopmost。
			SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
			ApplyTopmost();
		}
		catch
		{
		}
	}

	private int PickDefaultPage()
	{
		if (O.ShowMusicPage)
		{
			return 0;
		}
		if (O.ShowNotificationsPage)
		{
			return 1;
		}
		if (O.ShowControlPage)
		{
			return 3;
		}
		return 0;
	}

	// 页面: 0=音乐 1=通知 2=剪贴�?3=控制中心 10=Wi-Fi 11=蓝牙
	// syncSize：用户点页签/磁贴切换时立即按新页内容重测面板高度（瞬切，不播动画）；
// 展开流程内部�?false，由 Expand 末尾统一做展开过渡�?
private void SetPage(int page, bool syncSize = true)
	{
		currentPage = page;
		bool sub = page >= 10;
		if (!sub)
		{
			subBack = 3; // 回到主页面后，二级页面返回键默认回控制中心
		}
		pageHeading.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
		backBtn.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
		pageHeading.Text = page switch
		{
			10 => "网络列表",
			11 => "蓝牙设备",
			12 => noteHeading,
			13 => "查看随手记",
			_ => ""
		};
		Grid target = page switch
		{
			0 => musicPage,
			1 => notificationsPage,
			2 => clipboardPage,
			4 => notesPage,
			10 => wifiPage,
			11 => btPage,
			12 => noteEditPage,
			13 => noteViewPage,
			_ => controlPage
		};
		// 页切换直接瞬切（用户要求切页不要动画），切换后按新页面内容重测展开高度
		foreach (Grid p in new[] { musicPage, notificationsPage, clipboardPage, controlPage, wifiPage, btPage, notesPage, noteEditPage, noteViewPage })
		{
			p.Visibility = Visibility.Collapsed;
		}
		target.Visibility = Visibility.Visible;
// 主页面顶部只�?8px（原来是 26px，与 pageHost �?6px 叠加后和页签区隔�?

// 一大段空白）；二级页面（网�?蓝牙）需要给 pageHeading/backBtn 留位置，维持 26px�?

target.Margin = sub ? new Thickness(0, 36, 0, 0) : new Thickness(0, 8, 0, 0);
// 页签高亮：只改背景芯片，不换字体权重、不重建图标——权重切换会让文字变宽�?

// 图标重建会整行重绘，那一下瞬时位�?重绘读起来就像“标题有动画”�?

foreach (Border b in tabBar.Children.OfType<Border>())
		{
			int idx = (int)b.Tag;
			bool active = idx == page || (sub && idx == 3);
			b.Background = active ? ChipBrush() : Brushes.Transparent;
		}
		if (page == 1)
		{
			RefreshNotificationsPage();
		}
		if (page == 2)
		{
			RefreshClipboardPage();
		}
		if (page == 4)
		{
			RefreshNotesPage();
		}
		if (page == 3)
		{
			RefreshControlQuick();
		}
		if (page == 10)
		{
			LoadNetworks(false);
		}
		if (page == 11)
		{
			LoadBtDevices(false);
		}
// 展开高度随页面内容自适应（用户点击切页时平滑过渡；Expand 内部�?syncSize=false�?

// 由展开主过渡统一负责尺寸动画，避免双重动画）
		if (syncSize)
		{
			SyncExpandedSize();
		}
		EnsureControlSyncTimer();
	}

	// —�?页面数据刷新 —�?
	public void RefreshMusicPage(bool animate = false)
	{
		if (M == null)
		{
			// 引擎销毁：音乐页回到占位态，避免残留旧曲目与可用按钮
			if (musicTitle != null)
			{
				musicTitle.Text = "未在播放";
				musicTitle.Foreground = TextBrush;
			}
			if (musicArtist != null)
			{
				musicArtist.Text = "打开音乐应用后这里会显示曲目";
				musicArtist.Foreground = MutedBrush;
			}
			if (musicStatus != null)
			{
				musicStatus.Text = "";
				musicStatus.Foreground = MutedBrush;
			}
			if (bigCover != null)
			{
				bigCover.Child = IslandIcons.MusicPlaceholder(TextBrush, ChipBrush(), 96, 16);
			}
			if (musicProgressRow != null)
			{
				musicProgressRow.Visibility = Visibility.Collapsed;
			}
			if (musicButtonsRow != null)
			{
				musicButtonsRow.Visibility = Visibility.Collapsed;
			}
			return;
		}
		MediaSnapshot s = M.Session;
		bool has = s.Available || (M.Local && M.Playing) || !string.IsNullOrEmpty(M.Title);
		string newTitle = has && !string.IsNullOrEmpty(M.Title) ? M.Title : "未在播放";
		string newArtist = (has && !string.IsNullOrEmpty(M.Artist) ? M.Artist : "打开音乐应用后这里会显示曲目") + (has && !string.IsNullOrEmpty(M.Album) && M.Album != M.Artist ? " · " + M.Album : "");
		// 切歌时曲名/歌手/封面做一次柔和淡入淡出，其余状态（进度、按钮）直接更新
		if (animate && !string.Equals(musicTitle.Text, newTitle))
		{
			AnimateTextSwap(musicTitle, delegate { musicTitle.Text = newTitle; musicTitle.Foreground = TextBrush; });
		}
		else
		{
			musicTitle.Text = newTitle;
			musicTitle.Foreground = TextBrush;
		}
		if (animate && !string.Equals(musicArtist.Text, newArtist))
		{
			AnimateTextSwap(musicArtist, delegate { musicArtist.Text = newArtist; musicArtist.Foreground = MutedBrush; });
		}
		else
		{
			musicArtist.Text = newArtist;
			musicArtist.Foreground = MutedBrush;
		}
		musicStatus.Text = has ? (M.Playing ? "播放中" : "已暂停") + SourceSuffix(s) : "";
		musicStatus.Foreground = MutedBrush;
		// 封面
		FrameworkElement newCover = M.Artwork == null
			? IslandIcons.MusicPlaceholder(TextBrush, ChipBrush(), 96, 16)
			: (FrameworkElement)new Image { Source = M.Artwork, Stretch = Stretch.UniformToFill, Width = 96, Height = 96 };
		if (animate && bigCover.Child != newCover)
		{
			AnimateTextSwap(bigCover, delegate { bigCover.Child = newCover; });
		}
		else
		{
			bigCover.Child = newCover;
		}
		// 无曲目时隐藏进度行与控制键，只留居中占位信息
		if (musicProgressRow != null)
		{
			musicProgressRow.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
		}
		if (musicButtonsRow != null)
		{
			musicButtonsRow.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
		}
		// 控制能力
		bool available = s.Available;
		bool localPlaying = M.Local && M.Playing;
		bool canAny = available || localPlaying;
		SetBtnEnabled(prevBtn, available && s.CanPrevious);
		SetBtnEnabled(playBtn, canAny && (available ? s.CanToggle : true));
		SetBtnEnabled(nextBtn, available && s.CanNext);
		(playBtn.Child as Grid).Children.Clear();
		(playBtn.Child as Grid).Children.Add(M.Playing ? IslandIcons.Pause(PlayIconBrush(), 20) : IslandIcons.Play(PlayIconBrush(), 20));
		// 进度
		double duration = M.Duration > 0 ? M.Duration : s.Duration;
		double position = M.Position >= 0 ? M.Position : s.Position;
		bool seekable = available && (M.CanSeek || s.CanSeek);
		// 时长不可用时完全隐藏进度行，避免出现"坏掉的控�?
		if (musicProgressRow != null)
		{
			musicProgressRow.Visibility = duration > 0 ? Visibility.Visible : Visibility.Collapsed;
		}
		progressSlider.IsEnabled = seekable && duration > 0;
		if (!draggingProgress)
		{
			progressSlider.Minimum = 0;
			progressSlider.Maximum = Math.Max(1, duration);
			progressSlider.Value = Math.Min(Math.Max(0, position), progressSlider.Maximum);
		}
		positionText.Text = FormatTime(position);
		durationText.Text = duration > 0 ? FormatTime(duration) : "--:--";
		positionText.Foreground = MutedBrush;
		durationText.Foreground = MutedBrush;
		lastMusicTick = DateTime.Now;
	}

	private static void SetBtnEnabled(Border b, bool enabled)
	{
		b.Opacity = enabled ? 1.0 : 0.35;
		b.IsHitTestVisible = enabled;
	}

	// 切歌时文字/封面的柔和替换：快速淡出→替换内容→淡入。动画关闭或不可见时直接替换。
	private void AnimateTextSwap(FrameworkElement el, Action apply)
	{
		if (el == null)
		{
			apply();
			return;
		}
		if (!O.AnimationEnabled || !IsVisible || mode == Mode.Hidden)
		{
			apply();
			return;
		}
		double speed = Math.Max(0.5, O.ExpandSpeed);
		DoubleAnimation fadeOut = new DoubleAnimation(0.3, TimeSpan.FromMilliseconds(110.0 / speed));
		fadeOut.Completed += delegate
		{
			apply();
			DoubleAnimation fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200.0 / speed))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			el.BeginAnimation(OpacityProperty, fadeIn);
		};
		el.BeginAnimation(OpacityProperty, fadeOut);
	}

	private static string FormatTime(double seconds)
	{
		if (double.IsNaN(seconds) || seconds < 0)
		{
			return "0:00";
		}
		long total = (long)Math.Floor(seconds);
		long m = total / 60;
		long sec = total % 60;
		return m + ":" + sec.ToString("00");
	}

	private static string SourceSuffix(MediaSnapshot s)
	{
		string src = MusicPlatforms.FriendlyName(s.Source);
		return string.IsNullOrWhiteSpace(src) ? "" : " · " + src;
	}

	private void UpdateProgressTick()
	{
		if (sizeAnimating)
		{
			return; // 过渡动画期间跳过进度/歌词刷新：文本重排会抢主过渡的渲染帧
		}
// 收起态中部歌词行：按当前播放位置推进。播放器发布进度时用真实位置�?

// 不发布进度（如网易云桌面版）时用本曲开始播放至今的时长估算（可能轻微漂移）�?

// 位置不可知（播放器内部跳转）时只显示歌手名，绝不显示错位歌词�?

if (M != null && M.Playing && collapsedSub != null && mode == Mode.Collapsed)
		{
			string sub;
			if (M.Duration > 0)
			{
				// 播放器发布时间线：位置可信。采�?桥接往返约滞后 0.3~0.8s
// （这就是“歌词比音乐慢半秒”的来源），给歌词时钟加 0.5s 提前量补偿�?



lyricUntrusted = false;
				IslandLyrics.Update(M.Position + 0.5);
			}
			else if (!lyricUntrusted)
			{
// 无时间线：本机秒表计时，时间精确，不加提前量�?



IslandLyrics.Update(lyricStartUtc == DateTime.MinValue ? 0.0 : (DateTime.Now - lyricStartUtc).TotalSeconds);
			}
			if (lyricUntrusted && M.Duration <= 0)
			{
				sub = string.IsNullOrEmpty(M.Artist) ? "" : M.Artist;
			}
			else
			{
				sub = IslandLyrics.ActiveText;
				if (sub.Length == 0)
				{
					sub = string.IsNullOrEmpty(M.Artist) ? "" : M.Artist;
				}
			}
			if (collapsedSub.Text != sub)
			{
				collapsedSub.Text = sub;
			}
		}
// 无音乐时的默认收起布局：时�?日期每秒、屏幕刷新率�?2 秒刷新一次�?

if (collapsedTime != null && mode == Mode.Collapsed)
		{
			DateTime now = DateTime.Now;
			if ((now - lastClockTick).TotalSeconds >= 1.0)
			{
				lastClockTick = now;
				try
				{
					collapsedTime.Text = now.ToString("HH:mm");
					collapsedDate.Text = now.ToString("M月d日 ddd");
				}
				catch
				{
				}
			}
			if ((now - lastSuggestionAt).TotalSeconds >= 8.0)
			{
				lastSuggestionAt = now;
				if (suggestionPool.Length == 0 || (now - lastSuggestionPoolAt).TotalSeconds >= 60.0)
				{
					suggestionPool = IslandSuggestions.Build(SafeWeather(), CurrentRefreshRate(), SafeNotes(), SafeUnread());
					lastSuggestionPoolAt = now;
					suggestionIdx = 0;
				}
				if (refreshText != null && suggestionPool.Length > 0)
				{
					suggestionIdx = (suggestionIdx + 1) % suggestionPool.Length;
					string next = suggestionPool[suggestionIdx];
					if (!suggestTraced)
					{
						suggestTraced = true;
						Store.Trace("ISLAND suggest rotating n=" + suggestionPool.Length + " unread=" + SafeUnread());
					}
					AnimateTextSwap(refreshText, () => refreshText.Text = next);
				}
			}
		}
		if (mode != Mode.Expanded || currentPage != 0 || draggingProgress || M == null)
		{
			return;
		}
		MediaSnapshot s = M.Session;
		if (!s.Available)
		{
			return;
		}
// M.Position 已由 MusicEngine 按媒体会话时间线外推（Session.Age + 本地流逝时间）�?

// 这里不得再加本地 elapsed——旧实现双重外推导致进度超前、撞到时长后卡死�?

// 也就是“无法获取音乐播放进度”。未提供 Seek 能力的播放器同样照常显示进度�?

double position = M.Position;
		double duration = M.Duration > 0 ? M.Duration : s.Duration;
		if (duration > 0 && position > duration + 2)
		{
			position = duration;
		}
		if (musicProgressRow != null)
		{
			musicProgressRow.Visibility = duration > 0 ? Visibility.Visible : Visibility.Collapsed;
		}
		progressSlider.Maximum = Math.Max(1, duration);
		progressSlider.Value = Math.Min(Math.Max(0, position), progressSlider.Maximum);
		positionText.Text = FormatTime(position);
		durationText.Text = duration > 0 ? FormatTime(duration) : "--:--";
		lastMusicTick = DateTime.Now;
	}

	public void RefreshNotificationsPage()
	{
		notifList.Children.Clear();
		List<IslandNotification> items = IslandNotifications.Recent();
		if (items.Count == 0)
		{
			notifCats?.Children.Clear(); // 列表已空：分类计数必须一并清掉，避免“暂无通知”旁残留旧条数
			notifList.Children.Add(EmptyHint("暂无通知"));
			SyncExpandedSize();
			return;
		}
		bool hideBody = O.HideNotifyBody || IslandClipboard.PrivacyActive;
		Dictionary<string, List<IslandNotification>> groups = new Dictionary<string, List<IslandNotification>>();
		List<string> order = new List<string>();
		foreach (IslandNotification n in items.Take(30))
		{
			string key = KeyFor(n);
			if (!groups.ContainsKey(key))
			{
				groups[key] = new List<IslandNotification>();
				order.Add(key);
			}
			groups[key].Add(n);
		}
		if (notifFilter != null && !groups.ContainsKey(notifFilter))
		{
notifFilter = null; // 过滤键失效回退“全部�?

}
		if (notifCats != null)
		{
			notifCats.Children.Clear();
			notifCats.Children.Add(CategoryChip(null, "全部", items.Take(30).Count(), notifFilter == null));
			foreach (string key in order)
			{
				IslandNotification first = groups[key][0];
				string appName = string.IsNullOrEmpty(first.AppName) ? "新通知" : first.AppName;
				notifCats.Children.Add(CategoryChip(first, appName, groups[key].Count, notifFilter == key));
			}
		}
		List<IslandNotification> shown = notifFilter == null ? items.Take(30).ToList() : groups[notifFilter].Take(30).ToList();
		foreach (IslandNotification n in shown)
		{
			notifList.Children.Add(NotificationCard(n, hideBody));
		}
		SyncExpandedSize();
	}

	private static string KeyFor(IslandNotification n)
	{
		return string.IsNullOrEmpty(n.Aumid) ? "name:" + (string.IsNullOrEmpty(n.AppName) ? "其他" : n.AppName) : n.Aumid.ToLowerInvariant();
	}

	private Border CategoryChip(IslandNotification sample, string label, int count, bool selected)
	{
		Border chip = new Border
		{
			CornerRadius = new CornerRadius(Math.Min(12.0, Math.Max(4.0, O.CornerRadius))),
			Background = selected ? PlayButtonBrush() : ChipBrush(),
			Padding = new Thickness(8, 3, 8, 3),
			Margin = new Thickness(0, 0, 5, 0),
			Cursor = Cursors.Hand
		};
		StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };
		Brush fg = selected ? PlayIconBrush() : TextBrush;
		if (sample != null)
		{
			ImageSource ic = AppIconResolver.Resolve(sample.Aumid, label);
			if (ic != null)
			{
				sp.Children.Add(new Image { Source = ic, Width = 13, Height = 13, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
			}
			else
			{
				sp.Children.Add(new TextBlock { Text = FirstGlyph(label), FontSize = 10, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
			}
		}
		sp.Children.Add(new TextBlock { Text = count > 1 ? label + " " + count : label, FontSize = 11.5, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
		chip.Child = sp;
		chip.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			notifFilter = (selected || sample == null) ? null : KeyFor(sample);
			RefreshNotificationsPage();
		};
		return chip;
	}

	private FrameworkElement NotificationCard(IslandNotification n, bool hideBody)
	{
		Border card = new Border { CornerRadius = new CornerRadius(12), Background = CardBrush(), Padding = new Thickness(10, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6) };
		Grid g = new Grid();
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		Border chip = new Border
		{
			Width = 34,
			Height = 34,
			CornerRadius = new CornerRadius(9),
			Background = ChipBrush(),
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0, 0, 10, 0)
		};
		ImageSource appIcon = AppIconResolver.Resolve(n.Aumid, n.AppName);
		if (appIcon != null)
		{
			chip.Child = new Image { Source = appIcon, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		}
		else
		{
			chip.Child = new TextBlock
			{
				Text = FirstGlyph(string.IsNullOrEmpty(n.AppName) ? "新通知" : n.AppName),
				FontSize = 15,
				FontWeight = FontWeights.SemiBold,
				Foreground = TextBrush,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
		}
		Grid.SetColumn(chip, 0);
		g.Children.Add(chip);
		StackPanel stack = new StackPanel();
		Grid head = new Grid();
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		TextBlock app = new TextBlock { Text = string.IsNullOrEmpty(n.AppName) ? "新通知" : n.AppName, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis };
		Grid.SetColumn(app, 0);
		head.Children.Add(app);
		TextBlock time = new TextBlock { Text = RelativeTime(n.Stamp), FontSize = 10.5, Foreground = MutedBrush, Margin = new Thickness(8, 0, 0, 0) };
		Grid.SetColumn(time, 1);
		head.Children.Add(time);
		stack.Children.Add(head);
		if (!hideBody && !string.IsNullOrEmpty(n.Title))
		{
			stack.Children.Add(new TextBlock { Text = n.Title, FontSize = 13, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap, LineHeight = 17, MaxHeight = 34, Margin = new Thickness(0, 2, 0, 0) });
		}
		if (!hideBody && !string.IsNullOrEmpty(n.Body))
		{
			stack.Children.Add(new TextBlock { Text = n.Body, FontSize = 12, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, LineHeight = 16, MaxHeight = 32, Margin = new Thickness(0, 1, 0, 0) });
		}
		Grid.SetColumn(stack, 1);
		g.Children.Add(stack);
		card.Child = g;
		return card;
	}

	private static string RelativeTime(DateTime stamp)
	{
		double s = (DateTime.Now - stamp).TotalSeconds;
		if (s < 60)
		{
			return "刚刚";
		}
		if (s < 3600)
		{
			return ((int)(s / 60)) + " 分钟前";
		}
		if (s < 86400)
		{
			return ((int)(s / 3600)) + " 小时前";
		}
		return stamp.ToString("MM-dd HH:mm");
	}

	public void RefreshClipboardPage()
	{
		clipList.Children.Clear();
		List<IslandClipboard.ClipItem> items = IslandClipboard.Items();
		if (items.Count == 0)
		{
			clipList.Children.Add(EmptyHint("剪贴板历史为空（需在设置中开启）"));
			SyncExpandedSize();
			return;
		}
		bool hideBody = IslandClipboard.PrivacyActive;
		foreach (IslandClipboard.ClipItem item in items.Take(50))
		{
			Border card = new Border { CornerRadius = new CornerRadius(12), Background = CardBrush(), Padding = new Thickness(10, 6, 6, 6), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
			Grid g = new Grid();
			g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			int textCol = 0;
// 图片项：左侧放图片本身的缩略图（是内容不是图标）；文本项不显示任何前置图标�?


if (item.Kind == "image" && System.IO.File.Exists(item.ImagePath))
			{
				try
				{
					Image img = new Image { Width = 44, Height = 44, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center };
					System.Windows.Media.Imaging.BitmapImage bi = new System.Windows.Media.Imaging.BitmapImage();
					bi.BeginInit();
					bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
					bi.DecodePixelWidth = 88;
					bi.UriSource = new Uri(item.ImagePath);
					bi.EndInit();
					img.Source = bi;
					img.Clip = new RectangleGeometry(new Rect(0, 0, 44, 44), 8, 8);
					Grid.SetColumn(img, 0);
					g.Children.Add(img);
					textCol = 1;
				}
				catch
				{
				}
			}
			StackPanel textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
			if (hideBody)
			{
				textStack.Children.Add(new TextBlock { Text = "已隐藏预览（锁屏/隐私模式）", FontSize = 12, Foreground = MutedBrush });
			}
			else if (item.Kind == "text")
			{
				string preview = (item.Text ?? "").Replace("\r", " ").Replace("\n", " ");
				if (preview.Length > 120)
				{
					preview = preview.Substring(0, 120) + "…";
				}
				textStack.Children.Add(new TextBlock { Text = preview, FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
				textStack.Children.Add(new TextBlock { Text = item.Stamp.ToString("HH:mm:ss"), FontSize = 10.5, Foreground = MutedBrush });
			}
			else
			{
				textStack.Children.Add(new TextBlock { Text = "图片", FontSize = 12 });
				textStack.Children.Add(new TextBlock { Text = item.Stamp.ToString("HH:mm:ss"), FontSize = 10.5, Foreground = MutedBrush });
			}
			Grid.SetColumn(textStack, textCol);
			g.Children.Add(textStack);
			card.Child = g;
// click card body copies to clipboard; per-item copy button removed
card.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
			{
				DependencyObject src = e.OriginalSource as DependencyObject;
				while (src != null)
				{
					if (src is System.Windows.Controls.Button)
					{
						return;
					}
					src = VisualTreeHelper.GetParent(src);
				}
				if (IslandClipboard.WriteBack(item))
				{
					ShowClipToast();
				}
			};
			clipList.Children.Add(card);
		}
		SyncExpandedSize();
	}

	private FrameworkElement EmptyHint(string text)
	{
		return new TextBlock
		{
			Text = text,
			FontSize = 12,
			Foreground = MutedBrush,
			HorizontalAlignment = HorizontalAlignment.Center,
			Margin = new Thickness(0, 24, 0, 0)
		};
	}

	// —�?控制中心数据 —�?
	private ControlCenterBridge bridge;

	private ControlCenterBridge Bridge => bridge ?? (bridge = new ControlCenterBridge());

	public void RefreshControlQuick()
	{
		if (mode != Mode.Expanded)
		{
			return;
		}
		try
		{
			AudioCore.TryGetVolume(out float vol, out bool muted);
			volumeSyncing = true;
			// 修正音量刻度：TryGetVolume 返回 0-1，滑杆是 0-100，此前直接赋值导致滑杆永远显示 0-1、形同失效
			volumeSlider.Value = Math.Max(0, Math.Min(100, vol * 100.0));
			volumeSyncing = false;
			volumeMutedShown = muted;
			volumeValue.Text = muted ? "静音" : ((int)Math.Round(vol * 100.0)).ToString();
			volumeIconHost.Child = muted ? IslandIcons.Speaker(MutedBrush, 16) : IslandIcons.Speaker(TextBrush, 16);
		}
		catch
		{
		}
		try
		{
			int? brightness = BrightnessCore.TryGet();
			if (brightness.HasValue)
			{
				brightnessSyncing = true;
				brightnessSlider.Value = brightness.Value;
				brightnessSyncing = false;
				brightnessValue.Text = brightness.Value.ToString();
				brightnessSlider.IsEnabled = true;
			}
			else
			{
				brightnessSlider.IsEnabled = false;
				brightnessValue.Text = "--";
			}
		}
		catch
		{
			brightnessSlider.IsEnabled = false;
			brightnessValue.Text = "--";
		}
		try
		{
			List<AudioCore.AudioDevice> devices = AudioCore.Devices();
			AudioCore.AudioDevice selected = outputCombo.SelectedItem as AudioCore.AudioDevice;
			outputCombo.Items.Clear();
			foreach (AudioCore.AudioDevice d in devices)
			{
				outputCombo.Items.Add(d);
				if (d.IsDefault)
				{
					outputCombo.SelectedItem = d;
				}
			}
			if (outputCombo.SelectedItem == null && devices.Count > 0)
			{
				outputCombo.SelectedItem = devices[0];
			}
		}
		catch
		{
		}
		RefreshBatteryRow();
		RefreshRadioTiles();
	}

	private void RefreshRadioTiles()
	{
		Task.Run(delegate
		{
			try
			{
				CcReply reply = Bridge.State();
				bool wifi = false, bt = false;
				string v = reply.Value ?? "";
				foreach (string part in v.Split('|'))
				{
					if (part.StartsWith("wifi:", StringComparison.OrdinalIgnoreCase))
					{
						wifi = part.EndsWith("on", StringComparison.OrdinalIgnoreCase);
					}
					else if (part.StartsWith("bt:", StringComparison.OrdinalIgnoreCase))
					{
						bt = part.EndsWith("on", StringComparison.OrdinalIgnoreCase);
					}
				}
				string ssid = null;
				if (wifi)
				{
					try
					{
						ssid = WirelessCore.CurrentSsid();
					}
					catch
					{
					}
				}
				Dispatcher.BeginInvoke(new Action(delegate
				{
					// Wi-Fi 磁贴下方显示已连接的 Wi-Fi 名称（未连接时显示状态提示）
					wifiStatus.Text = !wifi ? "已关闭" : (string.IsNullOrEmpty(ssid) ? "已开启 · 未连接" : ssid);
					btStatus.Text = bt ? "已开启" : "已关闭";
					wifiTile.Opacity = wifi ? 1.0 : 0.65;
					btTile.Opacity = bt ? 1.0 : 0.65;
				}));
			}
			catch
			{
			}
		});
	}

	private void ToggleRadio(string kind)
	{
		Task.Run(delegate
		{
			try
			{
				// 先读当前状态再翻转：此前固定传 false 只能关闭、永远开不了。
				CcReply reply = Bridge.State();
				string v = reply.Value ?? "";
				bool current = false;
				foreach (string part in v.Split('|'))
				{
					if (part.StartsWith(kind + ":", StringComparison.OrdinalIgnoreCase) && part.EndsWith("on", StringComparison.OrdinalIgnoreCase))
					{
						current = true;
						break;
					}
				}
				Bridge.SetRadio(kind, !current);
				WirelessCore.InvalidateSsidCache();
				Store.Trace("ISLAND radio toggle kind=" + kind + " to=" + (!current ? "on" : "off"));
				if (!current && kind.Equals("wifi", StringComparison.OrdinalIgnoreCase))
				{
					// Wi-Fi 刚打开：网卡需要 1~2 秒就绪，稍等后主动扫描，
					// 网络列表自动刷新（此前打开 Wi-Fi 后列表不更新，需手动点刷新）。
					System.Threading.Thread.Sleep(1600);
					Dispatcher.BeginInvoke(new Action(delegate
					{
						LoadNetworks(true);
					}));
				}
			}
			catch
			{
			}
		});
		Dispatcher.BeginInvoke(new Action(delegate
		{
			RefreshRadioTiles();
		}), DispatcherPriority.ApplicationIdle);
	}

	// —— 系统同步 ——
	// 控制页可见时每 1.5s 轻量拉一次系统音量与 Wi-Fi/蓝牙开关状态：
	// 音量走 WASAPI GetMasterVolumeLevelScalar（微秒级），开关状态走常驻 PowerShell 管道（毫秒级），
	// 都极轻量且只在页面可见期间运行——在系统设置/托盘改音量、在别处开关 Wi-Fi，灵动岛即时跟上。
	private void EnsureControlSyncTimer()
	{
		bool want = mode == Mode.Expanded && currentPage == 3;
		if (want)
		{
			if (controlSyncTimer == null)
			{
				controlSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
				controlSyncTimer.Tick += delegate
				{
					try
					{
						RefreshVolumeAndRadio();
					}
					catch
					{
					}
				};
			}
			if (!controlSyncTimer.IsEnabled)
			{
				RefreshVolumeAndRadio();
				controlSyncTimer.Start();
			}
		}
		else if (controlSyncTimer != null && controlSyncTimer.IsEnabled)
		{
			controlSyncTimer.Stop();
		}
	}

	private void RefreshVolumeAndRadio()
	{
		if (volumeSlider == null || volumeValue == null)
		{
			return;
		}
		// 用户正拖动滑杆时不覆盖其操作
		if (volumeSlider.IsMouseCaptureWithin || volumeSlider.IsKeyboardFocusWithin)
		{
			return;
		}
		if (AudioCore.TryGetVolume(out float vol, out bool muted))
		{
			double v = Math.Round(vol * 100);
			if (Math.Abs(volumeSlider.Value - v) > 0.5)
			{
				volumeSyncing = true;
				volumeSlider.Value = v;
				volumeSyncing = false;
			}
			volumeValue.Text = muted ? "静音" : ((int)Math.Round(vol * 100)).ToString();
			if (muted != volumeMutedShown && volumeIconHost != null)
			{
				volumeMutedShown = muted;
				volumeIconHost.Child = muted ? IslandIcons.Speaker(MutedBrush, 16) : IslandIcons.Speaker(TextBrush, 16);
			}
		}
		RefreshRadioTiles();
	}

	private void LoadNetworks(bool force)
	{
		if (wifiScanning)
		{
			return;
		}
		wifiScanning = true;
		WirelessCore.InvalidateSsidCache();
		networkList.Children.Clear();
		networkList.Children.Add(EmptyHint("正在读取网络列表…"));
		Task.Run(delegate
		{
			List<WirelessCore.WifiNetwork> networks = null;
			HashSet<string> known = null;
			try
			{
				if (force)
				{
					WirelessCore.Scan();
				}
				networks = WirelessCore.Networks();
			}
			catch
			{
			}
			try
			{
				// 已保存配置的 SSID 集合：列表中标记"已保存"，点击免密直连
				known = WirelessCore.KnownProfiles();
			}
			catch
			{
			}
			Dispatcher.BeginInvoke(new Action(delegate
			{
				RunWhenIdle(delegate
				{
					networkList.Children.Clear();
					if (networks == null || networks.Count == 0)
					{
						networkList.Children.Add(EmptyHint("未找到可用网络"));
					}
					else
					{
						foreach (WirelessCore.WifiNetwork n in networks)
						{
							networkList.Children.Add(NetworkRow(n, known));
						}
					}
					wifiScanning = false;
					SyncExpandedSize();
				});
			}));
		});
	}

	private FrameworkElement NetworkRow(WirelessCore.WifiNetwork n, HashSet<string> known)
	{
		Border card = new Border { CornerRadius = new CornerRadius(12), Background = CardBrush(), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
		Grid g = new Grid();
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		StackPanel left = new StackPanel();
		left.Children.Add(new TextBlock { Text = n.Ssid, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
		bool saved = known != null && known.Contains(n.Ssid);
		TextBlock meta = new TextBlock { Text = (n.Connected ? "已连接" : saved ? "已保存" : n.Secured ? "需要密码" : "开放网络") + " · 信号 " + n.Signal + "%", FontSize = 11, Foreground = MutedBrush };
		left.Children.Add(meta);
		Grid.SetColumn(left, 0);
		g.Children.Add(left);
		// 信号条固定到右侧独立列：此前漏了 SetColumn，信号条落在第 0 列与 SSID 文字/状态重合
		FrameworkElement bars = SignalBars(n.Signal, TextBrush);
		bars.VerticalAlignment = VerticalAlignment.Center;
		bars.Margin = new Thickness(8, 0, 0, 0);
		Grid.SetColumn(bars, 1);
		g.Children.Add(bars);
		card.Child = g;
		card.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) => OnNetworkClick(n, card, saved);
		return card;
	}

	private void OnNetworkClick(WirelessCore.WifiNetwork n, Border card, bool saved)
	{
		if (n.Connected)
		{
			Task.Run(delegate { try { WirelessCore.Disconnect(); } catch { } });
			LoadNetworks(false);
			return;
		}
		if (n.Secured)
		{
			if (saved)
			{
				// 已保存的网络：系统配置里已有密码，直接按配置文件连接（免输密码）
				DoConnect(n, null, card);
				return;
			}
			// 内联密码输入（替换卡片内容）
			StackPanel p = new StackPanel();
			passwordBox = new PasswordBox { Height = 26, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) };
			p.Children.Add(new TextBlock { Text = "输入 " + n.Ssid + " 的密码：", FontSize = 12 });
			p.Children.Add(passwordBox);
			connectBtn = TextButton("连接", delegate { DoConnect(n, passwordBox.Password, card); });
			connectBtn.HorizontalAlignment = HorizontalAlignment.Left;
			p.Children.Add(connectBtn);
			card.Child = p;
			passwordBox.Focus();
			return;
		}
		DoConnect(n, "", card);
	}

	private void DoConnect(WirelessCore.WifiNetwork n, string password, Border card)
	{
		card.IsHitTestVisible = false;
		card.Child = EmptyHint("正在连接 " + n.Ssid + " …");
		Task.Run(delegate
		{
			string err = null;
			try
			{
				err = WirelessCore.Connect(n.Ssid, password ?? "");
			}
			catch (Exception ex)
			{
				err = ex.Message;
			}
			Dispatcher.BeginInvoke(new Action(delegate
			{
				if (err == null)
				{
					LoadNetworks(false);
				}
				else
				{
					card.IsHitTestVisible = true;
					card.Child = EmptyHint("连接失败，请检查密码后重试");
				}
			}));
		});
	}

	private static FrameworkElement SignalBars(int signal, Brush brush)
	{
		StackPanel bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		int filled = Math.Max(0, Math.Min(4, signal / 25));
		for (int i = 0; i < 4; i++)
		{
			bars.Children.Add(new Rectangle
			{
				Width = 3,
				Height = 5 + i * 3,
				Margin = new Thickness(1, 0, 1, 0),
				Fill = brush,
				Opacity = i < filled ? 1.0 : 0.22,
				VerticalAlignment = VerticalAlignment.Bottom
			});
		}
		return bars;
	}

	private void LoadBtDevices(bool force)
	{
		if (btScanning)
		{
			return;
		}
		btScanning = true;
		btDeviceList.Children.Clear();
		btDeviceList.Children.Add(EmptyHint("正在读取蓝牙设备…"));
		Task.Run(delegate
		{
			List<string> lines = null;
			try
			{
				CcReply reply = Bridge.BluetoothDevices();
				lines = (reply.Value ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
			}
			catch
			{
			}
			Dispatcher.BeginInvoke(new Action(delegate
			{
				RunWhenIdle(delegate
				{
					btDeviceList.Children.Clear();
					if (lines == null || lines.Count == 0)
					{
						btDeviceList.Children.Add(EmptyHint("未找到已配对设备"));
					}
					else
					{
						foreach (string line in lines)
						{
							btDeviceList.Children.Add(BtDeviceRow(line));
						}
					}
					btScanning = false;
					SyncExpandedSize();
				});
			}));
		});
	}

	private FrameworkElement BtDeviceRow(string line)
	{
		Border card = new Border { CornerRadius = new CornerRadius(12), Background = CardBrush(), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
		card.Child = new TextBlock { Text = line, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
		bool armed = false;
		card.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			if (!armed)
			{
				armed = true;
				card.Child = new TextBlock { Text = line + "（再次点击确认移除）", FontSize = 12.5, Foreground = MutedBrush };
				DispatcherTimer reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
				reset.Tick += (object s2, EventArgs e2) => { reset.Stop(); card.Child = new TextBlock { Text = line, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis }; armed = false; };
				reset.Start();
				return;
			}
			string id = line.Split('\t')[0];
			Task.Run(delegate { try { Bridge.Unpair(id); } catch { } });
			Dispatcher.BeginInvoke(new Action(delegate { LoadBtDevices(false); }), DispatcherPriority.ApplicationIdle);
		};
		return card;
	}

	// —�?定位 / 动画 / 全屏 —�?
	private Rect MonitorWorkArea()
	{
		try
		{
			int idx = Math.Max(0, O.MonitorIndex);
			System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
			System.Windows.Forms.Screen screen = idx < screens.Length ? screens[idx] : System.Windows.Forms.Screen.PrimaryScreen;
			System.Drawing.Rectangle wa = screen.WorkingArea;
			PresentationSource source = PresentationSource.FromVisual(this);
			Matrix m;
			if (source != null && source.CompositionTarget != null)
			{
				m = source.CompositionTarget.TransformFromDevice;
			}
			else
			{
				using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
				m = new Matrix(96.0 / graphics.DpiX, 0.0, 0.0, 96.0 / graphics.DpiY, 0.0, 0.0);
			}
			System.Windows.Point tl = m.Transform(new System.Windows.Point(wa.Left, wa.Top));
			System.Windows.Point br = m.Transform(new System.Windows.Point(wa.Right, wa.Bottom));
			return new Rect(tl, br);
		}
		catch
		{
			return SystemParameters.WorkArea;
		}
	}

// 展开高度自适应：按当前可见页面的内容测量所需高度（页面内部列表有 MaxHeight 上限�?
// 测量值有界），再加头�?46 + 页面上边�?6 + 页面下边�?14 + 根边�?28；不超过工作区可用高度�?
private double MeasureExpandedHeight()
	{
		try
		{
			pageHost.Measure(new Size(Math.Max(120, O.ExpandedWidth - 56), double.PositiveInfinity));
			double page = Math.Max(60, pageHost.DesiredSize.Height);
			Rect wa = MonitorWorkArea();
			double maxH = Math.Max(160, wa.Height - O.TopGap - 12);
			expandedHeight = Math.Min(86.0 + page, maxH);
		}
		catch
		{
		}
		return expandedHeight;
	}

	// 展开宽度自适应：剪贴板/通知内容放不下时小幅加宽（按内容自然宽度），
// 但不超过 ExpandedWidth+240 与工作区宽度；其他页面保持用户设置的宽度�?
private double MeasureExpandedWidth()
	{
		try
		{
			double baseW = O.ExpandedWidth;
			Rect wa = MonitorWorkArea();
			double cap = Math.Min(Math.Max(baseW, wa.Width - 24.0), baseW + 240.0);
			if (cap <= baseW)
			{
				return baseW;
			}
			double natural = baseW;
			tabBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			natural = Math.Max(natural, tabBar.DesiredSize.Width + 76.0);
			if (currentPage == 1 && notifCats != null && notifList != null)
			{
				notifCats.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				double chipsW = notifCats.DesiredSize.Width;
				double cardsW = 0;
				foreach (object c in notifList.Children)
				{
					if (c is FrameworkElement fe)
					{
						fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
						cardsW = Math.Max(cardsW, fe.DesiredSize.Width);
					}
				}
				// 保留页签栏的自然宽度，避免通知内容较窄时把窗口压窄、
				// 右侧「随手记」页签被挤出可视区
				natural = Math.Max(natural, Math.Max(chipsW, cardsW) + 84.0);
			}
			else if (currentPage == 2 && clipList != null)
			{
				double cardsW = 0;
				foreach (object c in clipList.Children)
				{
					if (c is FrameworkElement fe)
					{
						fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
						cardsW = Math.Max(cardsW, fe.DesiredSize.Width);
					}
				}
				natural = Math.Max(natural, cardsW + 84.0);
			}
			else if (currentPage == 4 && noteList != null)
			{
				double cardsW = 0;
				foreach (object c in noteList.Children)
				{
					if (c is FrameworkElement fe)
					{
						fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
						cardsW = Math.Max(cardsW, fe.DesiredSize.Width);
					}
				}
				natural = Math.Max(natural, cardsW + 84.0);
			}
			expandedWidth = Math.Min(cap, natural);
		}
		catch
		{
		}
		return expandedWidth;
	}

// 展开态按内容重新测量并（在无变化时不动、有变化时）平滑过渡到新尺寸�?
// 宽高都只动窗口矩形、不缩放内容（标�?页签保持 1:1，边缘平滑伸缩）�?
// 圆角裁切�?SizeChanged→RefreshClip 逐帧同步�?
private void SyncExpandedSize()
	{
		if (mode != Mode.Expanded)
		{
			return;
		}
		double w = MeasureExpandedWidth();
		double h = MeasureExpandedHeight();
		double cw = ActualWidth;
		double ch = ActualHeight;
		if (Math.Abs(cw - w) < 0.5 && Math.Abs(ch - h) < 0.5)
		{
			return;
		}
		if (O.AnimationEnabled && IsVisible)
		{
			syncToken++;
			int tok = syncToken;
			double left = MonitorWorkArea().Left + (MonitorWorkArea().Width - w) / 2.0;
			// 只动画高度（矩形底边伸缩）；宽度与水平位置一步到位。
			// 分层窗口同时逐帧改 宽+高+位置 三路会产生明显抖动；高度-only 与
			// “分页切换高度调节”是同一类动画（已验证丝滑），内容钉住不重排。
			Width = w;
			Left = left;
			Height = ch;
			double contentW = Math.Max(1.0, w - 28.0);
			double contentH = Math.Max(1.0, h - (SnapTop() ? 14.0 : 28.0));
			PinContent(expandedRoot, contentW, contentH);
			// 入场（从胶囊展开）与页面内高度调节分开处理：
			// 入场=内容保持原始大小，只做 淡入+轻微下滑，绝不缩放（缩放观感差）；
			// 页面切换/列表变化=只钉住内容，窗口矩形平滑伸缩，底部自然显隐。
			if (expandEntrance)
			{
				expandedRoot.HorizontalAlignment = HorizontalAlignment.Center;
				expandedRoot.CacheMode = new BitmapCache(1.0);
				expandedRoot.RenderTransform = new TranslateTransform(0.0, -12.0);
				expandedRoot.Opacity = 0.0;
				expandedRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(Math.Max(160.0, Math.Min(320.0, 220.0 / O.ExpandSpeed)))) { From = 0.0, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
				((TranslateTransform)expandedRoot.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-12.0, 0.0, TimeSpan.FromMilliseconds(Math.Max(200.0, Math.Min(420.0, 300.0 / O.ExpandSpeed)))) { From = -12.0, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
			}
			TimeSpan dur = TimeSpan.FromMilliseconds(Math.Max(200.0, Math.Min(420.0, 300.0 / O.ExpandSpeed)));
			DoubleAnimation aH = new DoubleAnimation(ch, h, dur) { From = ch, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
			aH.Completed += delegate
			{
				// 无条件清理视觉层（透明度/位移/位图缓存）——即使本动画已被更新的动画替换
				expandedRoot.BeginAnimation(OpacityProperty, null);
				expandedRoot.Opacity = 1.0;
				expandedRoot.RenderTransform = null;
				expandedRoot.RenderTransformOrigin = new Point(0.5, 0.5);
				expandedRoot.CacheMode = null;
				Store.Trace("ISLAND syncexp done w=" + w + " h=" + h + " mode=" + mode + " tokOk=" + (tok == syncToken));
				// 两重保护，防止把别的过渡动画打断（“快速点外面后小岛卡在中间”的根因）：
				if (tok != syncToken || mode != Mode.Expanded)
				{
					return;
				}
				BeginAnimation(HeightProperty, null);
				Height = h;
				Width = w;
				Left = left;
				UnpinContent(expandedRoot);
			};
			BeginAnimation(HeightProperty, aH);
		}
		else
		{
			Width = w;
			Height = h;
			expandedRoot.BeginAnimation(OpacityProperty, null);
			expandedRoot.Opacity = 1.0;
			expandedRoot.RenderTransform = null;
			expandedRoot.RenderTransformOrigin = new Point(0.5, 0.5);
			expandedRoot.CacheMode = null;
			UnpinContent(expandedRoot);
			RefreshClip();
		}
	}

	// 过渡动画期间把重活（列表填充/整树重建）推迟到动画结束后执行：
	// AllowsTransparency 窗口每帧都要整窗脏区拷贝，动画期间任何布局/文本重排都会吃掉
// 帧预算，直接�?60fps 动画拖成“一帧一帧抽出来”�?
private void RunWhenIdle(Action work)
	{
		if (!sizeAnimating)
		{
			try
			{
				work();
			}
			catch
			{
			}
			return;
		}
		DispatcherTimer retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
		retry.Tick += delegate
		{
			if (!sizeAnimating)
			{
				retry.Stop();
				try
				{
					work();
				}
				catch
				{
				}
			}
		};
		retry.Start();
	}

	private void PositionAnimated(double width, double height, Action onDone = null)
	{
		Rect wa = MonitorWorkArea();
		double left = wa.Left + (wa.Width - width) / 2.0;
		double top = SnapTop() ? wa.Top : wa.Top + Math.Max(0, O.TopGap);
// 吸附顶部�?Root 顶边距为 0，Surface 比非吸附状态多�?14px 高度—�?

// 视觉上整条小岛会“被拉高”。窗口高度补�?14px，保证两种模式下可见高度一致�?

if (SnapTop())
		{
			height = Math.Max(40.0, height - 14.0);
		}
		bool anim = O.AnimationEnabled;
		double duration = anim ? 460.0 / Math.Max(0.5, O.ExpandSpeed) : 0.0;
		if (duration < 280.0)
		{
			duration = 280.0;
		}
		if (duration > 600.0)
		{
			duration = 600.0;
		}

// 在清除动画之前先读当前值（可能正处于上一次动画中�?

double fromW = ActualWidth;
		double fromH = ActualHeight;
		double fromL = Left;
		bool firstShow = double.IsNaN(fromW) || fromW <= 0 || double.IsNaN(fromL);

// 位置单独变化（设置里拖“顶部间距�?换显示器）：窗口矩形直接落位�?

// 不打断、不重播缩放过渡。此前这里的守卫只比较了宽度/高度/左缘，漏�?Top—�?

// 拖动间距时后�?tick 全被 sameTarget 拦住，Top 永远不更新（“调�?0 不在顶部”）�?

if (!firstShow && !double.IsNaN(fromW) && fromW > 0 && Math.Abs(fromW - width) < 0.5 && Math.Abs(fromH - height) < 0.5)
		{
			Left = left;
			Top = top;
			animTargetL = left;
			RefreshClip();
			return;
		}

// 目标与当前动画目标一致：绝不打断进行中的动画，直接返回�?

// （曾经在这里先清动画再返回，会把窗口冻在动画中间值——小岛“消失”的根因。）
		bool sameTarget = sizeAnimating
			&& Math.Abs(animTargetW - width) < 0.5 && Math.Abs(animTargetH - height) < 0.5 && Math.Abs(animTargetL - left) < 0.5;
		if (!firstShow && (sameTarget || (!sizeAnimating && !double.IsNaN(fromW) && Math.Abs(fromW - width) < 0.5 && Math.Abs(fromH - height) < 0.5 && !double.IsNaN(fromL) && Math.Abs(fromL - left) < 0.5 && Math.Abs(Top - top) < 0.5)))
		{
			RefreshClip();
			return;
		}

// 先把上一个动画持有值固化为基值，再清除动画——否则清除后窗口会瞬间跳�?

// 基值（旧尺寸），收�?退场动画就变成“先瞬移再放空动画”�?

BeginAnimation(WidthProperty, null);
		BeginAnimation(HeightProperty, null);
		BeginAnimation(LeftProperty, null);
		BeginAnimation(OpacityProperty, null);
		if (Root.RenderTransform is ScaleTransform oldSt)
		{
			oldSt.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			oldSt.BeginAnimation(ScaleTransform.ScaleYProperty, null);
		}
		Root.RenderTransform = null;
		if (!double.IsNaN(fromW) && fromW > 0)
		{
			Width = fromW;
		}
		if (!double.IsNaN(fromH) && fromH > 0)
		{
			Height = fromH;
		}
		if (!double.IsNaN(fromL))
		{
			Left = fromL;
		}
		Top = top;

		if (!anim || duration <= 1)
		{
			sizeAnimating = false;
			animTargetW = animTargetH = animTargetL = double.NaN;
			Width = width;
			Height = height;
			Left = left;
			Opacity = 1.0;
			Surface.Effect = IslandChrome.Shadow(O);
			UnpinContent(collapsedRoot);
			UnpinContent(expandedRoot);
			RefreshClip();
			onDone?.Invoke();
			return;
		}
		// 丝滑动效（参�?WPF 官方“animate the size of a FrameworkElement”的推荐做法）：
		// 窗口矩形直接落到目标尺寸——逐帧�?Width/Height 会让整棵可视树每帧重排，
// 这正是旧版动画卡顿的根源。视觉过渡完全交�?GPU 合成�?RenderTransform 缩放�?

// 内容只排版一次；配合内容层自身的淡入（FadeInContent），观感平滑不重排�?

Width = width;
		Height = height;
		Left = left;
		UnpinContent(collapsedRoot);
		UnpinContent(expandedRoot);
		animTargetW = width;
		animTargetH = height;
		animTargetL = left;
		sizeAnimStart = Environment.TickCount;
		sizeAnimating = true;
		// 缩放起点 = 上一尺寸/目标尺寸。统一为等比缩放（fx == fy）：
// 非等比缩放会把文�?图标压扁再复原，视觉上就是“抖�?水波纹”�?

// 展开→从统一比例放大到位；收起→从统一比例压缩回来；首显→�?70% 放大入场�?

double fx = (fromW > 0.0 && width > 0.0) ? Math.Max(0.35, Math.Min(1.6, fromW / width)) : 0.7;
		double fy = (fromH > 0.0 && height > 0.0) ? Math.Max(0.35, Math.Min(1.6, fromH / height)) : 0.7;
		bool growing = width > fromW + 0.5 || height > fromH + 0.5;
		bool shrinking = (fromW > width + 0.5 || fromH > height + 0.5) && !firstShow;
		double s = 0.7;
		if (growing)
		{
			s = Math.Max(0.5, Math.Min(1.0, Math.Min(fx, fy)));
		}
		else if (shrinking)
		{
			s = Math.Min(1.18, Math.Max(1.0, Math.Max(fx, fy)));
		}
		else
		{
			s = Math.Max(0.92, Math.Min(1.08, (fx + fy) / 2.0));
		}
		fx = s;
		fy = s;
		CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
// DropShadowEffect 每帧都要把子树光栅化进中间缓冲再模糊，是 WPF 动画卡顿�?

// 经典来源：过渡期间摘除阴影，结束再装回（IslandChrome.Shadow 复原）�?

Surface.Effect = null;
// 收起/退场走窗口矩形逐帧收缩：缩放路径会把内容放大到窗口边界之外�?

// 两侧被窗口边缘平直裁掉（“动画两边被裁剪”）。此时内容已切换为轻量收起条�?

// 逐帧重排成本可忽略；SizeChanged→RefreshClip 每帧同步圆角裁切，不溢出不闪边�?

if (shrinking)
		{
			Width = fromW;
			Height = fromH;
			Left = fromL;
			Top = top;
			// 收缩时钉住“当前可见的内容根”：从展开态收起就钉住展开面板（当前分页），
			// 内容保持原始大小、水平居中：窗口向上收缩，底部像幕布一样逐帧收拢。
			// 无缩放、无单侧裁剪（内容中心始终跟随窗口中心）。
			FrameworkElement pinned = null;
			if (expandedRoot.Visibility == Visibility.Visible)
			{
				pinned = expandedRoot;
				double contentW = Math.Max(1.0, fromW - 28.0);
				double contentH = Math.Max(1.0, fromH - (SnapTop() ? 14.0 : 28.0));
				PinContent(pinned, contentW, contentH);
				pinned.HorizontalAlignment = HorizontalAlignment.Center;
				pinned.CacheMode = new BitmapCache(1.0);
				pinned.Opacity = 1.0;
				pinned.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.55, TimeSpan.FromMilliseconds(duration)) { From = 1.0, EasingFunction = ease });
			}
			else if (collapsedRoot.Visibility == Visibility.Visible)
			{
				pinned = collapsedRoot;
				PinContent(pinned, Math.Max(1.0, width - 28.0), Math.Max(1.0, height - (SnapTop() ? 14.0 : 28.0)));
			}
			animTargetW = width;
			animTargetH = height;
			animTargetL = left;
			sizeAnimStart = Environment.TickCount;
			sizeAnimating = true;
			transitionToken++;
			int tokShrink = transitionToken;
			EventHandler done2 = (object s2, EventArgs e2) =>
			{
				// 无条件清理视觉层（透明度/位图缓存）——即使本动画已被更新的动画替换
				if (pinned != null)
				{
					pinned.BeginAnimation(OpacityProperty, null);
					pinned.Opacity = 1.0;
					pinned.RenderTransform = null;
					pinned.RenderTransformOrigin = new Point(0.5, 0.5);
					pinned.CacheMode = null;
				}
				if (tokShrink != transitionToken)
				{
					return;
				}
				BeginAnimation(WidthProperty, null);
				BeginAnimation(HeightProperty, null);
				BeginAnimation(LeftProperty, null);
				Width = width;
				Height = height;
				sizeAnimating = false;
				animTargetW = animTargetH = animTargetL = double.NaN;
				Surface.Effect = IslandChrome.Shadow(O);
				RefreshClip();
				// 先切换内容（隐藏展开面板、显示收起条），再恢复钉住布局：标题等
				// 展开内容若先 Unpin 再隐藏，会在收窄的窗口里跳一下再消失（抖动）。
				onDone?.Invoke();
				if (pinned != null)
				{
					UnpinContent(pinned);
				}
				// 收缩期间锚定左缘不动；这里用 150ms 小动画顺滑校正到居中位，
				// 避免整条“瞬移”到中间产生的跳变感。
				DoubleAnimation aL2 = new DoubleAnimation(fromL, left, TimeSpan.FromMilliseconds(150))
				{
					From = fromL,
					EasingFunction = new QuadraticEase()
				};
				aL2.Completed += (object s3, EventArgs e3) =>
				{
					BeginAnimation(LeftProperty, null);
					Left = left;
				};
				BeginAnimation(LeftProperty, aL2);
			};
			DoubleAnimation aH = new DoubleAnimation(fromH, height, TimeSpan.FromMilliseconds(duration))
			{
				From = fromH,
				EasingFunction = ease
			};
			DoubleAnimation aW = new DoubleAnimation(fromW, width, TimeSpan.FromMilliseconds(duration))
			{
				From = fromW,
				EasingFunction = ease
			};
			aH.Completed += done2;
			// 收缩动画锚定左缘不动（Left 保持展开时的位置）：窗口原地变窄，避免
			// “先向右平移再收缩”的观感；结束后 done2 用 150ms 小动画校正到居中位。
			// 内容已钉住居中，左右像幕布一样随窗口收拢。
			BeginAnimation(HeightProperty, aH);
			BeginAnimation(WidthProperty, aW);
			RefreshClip();
			return;
		}
// 基值为 1.0：动画无论自然结束还是被清除，都收敛到正确终态，不会回跳�?

ScaleTransform st = new ScaleTransform(1.0, 1.0, width / 2.0, 0.0);
		Root.RenderTransform = st;
		transitionToken++;
		int tok = transitionToken;
		EventHandler done = (object s, EventArgs e) =>
		{
			if (tok != transitionToken)
			{
				return;
			}
			st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			Root.RenderTransform = null;
			BeginAnimation(OpacityProperty, null);
			Opacity = 1.0;
			sizeAnimating = false;
			animTargetW = animTargetH = animTargetL = double.NaN;
			Surface.Effect = IslandChrome.Shadow(O);
			RefreshClip();
		};
		DoubleAnimation aSX = new DoubleAnimation(fx, 1.0, TimeSpan.FromMilliseconds(duration))
		{
			From = fx,
			EasingFunction = ease
		};
		DoubleAnimation aSY = new DoubleAnimation(fy, 1.0, TimeSpan.FromMilliseconds(duration))
		{
			From = fy,
			EasingFunction = ease
		};
		aSX.Completed += done;
		st.BeginAnimation(ScaleTransform.ScaleXProperty, aSX);
		st.BeginAnimation(ScaleTransform.ScaleYProperty, aSY);
		if (firstShow)
		{
			Opacity = 0.0;
			DoubleAnimation aOp = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(Math.Max(160.0, duration * 0.6)))
			{
				From = 0.0,
				EasingFunction = ease
			};
			BeginAnimation(OpacityProperty, aOp);
		}
		RefreshClip();
	}

// 启动阶段（窗口尚未显示）就位并读取壁纸底图：壁纸文件直读/壁纸层渲染全程原地完成，
// 无需移窗、无需 Visibility 变化，不会产生 DWM 闪烁帧。
// 收起条永远停在屏幕顶部居中，壁纸底图只需与位置对齐一次；之后壁纸变化由
// 基类的壁纸钩子负责重读。交互过程中绝不为取底图移动窗口。
public void PrepareStartup()
	{
		try
		{
			Rect wa = MonitorWorkArea();
			double left = wa.Left + (wa.Width - O.CollapsedWidth) / 2.0;
			double top = SnapTop() ? wa.Top : wa.Top + Math.Max(0, O.TopGap);
			Root.Margin = SnapTop() ? new Thickness(14, 0, 14, 14) : new Thickness(14);
			// 停止 ctor 阶段可能已启动的入场动画：动画持有期间直接赋值 Width/Height
			// 会被动画值覆盖（窗口卡在半尺寸，高度不足导致 Backdrop 静默放弃）。


BeginAnimation(WidthProperty, null);
			BeginAnimation(HeightProperty, null);
			BeginAnimation(LeftProperty, null);
			BeginAnimation(OpacityProperty, null);
			sizeAnimating = false;
			animTargetW = animTargetH = animTargetL = double.NaN;
			Width = O.CollapsedWidth;
			Height = SnapTop() ? Math.Max(40.0, O.CollapsedHeight - 14.0) : O.CollapsedHeight;
			Left = left;
			Top = top;
			Opacity = 1.0;
			RefreshClip();
// ctor 阶段被提前 Show()（全屏检查的恢复路径）时窗口已在屏上：
// Backdrop 的可见分支会原地读壁纸，无需隐藏窗口。


// 启动阶段窗口从未被激活，读壁纸不会触发 Deactivated 级联。


// 之后交互过程中绝不为取底图移动窗口（闪烁根源）。


			Backdrop(LightTheme);
			expandedBackdropStale = !CaptureExpandedBackdrop(wa, top);
			int backdropCount = Root.Children.OfType<Border>().Count((Border b) => b.Tag as string == "backdrop");
			if (backdropCount == 0)
			{
				// 基类对“近黑帧”判定为黑屏异常而拒收；但壁纸顶部本身可能是深色区域
				// （实测用户壁纸顶部长条亮度≈6），灵动岛允许深色底图，仅拒绝纯黑帧。



CaptureBackdropAllowDark();
			}
			Store.Trace("ISLAND prepare done backdrops=" + backdropCount + " wh=" + ActualWidth + "x" + ActualHeight);
			// 启动瞬间 DWM/壁纸切换可能仍在过渡（纯黑帧被基类跳过）；
			// 安排最多 4 次、每 4 秒一次的延迟重读，拿到有效底图或超过次数即停。


bool hasBackdrop = Root.Children.OfType<Border>().Any((Border b) => b.Tag as string == "backdrop");
			if (!hasBackdrop && !backdropRetryArmed)
			{
				backdropRetryArmed = true;
				backdropRetryAttempts = 0;
				backdropRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.0) };
				backdropRetryTimer.Tick += (object s, EventArgs e) =>
				{
					try
					{
						backdropRetryAttempts++;
						bool has = Root.Children.OfType<Border>().Any((Border b) => b.Tag as string == "backdrop");
						if (has || mode == Mode.Hidden || !IsVisible || backdropRetryAttempts >= 4)
						{
							DispatcherTimer t = backdropRetryTimer;
							backdropRetryTimer = null;
							if (t != null)
							{
								t.Stop();
							}
							Store.Trace("ISLAND backdrop retry stop attempt=" + backdropRetryAttempts + " has=" + has + " mode=" + mode);
							return;
						}
						Store.Trace("ISLAND backdrop retry attempt=" + backdropRetryAttempts);
						RecaptureBackdrop(LightTheme);
// 基类仍拒收（壁纸深色长条）→ 允许深色底图的回退：原地重读原区域。





if (!Root.Children.OfType<Border>().Any((Border b) => b.Tag as string == "backdrop"))
						{
							IntPtr hwnd = new WindowInteropHelper(this).Handle;
							if (Native.GetWindowRect(hwnd, out Native.RECT rect))
							{
								// 只重试壁纸源；来源不可用时保留现有干净底图。
								CaptureBackdropAllowDark(rect);
							}
						}
					}
					catch
					{
					}
				};
				backdropRetryTimer.Start();
			}
// 入场：底图就绪后淡入（尺寸已就位，只做透明度过渡，不做缩放）。


if (O.AnimationEnabled)
			{
				Opacity = 0.0;
				BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(240)));
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

// 基类 CaptureRegion 把“近黑帧”当作黑屏异常拒收（防开始菜单过渡期涂黑）；
// 但用户壁纸顶部本身可能是深色长条（实测亮度≈6），灵动岛允许深色底图，
// 只拒绝 64 采样均值 < 4 的纯黑帧（显示器关闭/锁屏等真正异常）。
// 展开面板的背景预取：启动时（窗口尚未显示）把“未来展开后覆盖的区域”一次性
// 读成全尺寸壁纸底图。收起态是同一区域的下采样（清晰），展开态 1:1 对应，
// 避免用收起条的小图拉伸放大成“脏”背景。
// 展开前同步重读展开区域底图：壁纸文件直读/壁纸层渲染全程窗口原地不动，
// 无闪烁、无停顿，也不会把收起条自身烘进底图。
// 展开面板必须等到底图就绪才显示，避免小图拉伸的脏层闪现。
private void RefreshExpandedBackdrop()
{
	try
	{
		Rect wa = MonitorWorkArea();
		double top = SnapTop() ? wa.Top : wa.Top + Math.Max(0, O.TopGap);
		CaptureExpandedBackdrop(wa, top);
	}
	catch (Exception ex)
	{
		Store.Log(ex);
	}
}

// 基类管线重捕会把展开区底图替换成收起条小图（展开时小图拉伸=脏层）：
// 任何基类底图重捕后一律标记展开底图过期，由 Expand 在面板显示前重捕展开区域。
public override void Backdrop(bool light)
{
	base.Backdrop(light);
	if (mode != Mode.Expanded)
	{
		expandedBackdropStale = true;
	}
}

private bool CaptureExpandedBackdrop(Rect wa, double top)
	{
		try
		{
			double ew = MeasureExpandedWidth();
			double eh = MeasureExpandedHeight();
			if (ew < 40.0 || eh < 40.0)
			{
				return false;
			}
			IntPtr hwnd = new WindowInteropHelper(this).Handle;
			int dpi = Native.DpiOf(hwnd);
			double exLeft = wa.Left + (wa.Width - ew) / 2.0;
			Native.RECT er = new Native.RECT
			{
				Left = (int)Math.Round(exLeft * dpi / 96.0),
				Top = (int)Math.Round(top * dpi / 96.0),
				Right = (int)Math.Round((exLeft + ew) * dpi / 96.0),
				Bottom = (int)Math.Round((top + eh) * dpi / 96.0)
			};
			return CaptureBackdropAllowDark(er);
		}
		catch
		{
			return false;
		}
	}

private bool CaptureBackdropAllowDark(Native.RECT? originalRect = null)
	{
		try
		{
			if (Width < 40.0 || Height < 40.0 || GlassPercent == 0.0)
			{
				return false;
			}
			IntPtr hwnd = new WindowInteropHelper(this).Handle;
			Native.RECT rect = originalRect ?? default(Native.RECT);
			if (!originalRect.HasValue && (!Native.GetWindowRect(hwnd, out rect) || rect.Right - rect.Left < 56 || rect.Bottom - rect.Top < 56))
			{
				return false;
			}
			int dpi = Native.DpiOf(hwnd);
			int margin = (int)Math.Round(14.0 * dpi / 96.0);
			int w = Math.Max(1, rect.Right - rect.Left - 2 * margin);
			int h = Math.Max(1, rect.Bottom - rect.Top - 2 * margin);
			Native.RECT src = new Native.RECT
			{
				Left = rect.Left + margin,
				Top = rect.Top + margin,
				Right = rect.Right - margin,
				Bottom = rect.Bottom - margin
			};
			System.Drawing.Bitmap bitmap = GlassCapture.Acquire(src, out bool fromFile);
			if (bitmap == null)
			{
				return false;
			}
			using (bitmap)
			{
				bool pureBlack = !fromFile;
				for (int i = 0; i < 8 && pureBlack; i++)
				{
					for (int j = 0; j < 8; j++)
					{
						System.Drawing.Color c = bitmap.GetPixel(Math.Max(0, w - 1) * i / 7, Math.Max(0, h - 1) * j / 7);
						if (c.R >= 4 || c.G >= 4 || c.B >= 4)
						{
							pureBlack = false;
							break;
						}
					}
				}
				if (pureBlack)
				{
					return false;
				}
				// 统一烘焙（与基类同管线）：模糊关闭/很小时保留原分辨率；否则 1/8 高质量
				// 缩小 + 盒式模糊烘进小图。运行时图层不挂 BlurEffect：展开/收起动画每帧
				// 改变图层尺寸时高斯模糊整层重渲染，动画被拖僵且产生 banding。
				System.Windows.Media.Imaging.BitmapImage img = GlassMaterials.Bake(bitmap, MaterialBlur, MaterialBlur / 8.0);
				// 同步基类缓存键：展开态下基类 Backdrop 可见分支校验键匹配后只刷样式，
				// 不会因"基类缓存空/过期"而反复触发重捕（设置页实时预览会闪窗）。
				cachedBackdrop = img;
				backdropKey = BackdropKeyOfRect(rect, dpi, LightTheme, MaterialBlur);
				AddIslandBackdrop(img);
				return true;
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
		return false;
	}

// 前台窗口变化/壁纸广播触发的可见态重捕：展开态必须走灵动岛自己的底图管线
// （允许深色底图 + 烘焙模糊，运行时无 BlurEffect），收起态回退基类管线。
public override void RecaptureBackdrop(bool light)
{
	if (mode != Mode.Expanded)
	{
		base.RecaptureBackdrop(light);
		// 基类管线把展开区底图换成了收起条小图：标记过期，下次展开前重捕展开区域。
		expandedBackdropStale = true;
		return;
	}
	if (Width < 40.0 || Height < 40.0 || GlassPercent == 0.0)
	{
		base.RecaptureBackdrop(light);
		expandedBackdropStale = true;
		return;
	}
	try
	{
		IntPtr hwnd = new WindowInteropHelper(this).Handle;
		if (!Native.GetWindowRect(hwnd, out Native.RECT rect) || rect.Right - rect.Left < 56 || rect.Bottom - rect.Top < 56)
		{
			return;
		}
		if (CaptureBackdropAllowDark(rect))
		{
			expandedBackdropStale = false;
		}
	}
	catch (Exception ex)
	{
		Store.Log(ex);
	}
}

// 与基�?AddBackdrop 相同的两层结构（内层模糊�?+ 外层透明度），供深色壁纸回退使用�?
private void AddIslandBackdrop(System.Windows.Media.ImageSource image)
	{
		double w = Math.Max(1, ActualWidth - 28);
		double h = Math.Max(1, ActualHeight - (SnapTop() ? 14 : 28));
		CornerRadii(out double tl, out double tr, out double br, out double bl);
		Geometry clip = IslandChrome.RoundedRect(w, h, tl, tr, br, bl);
		Border existingOuter = Root.Children.OfType<Border>().FirstOrDefault((Border b) => b.Tag as string == "backdrop" && b.Child is Border);
		if (existingOuter != null && existingOuter.Child is Border existingInner)
		{
			existingInner.CornerRadius = new CornerRadius(tl, tr, br, bl);
			existingInner.IsHitTestVisible = false;
			existingInner.Background = new ImageBrush(image) { Stretch = Stretch.Fill };
			existingInner.Opacity = LightTheme ? 0.8 : 0.66;
			existingInner.Clip = clip;
			existingOuter.Clip = clip;
			existingOuter.Opacity = GlassPercent / 100.0;
			foreach (Border duplicate in Root.Children.OfType<Border>().Where((Border b) => b.Tag as string == "backdrop" && b != existingOuter).ToList())
			{
				Root.Children.Remove(duplicate);
			}
			return;
		}
		foreach (Border b in Root.Children.OfType<Border>().Where((Border b) => b.Tag as string == "backdrop").ToList())
		{
			Root.Children.Remove(b);
		}
		Border inner = new Border
		{
			Tag = "backdrop",
			CornerRadius = new CornerRadius(tl, tr, br, bl),
			IsHitTestVisible = false,
			Background = new ImageBrush(image) { Stretch = Stretch.Fill },
			Opacity = LightTheme ? 0.8 : 0.66,
			Clip = clip,
			Effect = null
		};
		Border outer = new Border
		{
			Tag = "backdrop",
			IsHitTestVisible = false,
			Opacity = GlassPercent / 100.0,
			Child = inner
		};
		Root.Children.Insert(0, outer);
		Store.Trace("ISLAND backdrop captured (dark wallpaper) w=" + w + "x" + h + " blur=" + MaterialBlur + (MaterialBlur < 6.0 ? "(full)" : "(baked)"));
	}

	// 动画期间把内容固定到最终尺寸：窗口逐帧变化时，内容不再逐帧 measure/arrange。
	// 逐帧重排是 AllowsTransparency 窗口“抽帧卡顿”的最大来源；固定后每帧只有裁剪+合成。
	private void PinContent(FrameworkElement el, double w, double h)
	{
		el.Width = Math.Max(1.0, w);
		el.Height = Math.Max(1.0, h);
		el.HorizontalAlignment = HorizontalAlignment.Left;
		el.VerticalAlignment = VerticalAlignment.Top;
	}

	private void UnpinContent(FrameworkElement el)
	{
		el.Width = double.NaN;
		el.Height = double.NaN;
		el.HorizontalAlignment = HorizontalAlignment.Stretch;
		el.VerticalAlignment = VerticalAlignment.Stretch;
	}

	private void RefreshClip()
	{
		try
		{
			double w = Math.Max(1, ActualWidth - 28);
			double h = Math.Max(1, ActualHeight - (SnapTop() ? 14 : 28));
			CornerRadii(out double tl, out double tr, out double br, out double bl);
			Geometry clip = IslandChrome.RoundedRect(w, h, tl, tr, br, bl);
			ContentGrid.Clip = clip;
			Surface.CornerRadius = new CornerRadius(tl, tr, br, bl);
			foreach (Border b in Root.Children.OfType<Border>().Where((Border b) => b.Tag as string == "backdrop"))
			{
				b.Clip = clip;
				if (b.Child is Border inner)
				{
					inner.Clip = clip;
					inner.CornerRadius = new CornerRadius(tl, tr, br, bl);
				}
			}
		}
		catch
		{
		}
	}

	private double ModeCorner()
	{
// 事件卡与小岛同尺寸，圆角与收起条一致（胶囊=�?2，圆角矩�?设置圆角）�?

double h = mode == Mode.Expanded ? expandedHeight : O.CollapsedHeight;
		return O.Shape == 0 ? Math.Min(h / 2.0, 48.0) : O.CornerRadius;
	}

	// 顶部间距�?0 时吸附到屏幕顶边：窗口顶缘与屏幕边缘齐平（顶部圆�?0），
// 两侧保留大圆角向下弧出，呈现“波浪滑下、连接底部”的悬挂状态�?
private bool SnapTop()
	{
		return O.TopGap <= 0.5;
	}

	private void CornerRadii(out double tl, out double tr, out double br, out double bl)
	{
		if (SnapTop())
		{
// 吸附：顶角为 0；两侧弯下的弧度正好是“形状与布局”里设置的圆角值�?


tl = 0.0;
			tr = 0.0;
			br = O.CornerRadius;
			bl = O.CornerRadius;
		}
		else
		{
			double r = ModeCorner();
			tl = tr = br = bl = r;
		}
	}

	public void CheckFullscreen()
	{
// 全屏判定要排除“最大化”窗口：任务栏隐�?自动隐藏时，最大化窗口的矩形等于整�?

// 显示器边界，会被 FullscreenState 误判为全屏，导致灵动岛一直隐藏（用户最常见现象）�?

// 真正的全屏（F11、无边框全屏游戏）通常没有 WS_MAXIMIZE 样式�?

bool full = FullscreenState.Active() && !ForegroundIsMaximized();
		if (full)
		{
// 用户主动展开的面板不因全屏策略强制隐藏（规格：展开面板打开时不被事件强行关闭）�?


// 只有被动状态（收起�?事件卡）在“全屏时隐藏”开启时避让�?


if (!O.ShowOnFullscreen && mode != Mode.Hidden && mode != Mode.Expanded)
			{
				HideForFullscreen();
			}
		}
		else if (mode == Mode.Hidden && modeBeforeHide != Mode.Hidden && O.Enabled)
		{
			RestoreFromFullscreen();
		}
	}

	private static bool ForegroundIsMaximized()
	{
		try
		{
			IntPtr h = GetForegroundWindow();
			if (h == IntPtr.Zero)
			{
				return false;
			}
			return (GetWindowLongW(h, GWL_STYLE) & WS_MAXIMIZE) != 0;
		}
		catch
		{
			return false;
		}
	}

	// —�?外观应用（Hub / 设置页调用） —�?
	// 置顶策略：显示桌面守护临时置顶优先；否则跟随"保持在窗口最上方"设置（默认关闭）。
	private void ApplyTopmost()
	{
		bool on = desktopRaised || (O != null && O.AlwaysOnTop);
		// 读 Win32 实际置顶位做同步：曾有代码绕过 WPF 直接 SetWindowPos(HWND_TOPMOST)
		// 改位（SetNoActivate 旧实现），WPF 属性不知道、永远不同步。这里以属性与
		// 实际位双重对比，任何不一致都强制拉回。
		bool actual = false;
		try
		{
			IntPtr hwnd = new WindowInteropHelper(this).Handle;
			actual = (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
		}
		catch
		{
		}
		if (Topmost != on || actual != on)
		{
			Store.Trace("ISLAND topmost on=" + on + " raised=" + desktopRaised + " setting=" + (O != null && O.AlwaysOnTop));
			Topmost = on;
		}
	}

	public void ApplyStyle(IslandOptions o)
	{
		if (o == null)
		{
			o = new IslandOptions();
		}
		bool light = IslandChrome.EffectiveLight(o, C.Prefs.Light);
		LightTheme = light;
		ApplyTopmost();
		AccentIndex = o.Accent;
		BlurRadius = o.BlurRadius;
		EdgeStrength = 1.0;
// 顶部吸附模式：Root 顶边距为 0，窗口顶缘与屏幕边缘齐平�?

// 其余模式保持基类 14px 边距（背景快�?裁切都按它对齐）�?

Root.Margin = SnapTop() ? new Thickness(14, 0, 14, 14) : new Thickness(14);
// 灵动岛只用单层扁平底色：强制 Kind=0（纯色，无渐变）并去�?

// 基类玻璃方案的高光渐�?Gloss)与颗粒噪�?Grain)覆盖层—�?

// 那层噪点就是“磨砂质感”，高光+噪点叠在半透明底色上形成“两层背景”�?

MaterialOptions m = (o.Material ?? new MaterialOptions()).Copy();
		m.Kind = 0;
		m.Gloss = 0.0;
		m.Grain = 0.0;
		SetMaterial(m);
		SetGlassOpacity(o.OpacityPercent);
		IslandChrome.Apply(Surface, o, light);
// 吸附模式：完全无边框线（顶边贴屏、两侧和底边都由玻璃填充和阴影界定形状）�?

// 悬浮模式四边 1px�?

Surface.BorderThickness = new Thickness(0); // 吸附与非吸附都不显示边框
		// 文字/图标颜色按实际背景亮度自适应（底�?× 不透明�?+ 壁纸亮度混合）；
// 对比度变化时对整棵可视树重着色，保证收起态与展开态用色一致�?

bool textBefore = textLight;
		UpdateTextContrast();
// 全岛文字颜色统一：窗口级 Foreground 让所有未显式指定画刷的元素继承同一主题文字�?

// （此前页签等元素继承的是默认黑色，深色模式下仍是黑字）�?

Foreground = TextBrush;
		if (textLight != textBefore)
		{
			RecolorTree(ContentGrid);
		}
		// 构建期静态元素的主题重着色（LightTheme 变化后必须更新）
		RestyleStatic();
// 内边距：展开面板固定 24；收起态两侧固�?8（标题填满中间，两侧自然紧凑）�?

collapsedStack.Margin = new Thickness(8, 4, 8, 4);
		tabBar.Margin = new Thickness(24, 0, 6, 0);
		if (headerDivider != null)
		{
			headerDivider.BorderBrush = LineBrush();
		}
		pageHost.Margin = new Thickness(24, 8, 24, 4);
		pageHeading.Margin = new Thickness(56, 10, 24, 6);
		backBtn.Margin = new Thickness(16, 6, 0, 0);
// 事件卡样式完全跟随皮肤（圆角/字号/图标/间距/颜色），�?RestyleStatic→RestyleToast 统一处理�?

RestyleToast();
// 收起态按新样式重�?

RebuildCollapsed();
		RebuildTabs();
// 展开面板页面按新主题刷新（颜�?数据全部重建�?

RefreshMusicPage();
		RefreshNotificationsPage();
		RefreshClipboardPage();
		RefreshControlQuick();
		if (currentPage == 10)
		{
			LoadNetworks(false);
		}
		if (currentPage == 11)
		{
			LoadBtDevices(false);
		}
		// 三态根节点互斥（防止样式热更新时出现两个根同时可见的重叠）
		if (mode == Mode.Collapsed)
		{
			collapsedRoot.Visibility = Visibility.Visible;
			toastCard.Visibility = Visibility.Collapsed;
			expandedRoot.Visibility = Visibility.Collapsed;
		}
		else if (mode == Mode.Toast)
		{
			collapsedRoot.Visibility = Visibility.Collapsed;
			toastCard.Visibility = Visibility.Visible;
			expandedRoot.Visibility = Visibility.Collapsed;
		}
		else if (mode == Mode.Hidden)
		{
			collapsedRoot.Visibility = Visibility.Collapsed;
		}
		RefreshClip();
		// 位置重算（显示器/间距可能已变）；展开态宽高按内容自适应
		double w = mode == Mode.Expanded ? MeasureExpandedWidth() : O.CollapsedWidth;
		double h = mode == Mode.Expanded ? MeasureExpandedHeight() : O.CollapsedHeight;
		PositionAnimated(w, h);
		// 全屏策略即时生效
		CheckFullscreen();
	}

	private void RestyleStatic()
	{
		try
		{
			double coverRadius = Math.Min(O.CornerRadius, 48.0);
			if (bigCover != null)
			{
				bigCover.CornerRadius = new CornerRadius(coverRadius);
				bigCover.Clip = new RectangleGeometry(new Rect(0, 0, 96, 96), coverRadius, coverRadius);
			}
			pageHeading.Foreground = TextBrush;
			if (closeBtn.Child is Grid g1)
			{
				g1.Children.Clear();
				g1.Children.Add(IslandIcons.Close(IconBrush(), 12));
			}
			if (backBtn.Child is Grid g2)
			{
				g2.Children.Clear();
				g2.Children.Add(IslandIcons.Chevron(IconBrush(), 12));
			}
			if (prevBtn != null && prevBtn.Child is Grid g3)
			{
				g3.Children.Clear();
				g3.Children.Add(IslandIcons.Previous(IconBrush(), 18));
			}
			if (nextBtn != null && nextBtn.Child is Grid g4)
			{
				g4.Children.Clear();
				g4.Children.Add(IslandIcons.Next(IconBrush(), 18));
			}
			if (playBtn != null && playBtn.Child is Grid g5)
			{
				g5.Children.Clear();
				g5.Children.Add(M != null && M.Playing ? IslandIcons.Pause(PlayIconBrush(), 20) : IslandIcons.Play(PlayIconBrush(), 20));
			}
			if (prevBtn != null)
			{
				prevBtn.Background = ChipBrush();
			}
			if (nextBtn != null)
			{
				nextBtn.Background = ChipBrush();
			}
			if (playBtn != null)
			{
				playBtn.Background = PlayButtonBrush();
			}
			if (progressSlider != null)
			{
				progressSlider.Style = BuildProgressSliderStyle();
			}
			if (volumeSlider != null)
			{
				volumeSlider.Style = BuildProgressSliderStyle();
			}
			if (brightnessSlider != null)
			{
				brightnessSlider.Style = BuildProgressSliderStyle();
			}
			if (outputCombo != null)
			{
				outputCombo.Style = BuildComboStyle();
				outputCombo.ItemContainerStyle = BuildComboItemStyle();
			}
			if (pcBatCard != null)
			{
				pcBatCard.Background = CardBrush();
			}
			if (mouseBatCard != null)
			{
				mouseBatCard.Background = CardBrush();
			}
			if (kbBatCard != null)
			{
				kbBatCard.Background = CardBrush();
			}
			if (pcBatText != null)
			{
				pcBatText.Foreground = TextBrush;
			}
			if (mouseBatText != null)
			{
				mouseBatText.Foreground = TextBrush;
			}
			if (kbBatText != null)
			{
				kbBatText.Foreground = TextBrush;
			}
			RefreshBatteryRow();
			if (volumeIconHost != null)
			{
				volumeIconHost.Child = IslandIcons.Speaker(IconBrush(), 16);
			}
			if (brightnessIconHost != null)
			{
				// 亮度图标此前是局部变量，主题切换后不会被重新着色（"亮度图标颜色不对"根因）
				brightnessIconHost.Child = IslandIcons.PathIcon("M8 12 a4 4 0 1 1 8 0 a4 4 0 1 1 -8 0 M12 2 v2 M12 20 v2 M4.93 4.93 l1.41 1.41 M17.66 17.66 l1.41 1.41 M2 12 h2 M20 12 h2 M6.34 17.66 l-1.41 1.41 M19.07 4.93 l-1.41 1.41", IconBrush(), 16, fill: false, strokeWidth: 1.6);
			}
			if (outputIconHost != null)
			{
				outputIconHost.Child = IslandIcons.PathIcon("M4 9.5 V14.5 H7.5 L12.5 19 V5 L7.5 9.5 Z M15.5 8.8 A3.8 3.8 0 0 1 15.5 15.2 M17.5 6.8 A6.5 6.5 0 0 1 17.5 17.2", MutedBrush, 16, fill: false, strokeWidth: 1.6);
			}
			if (wifiTileName != null)
			{
				wifiTileName.Foreground = TextBrush;
			}
			if (btTileName != null)
			{
				btTileName.Foreground = TextBrush;
			}
			if (wifiStatus != null)
			{
				wifiStatus.Foreground = MutedBrush;
			}
			if (btStatus != null)
			{
				btStatus.Foreground = MutedBrush;
			}
			if (outputLabel != null)
			{
				outputLabel.Foreground = TextBrush;
			}
			if (controlNote != null)
			{
				controlNote.Foreground = MutedBrush;
			}
			RestyleToast();
		}
		catch
		{
		}
	}

// 事件卡样式完全跟随灵动岛外观：事件卡不再自带独立卡片背景与四个圆角—�?
// 通知出现时是小岛“变身”，内容直接渲染在小岛自身的磨砂面板上，
// 外形（胶�?圆角矩形、圆角值）�?Surface 提供，尺寸与收起条一致�?
private void RestyleToast()
	{
		try
		{
			double fs = O.FontSize;
			toastCard.Background = Brushes.Transparent;
			toastCard.CornerRadius = new CornerRadius(0);
			toastCard.Padding = new Thickness(0);
			double icon = Math.Max(24.0, Math.Min(O.IconSize + 8.0, O.CollapsedHeight - 30.0));
			toastIcon.Width = icon;
			toastIcon.Height = icon;
			toastIcon.CornerRadius = new CornerRadius(Math.Max(6.0, icon / 3.0));
			toastIcon.Margin = new Thickness(0, 0, 10, 0);
			toastIconText.FontSize = Math.Max(11.0, icon * 0.44);
			toastApp.FontSize = Math.Max(10.0, fs - 3.0);
			toastApp.FontWeight = FontWeights.SemiBold;
			toastTitle.FontSize = fs + 0.5;
			toastTitle.FontWeight = FontWeights.SemiBold;
			toastBody.FontSize = Math.Max(10.0, fs - 1.0);
			toastBody.MaxHeight = 20;
			if (toastCard.Child is Grid tg)
			{
				tg.Margin = new Thickness(8, 4, 8, 4);
			}
			toastIcon.Background = ChipBrush();
			toastIconText.Foreground = TextBrush;
			toastApp.Foreground = MutedBrush;
			toastTitle.Foreground = TextBrush;
			toastBody.Foreground = MutedBrush;
		}
		catch
		{
		}
	}

	private void RebuildTabs()
	{
		tabIcons = new Dictionary<int, Func<Brush, FrameworkElement>>
		{
			{ 0, br => IslandIcons.TabMusic(br, 12) },
			{ 1, br => IslandIcons.TabBell(br, 12) },
			{ 2, br => IslandIcons.TabClipboard(br, 12) },
			{ 3, br => IslandIcons.TabControl(br, 12) },
			{ 4, br => IslandIcons.TabNote(br, 12) }
		};
		tabBar.Children.Clear();
		if (O.ShowMusicPage)
		{
			tabBar.Children.Add(TabButton("正在播放", 0));
		}
		if (O.ShowNotificationsPage)
		{
			tabBar.Children.Add(TabButton("通知", 1));
		}
		if (O.ClipboardEnabled)
		{
			tabBar.Children.Add(TabButton("剪贴板", 2));
		}
		if (O.ShowControlPage)
		{
			tabBar.Children.Add(TabButton("控制中心", 3));
		}
		tabBar.Children.Add(TabButton("随手记", 4));
		if (tabBar.Children.Count == 0)
		{
			tabBar.Children.Add(TabButton("正在播放", 0));
		}
	}

	// —�?Hub 驱动的数据更�?—�?
	public void RefreshBattery()
	{
		BatteryCore.BatteryState s = BatteryCore.Current;
		if (s.PcLevel.HasValue)
		{
			if (s.PcLevel.Value <= O.LowBatteryThreshold && s.PcCharging != true)
			{
				if (lowBatteryArmed && mode != Mode.Expanded)
				{
					lowBatteryArmed = false;
					ShowBatteryToast("电脑电量低", "剩余电量 " + (int)Math.Round(s.PcLevel.Value) + "%，请及时充电");
					return;
				}
			}
			else
			{
				lowBatteryArmed = true;
			}
		}
// 收起态已无电量显示：电量变化只驱动低电量事件卡与控制页电量区。
		if (mode == Mode.Expanded && currentPage == 3)
		{
			RunWhenIdle(delegate
			{
				if (mode == Mode.Expanded && currentPage == 3)
				{
					RefreshBatteryRow();
				}
			});
		}
	}

	public void RefreshMusic()
	{
		if (M == null)
		{
			// 音乐引擎已销毁（桌面组件与灵动岛都不再需要音乐）：
			// 清掉残留的收起条音乐区/展开音乐页，避免显示旧状态。
			if (mode == Mode.Collapsed)
			{
				RunWhenIdle(RebuildCollapsed);
			}
			else if (mode == Mode.Expanded)
			{
				RunWhenIdle(delegate
				{
					RefreshMusicPage(animate: false);
				});
			}
			return;
		}
		string key = M.Title + "|" + M.Artist;
		bool placeholder = string.IsNullOrEmpty(M.Title) || M.Title == "等待音乐" || M.Title == "选择本地音乐";
		bool changed = key != lastTrackKey;
		bool trackSwitched = false;
		bool playingChanged = M.Playing != lastPlaying;
		lastPlaying = M.Playing;
		string sessKey = (M.Session != null && M.Session.Key != null) ? M.Session.Key : "";
		bool sessChanged = sessKey != lastSessionKey;
		lastTrackKey = key;
		lastSessionKey = sessKey;
		lastMusicTick = DateTime.Now;
		double lyricAge = (lyricStartUtc == DateTime.MinValue) ? 0.0 : (DateTime.Now - lyricStartUtc).TotalSeconds;
		if (changed && !placeholder)
		{
			if (key == lastRealTrackKey && lyricAge > 5.0)
			{
				// 标题短暂丢失后恢复为同一首歌（播放器内部跳转/会话重启引起的一过性空缺）：
				// 歌词位置已不可知。不重置时钟、不显示错位歌词——改为显示歌手名。
				lyricUntrusted = true;
			}
			else
			{
				// 真正切歌：重置歌词与跳动条运动状态，重新计时（无时间线播放器用）
				lastRealTrackKey = key;
				trackSwitched = true;
				IslandLyrics.Reset(M.Artist, M.Title);
				lyricStartUtc = DateTime.Now;
				lyricUntrusted = false;
				for (int i = 0; i < eqCap.Length; i++)
				{
					eqCap[i] = 0.0;
					eqSpring[i] = 0.35;
					eqVel[i] = 0.0;
				}
			}
		}
		else if (sessChanged && !placeholder && !changed && M.Playing && M.Duration <= 0 && key == lastRealTrackKey && lyricAge > 5.0)
		{
			// 无时间线播放器（如网易云桌面版）在播放器内部拖动进度时，媒体会话会重启，
			// 但曲目不变、仍在播放：歌词位置已不可知，改为显示歌手名，绝不显示错位歌词。
			lyricUntrusted = true;
		}
		if (mode == Mode.Collapsed)
		{
			// 只有布局类别真正变化（切歌/播放暂停切换）才重建收起条：
			// 媒体会话每次轮询都会发 Changed（位置推进），若每次都重建，
			// 会不断重置“动态建议”的轮播时钟导致其永远不切换。
			if (trackSwitched || playingChanged)
			{
				RunWhenIdle(delegate
				{
					RebuildCollapsed();
					FadeInContent(collapsedStack);
				});
			}
		}
		if (mode == Mode.Expanded)
		{
			RunWhenIdle(delegate
			{
				RefreshMusicPage(trackSwitched);
			});
		}
		if (changed && coverBox != null && M.Artwork != null && O.AnimationEnabled)
		{
			coverBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(300 / O.ExpandSpeed)));
		}
	}

	public void NotifyArrived(IslandNotification n)
	{
		suggestionPool = new string[0]; // 未读数变化：建议池下轮立即重建
		// 收起态铃铛红点恒显示（开关已移除）。
		if (bellDot != null)
		{
			RunWhenIdle(delegate
			{
				bellDot.Visibility = Visibility.Visible;
				if (O.AnimationEnabled)
				{
					ScaleTransform scale = new ScaleTransform(1, 1);
					bellBadge.RenderTransform = scale;
					scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.35, TimeSpan.FromMilliseconds(140)) { AutoReverse = true });
					scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.35, TimeSpan.FromMilliseconds(140)) { AutoReverse = true });
				}
			});
		}
		if (mode == Mode.Expanded)
		{
			if (currentPage == 1)
			{
				RunWhenIdle(RefreshNotificationsPage);
			}
			return; // 展开时事件不强制关闭面板
		}
		if (n == null)
		{
			return;
		}
		bool muted = O.MutedApps.Any((string a) =>
		{
// MutedApps 存的�?AUMID（设置页按应用控制）；优先精确匹�?AUMID，其次按显示名匹�?


string aumid = (n.Aumid ?? "").ToLowerInvariant();
			string key = (a ?? "").Trim().ToLowerInvariant();
			if (key.Length == 0)
			{
				return false;
			}
			if (aumid.Length > 0)
			{
				return aumid == key || aumid.StartsWith(key, StringComparison.Ordinal) || key.StartsWith(aumid, StringComparison.Ordinal);
			}
			return n.AppName.ToLowerInvariant().Contains(key) || key.Contains(n.AppName.ToLowerInvariant());
		});
		if (!muted)
		{
// 隐藏正文/隐私模式：不弹事件卡，只保留铃铛红点与通知页记录�?


if (O.HideNotifyBody || IslandClipboard.PrivacyActive)
			{
				return;
			}
			Store.Trace("ISLAND notif mode=" + mode + " t=" + (n.Title ?? "").Length + " b=" + (n.Body ?? "").Length);
			ShowToast(n.AppName, n.Title, n.Body, n.Aumid);
		}
	}

	public void NotifyCleared()
	{
		suggestionPool = new string[0]; // 未读数变化：建议池下轮立即重建
		if (bellDot != null)
		{
			bellDot.Visibility = Visibility.Collapsed;
		}
		if (mode == Mode.Expanded && currentPage == 1)
		{
			RefreshNotificationsPage();
		}
	}

// 天气数据变化：仅在收起态且处于“无音乐默认布局”时重建（时�?刷新�?天气行）�?
public void RefreshWeather()
	{
		if (mode == Mode.Collapsed && collapsedTime != null)
		{
			suggestionPool = new string[0];
			RunWhenIdle(RebuildCollapsed);
		}
	}

	public void DisposeBridge()
	{
		try
		{
			bridge?.Dispose();
		}
		catch
		{
		}
		bridge = null;
	}

	private void OnSourceInitialized(object sender, EventArgs e)
	{
		IntPtr hwnd = new WindowInteropHelper(this).Handle;
		SetNoActivate(true);
		// 额外的消息钩子：阻止鼠标点击激活（收起态），全�?显示器变化时重新定位
		HwndSource source = HwndSource.FromHwnd(hwnd);
		source?.AddHook(WndProcHook);
	}

	private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == WM_MOUSEACTIVATE && mode != Mode.Expanded)
		{
			handled = true;
			return new IntPtr(3); // MA_NOACTIVATE
		}
		if (msg == 0x007E || msg == 0x02E0)
		{
			// 显示器/DPI 变化：重新定位并原地重读壁纸（展开区域底图由 ArmWallpaperRedo 补齐）
			Dispatcher.BeginInvoke(new Action(delegate
			{
				if (mode != Mode.Hidden)
				{
					double w = mode == Mode.Expanded ? MeasureExpandedWidth() : O.CollapsedWidth;
					double h = mode == Mode.Expanded ? MeasureExpandedHeight() : O.CollapsedHeight;
					PositionAnimated(w, h);
					ArmWallpaperRedo();
				}
			}), DispatcherPriority.ApplicationIdle);
		}
		if (msg == 0x001A && wParam.ToInt64() == 0x14)
		{
			// 壁纸变化：基类约 1.2s 后按收起矩形原地重读壁纸；这里延后 4s 再把“展开区域”的
			// 全尺寸底图补回来（原地完成、不隐藏窗口），保证展开后背景仍是 1:1 清晰壁纸
			ArmWallpaperRedo();
		}
		return IntPtr.Zero;
	}

	private void ArmWallpaperRedo()
	{
		if (wallpaperRedo == null)
		{
			wallpaperRedo = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.0) };
			wallpaperRedo.Tick += delegate
			{
				wallpaperRedo.Stop();
				try
				{
					if (mode == Mode.Hidden)
					{
						return;
					}
					// 收起态：基类管线原地重读壁纸（不移窗、无闪烁）；
					// 展开态：灵动岛自身管线。展开区底图随基类重捕标记过期，展开前补齐。
					RecaptureBackdrop(LightTheme);
				}
				catch
				{
				}
			};
		}
		wallpaperRedo.Stop();
		wallpaperRedo.Start();
	}

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
	private static extern int GetWindowLongW(IntPtr hwnd, int index);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

	private const int GWL_STYLE = -16;
	private const long WS_MAXIMIZE = 0x01000000;

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, IntPtr pTimingInfo);

// 实时屏幕刷新率：DWM 合成刷新率（uiNumerator/uiDenominator 分数，低 32 �?分子）�?
// 跟随显示器与省电/VRR 策略实时变化；DWM 不可用时回退 GDI EnumDisplaySettings �?dmDisplayFrequency�?
// 两者都不可用返�?0（对应显示项隐藏）�?
internal static double CurrentRefreshRate()
	{
		try
		{
			IntPtr buf = Marshal.AllocHGlobal(400);
			try
			{
				Marshal.WriteInt32(buf, 400);
				if (DwmGetCompositionTimingInfo(IntPtr.Zero, buf) != 0)
				{
					return FallbackRefreshRate();
				}
				long rate = Marshal.ReadInt64(buf, 4);
				uint num = (uint)(rate & 0xFFFFFFFF);
				uint den = (uint)(rate >> 32);
				if (num == 0 || den == 0)
				{
					return FallbackRefreshRate();
				}
				double hz = (double)num / (double)den;
				return hz >= 1.0 ? hz : FallbackRefreshRate();
			}
			finally
			{
				Marshal.FreeHGlobal(buf);
			}
		}
		catch
		{
			return FallbackRefreshRate();
		}
	}

	private static double FallbackRefreshRate()
	{
		try
		{
			var dm = new Native.DEVMODE
			{
				dmSize = (ushort)Marshal.SizeOf(typeof(Native.DEVMODE))
			};
			if (EnumDisplaySettingsW(null, -1, ref dm) && dm.dmDisplayFrequency > 1)
			{
				return dm.dmDisplayFrequency;
			}
		}
		catch
		{
		}
		return 0;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref Native.DEVMODE devMode);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
	private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
