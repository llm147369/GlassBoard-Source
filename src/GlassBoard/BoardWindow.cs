using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GlassBoard;

public class BoardWindow : GlassWindow
{
	public bool ModalBusy;

	private Controller C;

	private TextBox Search;
	private Popup searchPopup;
	private ListBox searchList;
	private CancellationTokenSource searchCts;
	private int searchSerial;
	private List<SystemSearchResult> searchItems = new List<SystemSearchResult>();

	private UniformGrid Tiles;

	// 滚动模式：磁贴宿主滚动视图与当前文件夹视图（"" = 根目录）
	private ScrollViewer tileScroller;
	private string currentFolder = "";
	private WrapPanel folderBar;

	private TextBlock Status;

	private TextBlock Empty;

	private Grid layout;

	private int columns = 7;

	private Point dragStart;

	private Entry dragEntry;

	// 搜索栏宿主边框：设置里「隐藏大面板搜索栏」开关控制其可见性
	private FrameworkElement searchHost;

	private bool editing;

	private readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private WrapPanel bulkBar;

	private RowDefinition bulkRow;

	// 媒体悬浮预览：图片显示大图，视频静音播放（任务栏缩略图风格）
	private Popup mediaPreviewPopup;
	private DispatcherTimer mediaPreviewTimer;
	private Entry mediaPreviewEntry;
	private FrameworkElement mediaPreviewAnchor;
	private MediaElement mediaPreviewPlayer;

	// 图标拖动排序：插入位置指示条（渲染于 Tiles 之上的竖线）
	private Border insertMarker;
	private int insertIndex = -1;
	// 拖动实时预览：其他图标滑开让位，被拖图标半透明占位
	private bool livePreview;
	private int liveIndex = -1;

	private TextBlock selectionCount;

	private Button selectAll;

	private Button clearSelection;

	private Button deleteSelected;

	private Button editSelection;

	private ContextMenu iconMenu;

	private Thumb moveGrip;

	private Thumb resizeGrip;

	private int visibilitySerial;
	private DispatcherTimer trimTimer;

	private readonly DispatcherTimer idleRelease = new DispatcherTimer
	{
		Interval = TimeSpan.FromSeconds(2.0)
	};

	private bool preparingView;

	private string pendingStatus;

	private Border dragPreview;

	private Image dragPreviewIcon;

	private TextBlock dragPreviewText;

	private bool activeDrag;

	private readonly DispatcherTimer sizeRefresh = new DispatcherTimer(DispatcherPriority.Background)
	{
		Interval = TimeSpan.FromMilliseconds(100.0)
	};

	private bool resizingPanel;

	private bool refreshingSize;

	private bool iconsDirty = true;

	internal bool HasView => layout != null;

	public void ClearSearch()
	{
		if (Search != null)
		{
			Search.Clear();
		}
	}

	public BoardWindow(Controller c)
		: base(42.0)
	{
		C = c;
		Title = "GlassBoard-Source 应用启动台";
		AllowDrop = true;
		// 点击搜索框以外的区域时收起键盘焦点，输入光标不再一直闪烁
		Root.PreviewMouseDown += delegate
		{
			if (Search != null && Search.IsKeyboardFocusWithin && !Search.IsMouseOver)
			{
				Keyboard.ClearFocus();
			}
		};
		// 媒体预览打开期间：光标离开磁贴与预览的合并区域才收起，避免悬浮抖动反复开关
		MouseMove += delegate
		{
			if (mediaPreviewPopup != null && mediaPreviewPopup.IsOpen && !IsCursorOverMediaPreview())
			{
				CancelMediaPreview();
			}
		};
		InitDragExperience();
		IsVisibleChanged += (object s, DependencyPropertyChangedEventArgs e) =>
		{
			if (IsVisible)
			{
				idleRelease.Stop();
			}
			else if (layout != null)
			{
				idleRelease.Start();
			}
		};
		idleRelease.Tick += (object s, EventArgs e) =>
		{
			if (IsVisible)
			{
				idleRelease.Stop();
			}
			else if (ReleaseHiddenView())
			{
				idleRelease.Stop();
			}
		};
		Closed += (object s, EventArgs e) =>
		{
			idleRelease.Stop();
			sizeRefresh.Stop();
		};
		PreviewKeyDown += (object s, KeyEventArgs e) =>
		{
			if (Search != null)
			{
				if (e.Key == Key.Escape)
				{
					CollapseSmooth();
					e.Handled = true;
				}
				else if (!Search.IsKeyboardFocusWithin && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.Z || e.Key == Key.Y))
				{
					if (e.Key == Key.Y || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
					{
						C.RedoBoard();
					}
					else
					{
						C.UndoBoard();
					}
					e.Handled = true;
				}
				else if (editing && !Search.IsKeyboardFocusWithin && e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
				{
					SelectFiltered();
					e.Handled = true;
				}
				else if (editing && !Search.IsKeyboardFocusWithin && e.Key == Key.Delete)
				{
					C.RemoveMany(selected.ToArray());
					e.Handled = true;
				}
				else if (e.Key == Key.Next)
				{
					tileScroller?.PageDown();
					e.Handled = true;
				}
				else if (e.Key == Key.Prior)
				{
					tileScroller?.PageUp();
					e.Handled = true;
				}
				else if (e.Key == Key.Return && Search.IsKeyboardFocusWithin && !editing)
				{
					if (searchItems.Count > 0)
					{
						OpenSearchResult(searchItems[0]);
						e.Handled = true;
					}
				}
			}
		};
		Drop += ExternalDrop;
		Deactivated += (object s, EventArgs e) =>
		{
			Dispatcher.BeginInvoke((Action)(() =>
			{
				if (C.Prefs.BoardDismissOnDeactivate && IsVisible && !IsActive && !ModalBusy && !activeDrag && !IsContextMenuOpen() && !ShellContextMenu.Active && Mouse.LeftButton != MouseButtonState.Pressed)
				{
					CollapseSmooth();
				}
			}));
		};
		moveGrip = new Thumb
		{
			Width = 60.0,
			Height = 8.0,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
			Cursor = Cursors.SizeAll,
			ToolTip = "拖动面板位置"
		};
		Root.Children.Add(moveGrip);
		resizeGrip = new Thumb
		{
			Width = 20.0,
			Height = 20.0,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(0.0, 0.0, 5.0, 5.0),
			Cursor = Cursors.SizeNWSE,
			ToolTip = "拖动调整面板大小"
		};
		Root.Children.Add(resizeGrip);
		moveGrip.Template = GlassWindow.GripTemplate();
		resizeGrip.Template = GlassWindow.GripTemplate(corner: true);
		Closing += (object s, CancelEventArgs e) =>
		{
			if (!C.ShuttingDown)
			{
				e.Cancel = true;
				CollapseSmooth();
			}
		};
		moveGrip.DragStarted += (object s, DragStartedEventArgs e) =>
		{
			ModalBusy = true;
		};
		moveGrip.DragDelta += (object s, DragDeltaEventArgs e) =>
		{
			if (!C.Prefs.Placement.PanelLocked)
			{
				Rect workArea = SystemParameters.WorkArea;
				Left = FeatureRules.Position(Left + e.HorizontalChange, workArea.Left, workArea.Width, Width);
				Top = FeatureRules.Position(Top + e.VerticalChange, workArea.Top, workArea.Height, Height);
			}
		};
		InitResizeRefresh();
		resizeGrip.DragStarted += (object s, DragStartedEventArgs e) =>
		{
			ModalBusy = true;
			resizingPanel = true;
			sizeRefresh.Stop();
		};
		resizeGrip.DragDelta += (object s, DragDeltaEventArgs e) =>
		{
			if (!C.Prefs.Placement.PanelLocked)
			{
				Rect workArea = SystemParameters.WorkArea;
				Width = Math.Max(Math.Min(860.0, workArea.Width - 32.0), Math.Min(workArea.Width - 32.0, Width + e.HorizontalChange));
				Height = Math.Max(Math.Min(560.0, workArea.Height - 32.0), Math.Min(workArea.Height - 32.0, Height + e.VerticalChange));
				Left = FeatureRules.Position(Left, workArea.Left, workArea.Width, Width);
				Top = FeatureRules.Position(Top, workArea.Top, workArea.Height, Height);
			}
		};
		moveGrip.DragCompleted += (object s, DragCompletedEventArgs e) =>
		{
			try
			{
				SavePlacement();
				Backdrop(C.Prefs.Light);
			}
			finally
			{
				ModalBusy = false;
			}
		};
		resizeGrip.DragCompleted += (object s, DragCompletedEventArgs e) =>
		{
			try
			{
				resizingPanel = false;
				SavePlacement();
				QueueSizeRefresh();
				Backdrop(C.Prefs.Light);
			}
			finally
			{
				ModalBusy = false;
			}
		};
	}

	private void SavePlacement()
	{
		PlacementOptions placement = C.Prefs.Placement;
		placement.PanelLeft = Left;
		placement.PanelTop = Top;
		placement.PanelWidth = Width;
		placement.PanelHeight = Height;
		C.Save();
	}

	private void BuildForSize()
	{
		string text = ((C.Prefs.BoardHideSearch || Search == null) ? "" : Search.Text);
		columns = BoardIconRules.Columns(Width - 104.0, C.Prefs.BoardIconSize, C.Prefs.BoardIconGap);
		Build();
		Search.Text = text;
	}

	public void RefreshIcons()
	{
		iconsDirty = true;
		QueueSizeRefresh();
	}

	private void IconSettings()
	{
		ContextMenu menu = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.BoardMaterial.Tint, (int)(C.Prefs.OpacityPercent ?? 72.0));
		iconMenu = menu;
		menu.Closed += (object s, RoutedEventArgs e) =>
		{
			if (iconMenu == menu)
			{
				iconMenu = null;
			}
		};
		Action<string, double, double, double, Action<double>> action = (string name, double min, double max, double value, Action<double> change) =>
		{
			StackPanel stackPanel = new StackPanel
			{
				Width = 230.0
			};
			TextBlock label = new TextBlock
			{
				Text = name + "：" + Math.Round(value) + " px",
				Foreground = TextBrush,
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
			};
			stackPanel.Children.Add(label);
			Slider slider = new Slider
			{
				Minimum = min,
				Maximum = max,
				Value = value,
				TickFrequency = 1.0,
				IsSnapToTickEnabled = true,
				Style = (Resources[typeof(Slider)] as Style)
			};
			stackPanel.Children.Add(slider);
			slider.ValueChanged += (object s, RoutedPropertyChangedEventArgs<double> e) =>
			{
				label.Text = name + "：" + Math.Round(slider.Value) + " px";
				change(slider.Value);
				RefreshIcons();
				C.Save();
			};
			menu.Items.Add(new MenuItem
			{
				Header = stackPanel,
				StaysOpenOnClick = true
			});
		};
		action("图标大小", 28.0, 80.0, C.Prefs.BoardIconSize, (double v) =>
		{
			C.Prefs.BoardIconSize = v;
		});
		action("图标间距", 0.0, 20.0, C.Prefs.BoardIconGap, (double v) =>
		{
			C.Prefs.BoardIconGap = v;
		});
		MenuItem menuItem = new MenuItem
		{
			Header = "恢复默认"
		};
		menuItem.Click += (object s, RoutedEventArgs e) =>
		{
			C.Prefs.BoardIconSize = 48.0;
			C.Prefs.BoardIconGap = 5.0;
			RefreshIcons();
			C.Save();
		};
		menu.Items.Add(menuItem);
		menu.PlacementTarget = this;
		menu.IsOpen = true;
	}

	public void CollapseSmooth()
	{
		if (!IsVisible)
		{
			return;
		}
		int serial = ++visibilitySerial;
		ScheduleTrim();
		if (!MotionEnabled)
		{
			Hide();
			return;
		}
		DoubleAnimation fade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(110.0))
		{
			EasingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseIn
			}
		};
		fade.Completed += (object s, EventArgs e) =>
		{
			if (serial == visibilitySerial)
			{
				Hide();
			}
		};
		BeginAnimation(UIElement.OpacityProperty, fade);
	}

	// 折叠后延迟 3 秒再整理内存：避免刚收起又打开时 GC/TrimWorkingSet 卡住首帧
	private void ScheduleTrim()
	{
		if (trimTimer == null)
		{
			trimTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(3.0)
			};
			trimTimer.Tick += (object s, EventArgs e) =>
			{
				trimTimer.Stop();
				try
				{
					GC.Collect(2, GCCollectionMode.Optimized, false);
					Native.TrimWorkingSet();
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			};
		}
		trimTimer.Stop();
		trimTimer.Start();
	}

	internal bool ReleaseHiddenView()
	{
		if (IsVisible || preparingView || ModalBusy || activeDrag || IsMouseCaptureWithin)
		{
			return false;
		}
		sizeRefresh.Stop();
		if (iconMenu != null)
		{
			iconMenu.IsOpen = false;
		}
		iconMenu = null;
		EndDragPreview();
		ContentGrid.Children.Clear();
		ReleaseBackdrop();
		Search = null;
		Tiles = null;
		tileScroller = null;
		folderBar = null;
		Status = (Empty = null);
		layout = null;
		bulkBar = null;
		bulkRow = null;
		selectionCount = null;
		selectAll = (clearSelection = (deleteSelected = (editSelection = null)));
		dragEntry = null;
		return true;
	}

	private bool IsContextMenuOpen()
	{
		if (iconMenu == null || !iconMenu.IsOpen)
		{
			if (Tiles != null)
			{
				return Tiles.Children.OfType<FrameworkElement>().Any((FrameworkElement x) => x.ContextMenu != null && x.ContextMenu.IsOpen);
			}
			return false;
		}
		return true;
	}

	public void Open()
	{
		idleRelease.Stop();
		if (trimTimer != null)
		{
			trimTimer.Stop();
		}
		preparingView = true;
		Stopwatch openTimer = Stopwatch.StartNew();
		long buildMs = 0L;
		long backdropMs = 0L;
		try
		{
			visibilitySerial++;
			Rect workArea = SystemParameters.WorkArea;
			double num = ((C.Prefs.PanelSize == 0) ? 980 : 1280);
			double num2 = ((C.Prefs.PanelSize == 0) ? 620 : 730);
			if (C.Prefs.PanelSize == 2)
			{
				num = workArea.Width - 32.0;
				num2 = workArea.Height - C.Dock.Height - 40.0;
			}
			PlacementOptions placement = C.Prefs.Placement;
			Width = Math.Min(placement.PanelWidth ?? num, workArea.Width - 32.0);
			Height = Math.Min(placement.PanelHeight ?? num2, workArea.Height - 32.0);
			Height = Math.Max(Math.Min(400.0, workArea.Height - 130.0), Height);
			Width = Math.Max(Math.Min(860.0, workArea.Width - 32.0), Width);
			Height = Math.Max(Math.Min(560.0, workArea.Height - 32.0), Height);
			Left = FeatureRules.Position(placement.PanelLeft ?? (workArea.Left + (workArea.Width - Width) / 2.0), workArea.Left, workArea.Width, Width);
			Top = FeatureRules.Position(placement.PanelTop ?? (workArea.Top + (workArea.Height - C.Dock.Height - Height) / 2.0), workArea.Top, workArea.Height, Height);
			Thumb thumb = moveGrip;
			Visibility visibility = (resizeGrip.Visibility = (placement.PanelLocked ? Visibility.Collapsed : Visibility.Visible));
			thumb.Visibility = visibility;
			editing = false;
			selected.Clear();
			currentFolder = "";
			LightTheme = C.Prefs.Light;
			MotionEnabled = C.Prefs.Dock.MotionEnabled;
			MotionLevel = C.Prefs.Dock.MotionLevel;
			ConfigureGlass(C.Prefs.Dock, C.Prefs.BoardMaterial);
			GlassPercent = C.Prefs.OpacityPercent ?? 72.0;
			Polish.Apply(this, LightTheme);
			BuildForSize();
			buildMs = openTimer.ElapsedMilliseconds;
			Backdrop(C.Prefs.Light);
			backdropMs = openTimer.ElapsedMilliseconds - buildMs;
			Show();
			AnimateOpen();
			Keyboard.ClearFocus();
			// 激活窗口放到开启动画之后（下一空闲帧）：SetForegroundWindow 的 shell 往返
			// 在部分系统上会阻塞几十毫秒，原本夹在 Show 与动画之间正是"动画开头停一帧"的来源。
			Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
			{
				try
				{
					Activate();
					Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
				}
				catch
				{
				}
			}));
			// 打开耗时诊断：build 为 UI 树重建（含磁贴），backdrop 为背景捕获（缓存命中近 0）
			Store.Trace("BOARD open total=" + openTimer.ElapsedMilliseconds + "ms build=" + buildMs + "ms backdrop=" + backdropMs + "ms");
		}
		finally
		{
			preparingView = false;
		}
	}

	private void Build()
	{
		ContentGrid.Children.Clear();
		layout = new Grid
		{
			Margin = new Thickness(38.0, 22.0, 38.0, 20.0)
		};
		layout.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(55.0)
		});
		layout.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		bulkRow = new RowDefinition
		{
			Height = new GridLength(0.0)
		};
		layout.RowDefinitions.Add(bulkRow);
		layout.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		layout.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(1.0, GridUnitType.Star)
		});
		layout.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(32.0)
		});
		ContentGrid.Children.Add(layout);
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(116.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(80.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(40.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(40.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(44.0)
		});
		layout.Children.Add(grid);
		Border border = new Border
		{
			CornerRadius = new CornerRadius(16.0),
			BorderThickness = new Thickness(1.0),
			BorderBrush = new SolidColorBrush(Color.FromArgb(90, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			Background = new SolidColorBrush(Color.FromArgb(25, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			ClipToBounds = true,
			Height = 36.0,
			Margin = new Thickness(8.0, 4.0, 8.0, 4.0)
		};
		Grid grid2 = (Grid)(border.Child = new Grid());
		// 放大镜与提示文字放在同一个水平 StackPanel 里，紧贴输入框左侧：
		// 图标在前、文字在后，两者高度一致，彻底避免对齐歧义。
		StackPanel searchHint = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(15.0, 0.0, 0.0, 0.0),
			IsHitTestVisible = false
		};
		Viewbox magnifierBox = new Viewbox
		{
			Width = 14.0,
			Height = 14.0,
			Stretch = Stretch.Uniform,
			VerticalAlignment = VerticalAlignment.Center
		};
		magnifierBox.Child = new System.Windows.Shapes.Path
		{
			Data = Geometry.Parse("M 2.9,2.9 m -4.3,0 a 4.3,4.3 0 1 0 8.6,0 a 4.3,4.3 0 1 0 -8.6,0 M 8.0,8.0 L 13.4,13.4"),
			Stroke = MutedBrush,
			StrokeThickness = 1.9,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			Width = 16.0,
			Height = 16.0,
			Stretch = Stretch.Uniform
		};
		TextBlock hint = new TextBlock
		{
			Text = "搜索系统应用、文件、文件夹…",
			FontSize = 15.0,
			Foreground = MutedBrush,
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		searchHint.Children.Add(magnifierBox);
		searchHint.Children.Add(hint);
		grid2.Children.Add(searchHint);
		Search = new TextBox
		{
			FontSize = 15.0,
			Foreground = TextBrush,
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			CaretBrush = TextBrush,
			VerticalContentAlignment = VerticalAlignment.Center,
			Padding = new Thickness(30.0, 0.0, 12.0, 0.0)
		};
		System.Windows.Media.Color idleBorder = Color.FromArgb(90, byte.MaxValue, byte.MaxValue, byte.MaxValue);
		System.Windows.Media.Color accentColor = IconPacks.AccentColor(C.Prefs.Dock.Accent);
		System.Windows.Media.Color focusBorder = Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B);
		Search.GotKeyboardFocus += delegate
		{
			border.BorderBrush = new SolidColorBrush(focusBorder);
		};
		Search.LostKeyboardFocus += delegate
		{
			border.BorderBrush = new SolidColorBrush(idleBorder);
		};
		Search.TextChanged += (object s, TextChangedEventArgs e) =>
		{
			hint.Visibility = ((Search.Text.Length != 0) ? Visibility.Hidden : Visibility.Visible);
			RunSearch();
		};
		grid2.Children.Add(Search);
		searchHost = border;
		searchHost.Visibility = (C.Prefs.BoardHideSearch ? Visibility.Collapsed : Visibility.Visible);
		Grid.SetColumn(border, 0);
		grid.Children.Add(border);
		BuildSearchPopup(border);
		Button element = Button("+ 批量添加", (object s, RoutedEventArgs e) =>
		{
			C.Choose();
		}, 106.0);
		Grid.SetColumn(element, 1);
		grid.Children.Add(element);
		editSelection = Button(editing ? "完成" : "多选", null, 68.0);
		editSelection.ToolTip = "跨页勾选，一次移除多个入口";
		editSelection.Click += (object s, RoutedEventArgs e) =>
		{
			SetSelectionMode(!editing);
			Search.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
			Render();
		};
		Grid.SetColumn(editSelection, 2);
		grid.Children.Add(editSelection);
		Button button = Button("⚙", (object s, RoutedEventArgs e) =>
		{
			C.Settings();
		}, 30.0);
		button.Content = CustomIcons.Settings(18.0, TextBrush);
		button.ToolTip = "外观、组件与快捷键";
		Grid.SetColumn(button, 3);
		grid.Children.Add(button);
		Button button2 = Button("···", (object s, RoutedEventArgs e) =>
		{
			MoreMenu();
		}, 32.0);
		button2.ToolTip = "更多与显示选项";
		Grid.SetColumn(button2, 4);
		grid.Children.Add(button2);
		Button element2 = Button("×", (object s, RoutedEventArgs e) =>
		{
			CollapseSmooth();
		}, 32.0);
		Grid.SetColumn(element2, 5);
		grid.Children.Add(element2);
		bulkBar = new WrapPanel
		{
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		Grid.SetRow(bulkBar, 2);
		layout.Children.Add(bulkBar);
		selectAll = Button("全选结果", (object s, RoutedEventArgs e) =>
		{
			SelectFiltered();
		}, 90.0);
		selectAll.ToolTip = "选中当前搜索结果的所有页；已有勾选会保留";
		bulkBar.Children.Add(selectAll);
		clearSelection = Button("取消全选", (object s, RoutedEventArgs e) =>
		{
			selected.Clear();
			Render();
		}, 90.0);
		bulkBar.Children.Add(clearSelection);
		deleteSelected = Button("移除已选", (object s, RoutedEventArgs e) =>
		{
			C.RemoveMany(selected.ToArray());
		}, 112.0);
		deleteSelected.Foreground = new SolidColorBrush(Color.FromRgb(byte.MaxValue, 155, 155));
		bulkBar.Children.Add(deleteSelected);
		selectionCount = new TextBlock
		{
			Foreground = MutedBrush,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0)
		};
		bulkBar.Children.Add(selectionCount);
		folderBar = new WrapPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
			Visibility = Visibility.Collapsed
		};
		Grid.SetRow(folderBar, 3);
		layout.Children.Add(folderBar);
		DockPanel dockPanel = new DockPanel
		{
			Margin = new Thickness(0.0, 10.0, 0.0, 4.0)
		};
		Grid.SetRow(dockPanel, 4);
		layout.Children.Add(dockPanel);
		Grid grid3 = new Grid();
		dockPanel.Children.Add(grid3);
		tileScroller = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			ClipToBounds = true,
			PanningMode = PanningMode.VerticalFirst,
			Background = Brushes.Transparent
		};
		grid3.Children.Add(tileScroller);
		Tiles = new UniformGrid
		{
			Columns = columns,
			// Rows 不设 = 自动按内容延展，配合 ScrollViewer 实现纵向滚动
			// 透明背景让空格子也能命中拖放事件（否则空白处会穿透到窗口层）
			Background = Brushes.Transparent,
			AllowDrop = true
		};
		tileScroller.Content = Tiles;
		tileScroller.AllowDrop = true;
		tileScroller.DragOver += (object s, DragEventArgs e) =>
		{
			// 磁贴排满时网格没有空位，滚到内容下方空白处仍可作为落点（追加到末尾）
			if (DraggedEntries(e.Data).Length != 0 && e.OriginalSource == tileScroller)
			{
				e.Effects = DragDropEffects.Move;
				e.Handled = true;
			}
		};
		tileScroller.Drop += (object s, DragEventArgs e) =>
		{
			if (DraggedEntries(e.Data).Length != 0 && e.OriginalSource == tileScroller)
			{
				HideInsertMarker();
				List<string> seq = CurrentPageSequence().Where((string p) => !DraggedEntries(e.Data).Any((string d) => string.Equals(d, p, StringComparison.OrdinalIgnoreCase))).ToList();
				seq.AddRange(DraggedEntries(e.Data));
				if (!C.ReorderView(seq.ToArray()))
				{
					Render();
				}
				e.Handled = true;
			}
		};
		Tiles.SizeChanged += (object s, SizeChangedEventArgs e) =>
		{
			QueueSizeRefresh();
		};
		// 容器级拖放：任意空格 / 末尾都可落点，悬停时其他图标实时让位；贴边自动滚动
		Tiles.DragOver += (object s, DragEventArgs e) =>
		{
			if (DraggedEntries(e.Data).Length != 0 && e.OriginalSource == Tiles)
			{
				AutoScrollFor(e.GetPosition(tileScroller));
				int idx = InsertIndexFor(e.GetPosition(Tiles));
				if (idx != liveIndex)
				{
					liveIndex = idx;
					ApplyLivePreview(DraggedEntries(e.Data), idx);
				}
				e.Effects = DragDropEffects.Move;
				e.Handled = true;
			}
		};
		Tiles.Drop += (object s, DragEventArgs e) =>
		{
			if (DraggedEntries(e.Data).Length != 0 && e.OriginalSource == Tiles)
			{
				HideInsertMarker();
				if (!C.ReorderView(CurrentPageSequence()))
				{
					Render();
				}
				e.Handled = true;
			}
		};
		Tiles.DragLeave += (object s, DragEventArgs e) =>
		{
			// 离开网格只收起指示线；让位布局保留，回到网格继续拖
			HideInsertMarker();
		};
		insertMarker = new Border
		{
			Width = 3.0,
			CornerRadius = new CornerRadius(1.5),
			Background = new SolidColorBrush(IconPacks.AccentColor(C.Prefs.Dock.Accent)),
			Visibility = Visibility.Collapsed,
			IsHitTestVisible = false,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			RenderTransform = new TranslateTransform()
		};
		grid3.Children.Add(insertMarker);
		Empty = new TextBlock
		{
			Text = "将软件、文档或文件夹拖到这里\n面板默认保持打开；按 Esc 收起，也可点击「批量添加」",
			TextAlignment = TextAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			Foreground = MutedBrush,
			FontSize = 19.0,
			LineHeight = 32.0
		};
		grid3.Children.Add(Empty);
		Grid grid4 = new Grid();
		Grid.SetRow(grid4, 5);
		layout.Children.Add(grid4);
		Status = new TextBlock
		{
			Text = "图标可拖到 Dock 固定 · Esc 收起 · ··· 显示选项",
			Foreground = MutedBrush,
			FontSize = 11.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center
		};
		grid4.Children.Add(Status);
		Render(animate: true);
	}

	private List<Entry> Filtered()
	{
		return C.Prefs.Apps.Where((Entry a) => string.Equals(a.Folder ?? "", currentFolder, StringComparison.OrdinalIgnoreCase)).ToList();
	}

	// 文件夹名列表（按首次出现顺序，忽略大小写去重）
	private List<string> FolderNames()
	{
		List<string> names = new List<string>();
		foreach (Entry a in C.Prefs.Apps)
		{
			if (!string.IsNullOrWhiteSpace(a.Folder) && !names.Any((string n) => string.Equals(n, a.Folder, StringComparison.OrdinalIgnoreCase)))
			{
				names.Add(a.Folder);
			}
		}
		return names;
	}

	private int FolderCount(string name)
	{
		return C.Prefs.Apps.Count((Entry a) => string.Equals(a.Folder ?? "", name, StringComparison.OrdinalIgnoreCase));
	}

	// 文件夹栏：根视图显示全部文件夹 chip + 「新建文件夹」落点；文件夹视图显示「← 返回」。
	private void RefreshFolderBar()
	{
		if (folderBar == null)
		{
			return;
		}
		folderBar.Children.Clear();
		if (currentFolder != "")
		{
			folderBar.Children.Add(FolderChip("← 返回", null));
			folderBar.Children.Add(FolderChip("📁 " + currentFolder + "（" + FolderCount(currentFolder) + "）", currentFolder));
		}
		else
		{
			foreach (string name in FolderNames())
			{
				folderBar.Children.Add(FolderChip("📁 " + name + "（" + FolderCount(name) + "）", name));
			}
		}
		folderBar.Visibility = ((folderBar.Children.Count > 0) ? Visibility.Visible : Visibility.Collapsed);
	}

	// 文件夹 chip：单击打开；拖入图标移入；右键打开 / 重命名 / 一键解散。
	private Border FolderChip(string text, string folderName)
	{
		Border chip = new Border
		{
			CornerRadius = new CornerRadius(14.0),
			Padding = new Thickness(12.0, 6.0, 12.0, 6.0),
			Margin = new Thickness(0.0, 0.0, 8.0, 6.0),
			Background = new SolidColorBrush(Color.FromArgb(35, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(60, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(1.0),
			Cursor = Cursors.Hand,
			AllowDrop = true
		};
		TextBlock label = new TextBlock
		{
			Text = text,
			Foreground = TextBrush,
			FontSize = 13.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		chip.Child = label;
		chip.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			OpenFolder(folderName);
		};
		chip.DragOver += (object s, DragEventArgs e) =>
		{
			if (DraggedEntries(e.Data).Length != 0)
			{
				e.Effects = DragDropEffects.Move;
				e.Handled = true;
			}
		};
		chip.Drop += (object s, DragEventArgs e) =>
		{
			string[] paths = DraggedEntries(e.Data);
			if (paths.Length == 0)
			{
				return;
			}
			MoveEntriesToFolder(paths, folderName);
			e.Handled = true;
		};
		chip.ContextMenu = FolderMenu(folderName);
		return chip;
	}

	private ContextMenu FolderMenu(string folderName)
	{
		if (folderName == null)
		{
			return null;
		}
		ContextMenu menu = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.BoardMaterial.Tint, (int)(C.Prefs.OpacityPercent ?? 72.0));
		MenuItem open = new MenuItem
		{
			Header = "打开"
		};
		open.Click += (object s, RoutedEventArgs e) => OpenFolder(folderName);
		menu.Items.Add(open);
		MenuItem rename = new MenuItem
		{
			Header = "重命名文件夹"
		};
		rename.Click += (object s, RoutedEventArgs e) =>
		{
			string name = FeatureUi.Ask(this, "重命名文件夹", folderName);
			if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name.Trim(), folderName, StringComparison.OrdinalIgnoreCase))
			{
				RenameFolder(folderName, name.Trim());
			}
		};
		menu.Items.Add(rename);
		MenuItem dissolve = new MenuItem
		{
			Header = "解散文件夹"
		};
		dissolve.Click += (object s, RoutedEventArgs e) => DissolveFolder(folderName);
		menu.Items.Add(dissolve);
		return menu;
	}

	// 进入 / 返回文件夹视图；Controller 的磁贴右键「放入文件夹」也会调用
	public void OpenFolder(string name)
	{
		currentFolder = (name ?? "");
		tileScroller?.ScrollToVerticalOffset(0.0);
		Render(animate: true);
	}

	private void MoveEntriesToFolder(string[] paths, string folderName)
	{
		bool changed = false;
		foreach (string p in paths)
		{
			Entry a = C.Prefs.Apps.FirstOrDefault((Entry x) => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase));
			if (a != null && !string.Equals(a.Folder ?? "", folderName ?? "", StringComparison.OrdinalIgnoreCase))
			{
				a.Folder = folderName;
				changed = true;
			}
		}
		if (changed)
		{
			C.Save();
			Render(animate: true);
		}
	}

	private void RenameFolder(string oldName, string newName)
	{
		bool changed = false;
		foreach (Entry a in C.Prefs.Apps)
		{
			if (string.Equals(a.Folder ?? "", oldName, StringComparison.OrdinalIgnoreCase))
			{
				a.Folder = newName;
				changed = true;
			}
		}
		if (changed)
		{
			if (string.Equals(currentFolder, oldName, StringComparison.OrdinalIgnoreCase))
			{
				currentFolder = newName;
			}
			C.Save();
			Render(animate: true);
		}
	}

	private void DissolveFolder(string name)
	{
		bool changed = false;
		foreach (Entry a in C.Prefs.Apps)
		{
			if (string.Equals(a.Folder ?? "", name, StringComparison.OrdinalIgnoreCase))
			{
				a.Folder = "";
				changed = true;
			}
		}
		if (changed)
		{
			if (string.Equals(currentFolder, name, StringComparison.OrdinalIgnoreCase))
			{
				currentFolder = "";
			}
			C.Save();
			Render(animate: true);
		}
	}

	// 拖拽贴边自动滚动（滚动视图边缘 56px 内触发）
	private void AutoScrollFor(Point p)
	{
		if (tileScroller == null || tileScroller.ScrollableHeight <= 0.0)
		{
			return;
		}
		if (p.Y > tileScroller.ViewportHeight - 56.0)
		{
			tileScroller.ScrollToVerticalOffset(Math.Min(tileScroller.ScrollableHeight, tileScroller.VerticalOffset + 22.0));
		}
		else if (p.Y < 56.0)
		{
			tileScroller.ScrollToVerticalOffset(Math.Max(0.0, tileScroller.VerticalOffset - 22.0));
		}
	}

	// 滚动模式下当前行数 = ceil(磁贴数 / 列数)，用于拖放让位与指示线的格网数学
	private int TileRowCount()
	{
		int count = Tiles.Children.Count;
		if (count <= 0)
		{
			return 0;
		}
		return (count + Math.Max(1, columns) - 1) / Math.Max(1, columns);
	}

	private void BuildSearchPopup(Border anchor)
	{
		searchPopup = new Popup
		{
			PlacementTarget = anchor,
			Placement = PlacementMode.Bottom,
			StaysOpen = false,
			AllowsTransparency = true,
			PopupAnimation = PopupAnimation.Fade,
			VerticalOffset = 2.0
		};
		// 搜索结果列表强制高对比：背景只跟明暗主题，不掺玻璃色调（tint 白色时曾出现白底白字看不清）
		System.Windows.Media.Color bg = C.Prefs.Light ? System.Windows.Media.Color.FromRgb(250, 252, 255) : System.Windows.Media.Color.FromRgb(28, 32, 44);
		System.Windows.Media.Color ink = C.Prefs.Light ? System.Windows.Media.Color.FromRgb(32, 38, 53) : System.Windows.Media.Color.FromRgb(244, 246, 252);
		System.Windows.Media.Color line = C.Prefs.Light ? System.Windows.Media.Color.FromRgb(210, 216, 226) : System.Windows.Media.Color.FromRgb(62, 72, 96);
		bg.A = byte.MaxValue;
		line.A = 180;
		searchList = new ListBox
		{
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			MaxHeight = 400.0
		};
		ScrollViewer.SetVerticalScrollBarVisibility(searchList, ScrollBarVisibility.Hidden);
		ScrollViewer.SetHorizontalScrollBarVisibility(searchList, ScrollBarVisibility.Disabled);
		Border container = new Border
		{
			CornerRadius = new CornerRadius(14.0),
			Background = new SolidColorBrush(bg),
			BorderBrush = new SolidColorBrush(line),
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(6.0),
			MinWidth = 300.0,
			MaxWidth = 460.0,
			Child = searchList
		};
		// Popup 内容不继承窗口隐式样式，这里自带一套：防止系统默认白色 hover/选中底色
		Style popupListStyle = new Style(typeof(System.Windows.Controls.ListBox));
		popupListStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		popupListStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(ink)));
		popupListStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0.0)));
		container.Resources[typeof(System.Windows.Controls.ListBox)] = popupListStyle;
		Style popupItemStyle = new Style(typeof(System.Windows.Controls.ListBoxItem));
		popupItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		popupItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8.0, 6.0, 8.0, 6.0)));
		popupItemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
		FrameworkElementFactory popupItemFrame = new FrameworkElementFactory(typeof(Border));
		popupItemFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(8.0));
		popupItemFrame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
		popupItemFrame.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
		popupItemStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(System.Windows.Controls.ListBoxItem))
		{
			VisualTree = popupItemFrame
		}));
		container.Resources[typeof(System.Windows.Controls.ListBoxItem)] = popupItemStyle;
		searchPopup.Child = container;
	}

	private void RunSearch()
	{
		int serial = ++searchSerial;
		if (searchCts != null)
		{
			searchCts.Cancel();
			searchCts.Dispose();
			searchCts = null;
		}
		string query = ((Search == null) ? "" : Search.Text.Trim());
		searchItems = new List<SystemSearchResult>();
		if (searchList != null)
		{
			searchList.ItemsSource = null;
		}
		if (query.Length == 0)
		{
			if (searchPopup != null)
			{
				searchPopup.IsOpen = false;
			}
			return;
		}
		if (searchPopup != null)
		{
			searchPopup.IsOpen = true;
		}
		searchCts = new CancellationTokenSource();
		CancellationToken ct = searchCts.Token;
		Task.Run(() =>
		{
			List<SystemSearchResult> instant = SystemSearch.Instant(query);
			if (serial != searchSerial || ct.IsCancellationRequested)
			{
				return;
			}
			Dispatcher.BeginInvoke(new Action(() =>
			{
				if (serial == searchSerial)
				{
					SetSearchResults(instant);
				}
			}));
			try
			{
				SystemSearch.Deep(query, (List<SystemSearchResult> batch2) =>
				{
					if (serial != searchSerial || ct.IsCancellationRequested)
					{
						return;
					}
					Dispatcher.BeginInvoke(new Action(() =>
					{
						if (serial == searchSerial)
						{
							SetSearchResults(batch2);
						}
					}));
				}, ct);
			}
			catch (OperationCanceledException)
			{
			}
			catch
			{
			}
		});
	}

	private void SetSearchResults(List<SystemSearchResult> incoming)
	{
		if (searchList == null)
		{
			return;
		}
		Dictionary<string, bool> seen = new Dictionary<string, bool>();
		foreach (SystemSearchResult item in searchItems)
		{
			seen[item.Path.ToLowerInvariant()] = true;
		}
		foreach (SystemSearchResult item in incoming)
		{
			string key = item.Path.ToLowerInvariant();
			if (!seen.ContainsKey(key))
			{
				seen[key] = true;
				searchItems.Add(item);
			}
		}
		searchItems.Sort((SystemSearchResult a, SystemSearchResult b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		List<ListBoxItem> rows = new List<ListBoxItem>();
		int max = Math.Min(searchItems.Count, 60);
		for (int i = 0; i < max; i++)
		{
			rows.Add(MakeSearchRow(searchItems[i]));
		}
		searchList.ItemsSource = rows;
	}

	private ListBoxItem MakeSearchRow(SystemSearchResult r)
	{
		// 与 BuildSearchPopup 一致：强制高对比，不掺玻璃色调
		System.Windows.Media.Color ink = C.Prefs.Light ? System.Windows.Media.Color.FromRgb(32, 38, 53) : System.Windows.Media.Color.FromRgb(244, 246, 252);
		System.Windows.Media.Color hoverColor = C.Prefs.Light ? System.Windows.Media.Color.FromRgb(58, 96, 205) : System.Windows.Media.Color.FromRgb(255, 255, 255);
		SolidColorBrush hoverBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, hoverColor.R, hoverColor.G, hoverColor.B));
		ListBoxItem row = new ListBoxItem
		{
			Background = Brushes.Transparent,
			Padding = new Thickness(8.0, 6.0, 8.0, 6.0),
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			Focusable = false
		};
		row.MouseEnter += delegate
		{
			row.Background = hoverBrush;
		};
		row.MouseLeave += delegate
		{
			row.Background = Brushes.Transparent;
		};
		StackPanel panel = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		// 左：大图标（32px），应用取真实图标、文件夹用原生文件夹样式
		Border iconHost = new Border
		{
			Width = 32.0,
			Height = 32.0,
			VerticalAlignment = VerticalAlignment.Center,
			CornerRadius = new CornerRadius(7.0),
			Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(22, ink.R, ink.G, ink.B)),
			Child = new Image
			{
				Width = 26.0,
				Height = 26.0,
				Stretch = Stretch.Uniform,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
		Image image = (Image)iconHost.Child;
		if (r.IsFolder)
		{
			image.Source = FolderGlyph();
		}
		else
		{
			LoadSearchIcon(image, r.Path);
		}
		panel.Children.Add(iconHost);
		// 右：两行——名称 + 类型/副标题（仿 Windows 搜索）
		StackPanel textColumn = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
			MaxWidth = 330.0
		};
		TextBlock nameBlock = new TextBlock
		{
			Text = r.Name,
			Foreground = new SolidColorBrush(ink),
			FontSize = 14.0,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		textColumn.Children.Add(nameBlock);
		string subtitle;
		if (r.IsFolder)
		{
			subtitle = "文件夹";
		}
		else
		{
			string ext = ((r.Path.LastIndexOf('.') > r.Path.LastIndexOf('\\')) ? r.Path.Substring(r.Path.LastIndexOf('.') + 1) : "").ToLowerInvariant();
			subtitle = (ext == "exe" || ext == "lnk" || ext == "url" || ext == "appref-ms") ? "应用" : "文件";
		}
		TextBlock subBlock = new TextBlock
		{
			Text = subtitle,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(160, ink.R, ink.G, ink.B)),
			FontSize = 12.0,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		textColumn.Children.Add(subBlock);
		panel.Children.Add(textColumn);
		row.Content = panel;
		row.MouseLeftButtonUp += (object s, MouseButtonEventArgs e2) =>
		{
			OpenSearchResult(r);
		};
		row.PreviewMouseRightButtonUp += (object s, MouseButtonEventArgs e2) =>
		{
			// 直接弹程序自带的系统右键菜单（打开方式/管理员运行/属性等）
			e2.Handled = true;
			if (searchPopup != null)
			{
				searchPopup.IsOpen = false;
			}
			Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
			{
				ShellContextMenu.ShowAtCursor(r.Path, this);
			}));
		};
		return row;
	}

	private void LoadSearchIcon(Image image, string path)
	{
		Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
		{
			try
			{
				ImageSource source = Native.Icon(path, false, true);
				if (source != null)
				{
					image.Source = source;
				}
			}
			catch
			{
			}
		}));
	}

	private ImageSource FolderGlyph()
	{
		DrawingGroup group = new DrawingGroup();
		group.Children.Add(new GeometryDrawing(new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 242, 193, 104)), null, Geometry.Parse("M 2,5 L 8,5 L 10,7 L 18,7 L 18,17 L 2,17 Z")));
		DrawingImage drawing = new DrawingImage(group);
		drawing.Freeze();
		return drawing;
	}

	private void OpenSearchResult(SystemSearchResult r)
	{
		if (searchPopup != null)
		{
			searchPopup.IsOpen = false;
		}
		Keyboard.ClearFocus();
		SystemSearch.Open(r);
	}

	public void SetStatus(string text)
	{
		pendingStatus = text;
		if (Status != null)
		{
			Status.Text = text;
		}
	}

	private void SetSelectionMode(bool enabled)
	{
		editing = enabled;
		selected.Clear();
		dragEntry = null;
		if (editSelection != null)
		{
			editSelection.Content = (enabled ? "完成" : "多选");
		}
	}

	public void FinishSelection()
	{
		SetSelectionMode(enabled: false);
	}

	private void SelectFiltered()
	{
		foreach (Entry item in Filtered())
		{
			selected.Add(item.Path);
		}
		Render();
	}

	private void UpdateSelectionBar()
	{
		selected.RemoveWhere((string p) => !C.Prefs.Apps.Any((Entry a) => string.Equals(a.Path, p, StringComparison.OrdinalIgnoreCase)));
		bool flag = editing;
		bulkBar.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		bulkRow.Height = (flag ? GridLength.Auto : new GridLength(0.0));
		selectAll.IsEnabled = editing;
		clearSelection.IsEnabled = editing && selected.Count > 0;
		deleteSelected.IsEnabled = editing && selected.Count > 0;
		deleteSelected.Content = "移除已选（" + selected.Count + "）";
		selectionCount.Text = (editing ? ("已选 " + selected.Count + " · 滚动保留") : "可撤销最近操作");
	}

	public void Render(bool animate = false)
	{
		CloseMediaPreview();
		HideInsertMarker();
		if (Tiles == null || (!IsVisible && !preparingView))
		{
			return;
		}
		UpdateSelectionBar();
		Tiles.Children.Clear();
		List<Entry> list = Filtered();
		Empty.Visibility = ((list.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
		if (pendingStatus != null)
		{
			Status.Text = pendingStatus;
		}
		if (currentFolder != "")
		{
			Status.Text = "文件夹「" + currentFolder + "」 · 拖动图标到「← 返回」可移出 · Esc 收起";
		}
		int num2 = 0;
		foreach (Entry item in list)
		{
			Tiles.Children.Add(Tile(item, num2++, animate));
		}
		RefreshFolderBar();
	}

	// —— 媒体悬浮预览：图片显示大图，视频静音循环播放（悬浮 450ms 触发）——

	private void QueueMediaPreview(Entry entry, FrameworkElement anchor)
	{
		if (editing || !MediaThumb.IsPreviewable(entry.Path))
		{
			return;
		}
		// 同一入口的预览已打开时不重开：悬浮动画抖动会反复触发 Enter，重开会闪
		if (mediaPreviewPopup != null && mediaPreviewPopup.IsOpen && ReferenceEquals(mediaPreviewAnchor, anchor))
		{
			return;
		}
		mediaPreviewEntry = entry;
		mediaPreviewAnchor = anchor;
		if (mediaPreviewTimer == null)
		{
			mediaPreviewTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(450.0)
			};
			mediaPreviewTimer.Tick += (object s, EventArgs e) =>
			{
				mediaPreviewTimer.Stop();
				Entry captured = mediaPreviewEntry;
				FrameworkElement capturedAnchor = mediaPreviewAnchor;
				if (captured != null && capturedAnchor != null)
				{
					OpenMediaPreview(captured, capturedAnchor);
				}
			};
		}
		mediaPreviewTimer.Stop();
		mediaPreviewTimer.Start();
	}

	private void CancelMediaPreview()
	{
		mediaPreviewTimer?.Stop();
		CloseMediaPreview();
	}

	// 悬浮动画的缩放/位移会让光标在磁贴边缘反复进出（Enter/Leave 抖动），
	// 预览已打开时不再因 Leave 立即关闭，改由窗口级 MouseMove 判断光标真正离开后收起。
	private void OnMediaTileLeave()
	{
		if (mediaPreviewPopup != null && mediaPreviewPopup.IsOpen)
		{
			return;
		}
		CancelMediaPreview();
	}

	private bool IsCursorOverMediaPreview()
	{
		System.Drawing.Point raw = System.Windows.Forms.Cursor.Position;
		System.Windows.Point cursor = new System.Windows.Point(raw.X, raw.Y);
		if (mediaPreviewAnchor != null && mediaPreviewAnchor.IsVisible)
		{
			System.Windows.Point tl = mediaPreviewAnchor.PointToScreen(new System.Windows.Point(0.0, 0.0));
			System.Windows.Rect anchorRect = new System.Windows.Rect(tl.X, tl.Y, Math.Max(1.0, mediaPreviewAnchor.ActualWidth), Math.Max(1.0, mediaPreviewAnchor.ActualHeight));
			if (anchorRect.Contains(cursor))
			{
				return true;
			}
		}
		if (mediaPreviewPopup != null && mediaPreviewPopup.IsOpen && mediaPreviewPopup.Child is FrameworkElement child && child.IsVisible)
		{
			System.Windows.Point tl2 = child.PointToScreen(new System.Windows.Point(0.0, 0.0));
			System.Windows.Rect popupRect = new System.Windows.Rect(tl2.X, tl2.Y, Math.Max(1.0, child.ActualWidth), Math.Max(1.0, child.ActualHeight));
			if (popupRect.Contains(cursor))
			{
				return true;
			}
		}
		return false;
	}

	private void OpenMediaPreview(Entry entry, FrameworkElement anchor)
	{
		CloseMediaPreview();
		mediaPreviewPopup = new Popup
		{
			PlacementTarget = anchor,
			Placement = PlacementMode.Top,
			VerticalOffset = -8.0,
			AllowsTransparency = true,
			PopupAnimation = PopupAnimation.Fade,
			IsHitTestVisible = true,
			StaysOpen = true,
			Child = BuildMediaCard(entry)
		};
		mediaPreviewPopup.Closed += (object s, EventArgs e) =>
		{
			if (mediaPreviewPlayer != null)
			{
				try
				{
					mediaPreviewPlayer.Stop();
				}
				catch
				{
				}
				mediaPreviewPlayer.Source = null;
				mediaPreviewPlayer = null;
			}
		};
		mediaPreviewPopup.IsOpen = true;
	}

	private FrameworkElement BuildMediaCard(Entry entry)
	{
		string ext = Path.GetExtension(entry.Path);
		bool isVideo = MediaThumb.IsVideo(entry.Path);
		Grid host = new Grid
		{
			Width = 424.0,
			Height = 238.0,
			ClipToBounds = true
		};
		if (isVideo)
		{
			MediaElement player = new MediaElement
			{
				Stretch = Stretch.Uniform,
				LoadedBehavior = MediaState.Play,
				UnloadedBehavior = MediaState.Close,
				Volume = 0.0,
				ScrubbingEnabled = true
			};
			player.MediaFailed += (object s, ExceptionRoutedEventArgs e) =>
			{
				host.Children.Clear();
				host.Children.Add(new TextBlock
				{
					Text = "无法解码该视频（缺少系统解码器）",
					Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 170)),
					FontSize = 13.0,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				});
			};
			player.Source = new Uri(entry.Path);
			mediaPreviewPlayer = player;
			host.Children.Add(player);
		}
		else
		{
			ImageSource src = null;
			if (string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase))
			{
				src = MediaThumb.SvgPreview(entry.Path, 480);
			}
			else
			{
				src = MediaThumb.LoadFile(entry.Path, 480);
				if (src == null)
				{
					src = ShellThumbnail.Get(entry.Path, 480);
				}
			}
			if (src != null)
			{
				System.Windows.Controls.Image img = new System.Windows.Controls.Image
				{
					Source = src,
					MaxWidth = 424.0,
					MaxHeight = 238.0,
					Stretch = Stretch.Uniform
				};
				RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
				host.Children.Add(img);
			}
			else
			{
				host.Children.Add(new TextBlock
				{
					Text = "无法生成预览",
					Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 170)),
					FontSize = 13.0,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				});
			}
		}
		string sizeText = "—";
		try
		{
			sizeText = FormatBytes(new FileInfo(entry.Path).Length);
		}
		catch
		{
		}
		StackPanel caption = new StackPanel
		{
			Margin = new Thickness(12.0, 9.0, 12.0, 11.0)
		};
		caption.Children.Add(new TextBlock
		{
			Text = entry.Name,
			FontSize = 13.0,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brushes.White,
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		caption.Children.Add(new TextBlock
		{
			Text = ((isVideo ? "视频 · " : "图片 · ") + sizeText + " · 点击磁贴用默认应用打开"),
			FontSize = 11.0,
			Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 170)),
			Margin = new Thickness(0.0, 3.0, 0.0, 0.0)
		});
		StackPanel layout = new StackPanel();
		layout.Children.Add(host);
		layout.Children.Add(caption);
		Border cardBorder = new Border
		{
			CornerRadius = new CornerRadius(10.0),
			Background = new SolidColorBrush(Color.FromArgb(247, 32, 35, 45)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(40, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(1.0),
			Child = layout,
			SnapsToDevicePixels = true
		};
		// 点击预览卡片：关闭悬浮预览并用默认应用打开（此前点击会穿透到桌面导致预览残留）
		cardBorder.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			e.Handled = true;
			CloseMediaPreview();
			C.Launch(entry);
		};
		// 光标离开预览卡片且不在磁贴上时收起，避免预览悬浮残留
		cardBorder.MouseLeave += (object s, MouseEventArgs e) =>
		{
			if (!IsCursorOverMediaPreview())
			{
				CancelMediaPreview();
			}
		};
		return cardBorder;
	}

	private void CloseMediaPreview()
	{
		mediaPreviewTimer?.Stop();
		if (mediaPreviewPopup != null)
		{
			mediaPreviewPopup.IsOpen = false;
			mediaPreviewPopup = null;
		}
		if (mediaPreviewPlayer != null)
		{
			// LoadedBehavior 为自动模式时调用 Stop() 会抛 NotSupportedException，直接置空 Source 即可停止并释放文件句柄
			MediaElement player = mediaPreviewPlayer;
			mediaPreviewPlayer = null;
			try
			{
				player.Stop();
			}
			catch
			{
			}
			player.Source = null;
		}
	}

	private static string FormatBytes(long bytes)
	{
		string[] units = { "B", "KB", "MB", "GB", "TB" };
		double value = bytes;
		int unit = 0;
		while (value >= 1024.0 && unit < units.Length - 1)
		{
			value /= 1024.0;
			unit++;
		}
		return value.ToString((unit == 0) ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
	}

	// 后台图标回填：把磁贴占位符（▣）换成真图标；磁贴已被重建（脱离可视树）时放弃
	private void TileSetIcon(StackPanel panel, ImageSource source)
	{
		if (source == null || panel == null || panel.Parent == null)
		{
			return;
		}
		System.Windows.Controls.Image image = new System.Windows.Controls.Image
		{
			Source = source,
			Width = C.Prefs.BoardIconSize,
			Height = C.Prefs.BoardIconSize,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
		if (panel.Children.Count > 0)
		{
			// WPF 的 Children[i]=x 赋值会抛「索引已经在使用」；必须先移除再插入。
			panel.Children.RemoveAt(0);
		}
		panel.Children.Insert(0, image);
	}

	private FrameworkElement Tile(Entry a, int index, bool animate)
	{
		bool isSelected = editing && selected.Contains(a.Path);
		SolidColorBrush selectedBrush = new SolidColorBrush(Color.FromArgb(48, 111, 167, byte.MaxValue));
		Border tile = new Border
		{
			Tag = a,
			Background = (isSelected ? selectedBrush : Brushes.Transparent),
			BorderThickness = new Thickness(1.0),
			BorderBrush = (isSelected ? new SolidColorBrush(Color.FromArgb(150, 146, 187, byte.MaxValue)) : Brushes.Transparent),
			CornerRadius = new CornerRadius(18.0),
			Margin = new Thickness(C.Prefs.BoardIconGap),
			Padding = new Thickness(4.0),
			Cursor = Cursors.Hand,
			ToolTip = a.Name,
			AllowDrop = true
		};
		// 右键直接弹系统原生菜单（资源管理器同款：打开/管理员运行/属性/固定等）。
		// 不再使用内置菜单，也避免 ContextMenuOpening 惰性赋值导致的“第一次右键不弹”问题。
		tile.PreviewMouseRightButtonUp += (object s, MouseButtonEventArgs e2) =>
		{
			e2.Handled = true;
			if (editing)
			{
				return; // 多选模式下不弹右键菜单（与原逻辑一致）
			}
			CloseMediaPreview();
			Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
			{
				ShellContextMenu.ShowAtCursor(a.Path, this, delegate
				{
					// 原生菜单构建失败（路径不可解析/无 Shell 菜单）时回退内置菜单，保证右键永远有响应
					System.Windows.Controls.ContextMenu menu = C.Menu(a, C.Prefs.BoardMaterial.Tint, (int)(C.Prefs.OpacityPercent ?? 72.0));
					menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
					menu.PlacementTarget = tile;
					menu.IsOpen = true;
				});
			}));
		};
		Grid grid = new Grid();
		tile.Child = grid;
		StackPanel stackPanel = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center
		};
		grid.Children.Add(stackPanel);
		// 磁贴图标走后台管线：缓存未命中先渲染 ▣ 占位符，提取完成后回填
		ImageSource imageSource = IconPipeline.Request(a, C.Prefs.Dock.ShowShortcutOverlays, true, true, img => TileSetIcon(stackPanel, img));
		if (imageSource != null)
		{
			System.Windows.Controls.Image image = new System.Windows.Controls.Image
			{
				Source = imageSource,
				Width = C.Prefs.BoardIconSize,
				Height = C.Prefs.BoardIconSize,
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
			stackPanel.Children.Add(image);
		}
		else
		{
			stackPanel.Children.Add(new TextBlock
			{
				Text = "▣",
				Foreground = TextBrush,
				FontSize = C.Prefs.BoardIconSize * 0.84,
				Height = C.Prefs.BoardIconSize,
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
			});
		}
		stackPanel.Children.Add(new TextBlock
		{
			Text = a.Name,
			Foreground = TextBrush,
			FontSize = 12.0,
			TextAlignment = TextAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = Math.Max(20.0, ((Tiles.ActualWidth > 0.0) ? Tiles.ActualWidth : (Width - 104.0)) / (double)columns - 2.0 * C.Prefs.BoardIconGap - 10.0)
		});
		grid.Children.Add(new Border
		{
			Tag = "running",
			Width = 5.0,
			Height = 5.0,
			CornerRadius = new CornerRadius(3.0),
			Background = new SolidColorBrush(Color.FromRgb(135, 205, byte.MaxValue)),
			VerticalAlignment = VerticalAlignment.Bottom,
			HorizontalAlignment = HorizontalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
			ToolTip = "正在运行，点击切回窗口",
			Visibility = ((!C.Prefs.Dock.ShowRunning || !RunningWindows.IsRunning(a)) ? Visibility.Collapsed : Visibility.Visible)
		});
		if (editing)
		{
			CheckBox element = new CheckBox
			{
				IsChecked = isSelected,
				IsHitTestVisible = false,
				Focusable = false,
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(2.0)
			};
			grid.Children.Add(element);
		}
		ScaleTransform scale = new ScaleTransform(1.0, 1.0);
		TranslateTransform lift = new TranslateTransform();
		TransformGroup transformGroup = new TransformGroup();
		transformGroup.Children.Add(scale);
		transformGroup.Children.Add(lift);
		tile.RenderTransform = transformGroup;
		tile.RenderTransformOrigin = new Point(0.5, 0.5);
		Action<bool> hover = (bool over) =>
		{
			Border border = tile;
			SolidColorBrush background = (isSelected ? selectedBrush : (over ? new SolidColorBrush(Color.FromArgb(24, byte.MaxValue, byte.MaxValue, byte.MaxValue)) : Brushes.Transparent));
			border.Background = background;
			if (MotionEnabled)
			{
				CubicEase easingFunction2 = new CubicEase
				{
					EasingMode = EasingMode.EaseOut
				};
				scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(over ? (1.0 + 0.025 * MotionLevel) : 1.0, TimeSpan.FromMilliseconds(170.0))
				{
					EasingFunction = easingFunction2
				});
				scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(over ? (1.0 + 0.025 * MotionLevel) : 1.0, TimeSpan.FromMilliseconds(170.0))
				{
					EasingFunction = easingFunction2
				});
				lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(over ? (-3.0 * MotionLevel) : 0.0, TimeSpan.FromMilliseconds(170.0))
				{
					EasingFunction = easingFunction2
				});
			}
		};
		tile.MouseEnter += (object s, MouseEventArgs e) =>
		{
			if (activeDrag)
			{
				return;
			}
			hover(obj: true);
			QueueMediaPreview(a, tile);
		};
		tile.MouseLeave += (object s, MouseEventArgs e) =>
		{
			hover(obj: false);
			OnMediaTileLeave();
		};
		if (animate && MotionEnabled)
		{
			CubicEase easingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			};
			TimeSpan value = TimeSpan.FromMilliseconds(Math.Min(index, 14) * 12);
			tile.Opacity = 0.0;
			tile.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(190.0))
			{
				BeginTime = value,
				EasingFunction = easingFunction
			});
			lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(9.0 * MotionLevel, 0.0, TimeSpan.FromMilliseconds(230.0))
			{
				BeginTime = value,
				EasingFunction = easingFunction
			});
		}
		tile.MouseLeftButtonDown += (object s, MouseButtonEventArgs e) =>
		{
			dragStart = e.GetPosition(this);
			dragEntry = a;
		};
		tile.MouseMove += (object s, MouseEventArgs e) =>
		{
			if (e.LeftButton == MouseButtonState.Pressed && dragEntry == a)
			{
				Point position = e.GetPosition(this);
				if (Math.Abs(position.X - dragStart.X) + Math.Abs(position.Y - dragStart.Y) > 10.0)
				{
					dragEntry = null;
					StartEntryDrag(tile, a);
				}
			}
		};
		tile.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			if (dragEntry == a)
			{
				dragEntry = null;
				CloseMediaPreview();
				if (editing)
				{
					if (!selected.Remove(a.Path))
					{
						selected.Add(a.Path);
					}
					Render();
					e.Handled = true;
				}
				else
				{
					C.Launch(a);
				}
			}
		};
		tile.DragOver += (object s, DragEventArgs e) =>
		{
			if (RecycleBin.IsEntry(a))
			{
				if (DraggedEntries(e.Data).Length != 0 || e.Data.GetDataPresent(DataFormats.FileDrop))
				{
					e.Effects = DragDropEffects.Move;
					e.Handled = true;
				}
				return;
			}
			if (DraggedEntries(e.Data).Length != 0)
			{
				AutoScrollFor(e.GetPosition(tileScroller));
				int idx = Tiles.Children.IndexOf(tile);
				if (e.GetPosition(tile).X > tile.ActualWidth / 2.0)
				{
					idx++;
				}
				if (idx != liveIndex)
				{
					liveIndex = idx;
					ApplyLivePreview(DraggedEntries(e.Data), idx);
				}
				e.Effects = DragDropEffects.Move;
				e.Handled = true;
			}
		};
		tile.Drop += (object s, DragEventArgs e) =>
		{
			if (RecycleBin.IsEntry(a))
			{
				e.Handled = true;
				if (e.Data.GetDataPresent(DataFormats.FileDrop))
				{
					C.DeleteToRecycleBin((string[])e.Data.GetData(DataFormats.FileDrop));
					return;
				}
				string[] dropped = DraggedEntries(e.Data);
				if (dropped.Length != 0 && !dropped.Any((string p) => RecycleBin.IsPath(p)))
				{
					// 拖应用入口到回收站 = 把入口指向的真实文件/文件夹送入系统回收站（可恢复），
					// 与文件拖入行为一致，不再做自定义确认/撤销。
					string[] real = (from p in dropped
						let a = C.Prefs.Apps.FirstOrDefault((Entry x) => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))
						where a != null && !string.IsNullOrEmpty(a.Path)
						&& !a.Path.Contains("://") && !a.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !a.Path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase)
						select a.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
					C.DeleteToRecycleBin(real);
				}
				return;
			}
			string[] array = DraggedEntries(e.Data);
			if (array.Length != 0)
			{
				HideInsertMarker();
				if (currentFolder == "")
				{
					// 手机式：拖到另一个图标上自动创建文件夹（重名则并入），把双方一起放进去
					List<string> paths = array.Where((string p) => !string.Equals(p, a.Path, StringComparison.OrdinalIgnoreCase)).ToList();
					if (paths.Count == 0)
					{
						e.Handled = true; // 拖到自己身上：无操作
						return;
					}
					paths.Add(a.Path);
					MoveEntriesToFolder(paths.ToArray(), "文件夹");
					OpenFolder("文件夹");
				}
				else if (!C.ReorderView(CurrentPageSequence()))
				{
					Render();
				}
				e.Handled = true;
			}
		};
		return tile;
	}

	private string[] DraggedEntries(IDataObject data)
	{
		if (data.GetDataPresent("GlassBoard.Entries"))
		{
			return (string[])data.GetData("GlassBoard.Entries");
		}
		if (data.GetDataPresent("GlassBoard.Entry"))
		{
			return new string[1] { (string)data.GetData("GlassBoard.Entry") };
		}
		return new string[0];
	}

	// 按落点（相对 Tiles 的坐标）换算插入位置：行优先，越界时取末尾（追加）
	private int InsertIndexFor(Point p)
	{
		if (columns <= 0 || Tiles.ActualWidth <= 0.0)
		{
			return Tiles.Children.Count;
		}
		double cellW = Tiles.ActualWidth / (double)columns;
		double cellH = (TileRowCount() > 0 && Tiles.ActualHeight > 0.0) ? Tiles.ActualHeight / (double)TileRowCount() : 96.0;
		int col = Math.Max(0, Math.Min((int)(p.X / cellW), columns - 1));
		int row = Math.Max(0, Math.Min((int)(p.Y / cellH), Math.Max(0, TileRowCount() - 1)));
		return Math.Min(row * columns + col, Tiles.Children.Count);
	}

	// 在目标格左侧画一条插入指示线；idx == 数量时画在最后一个图标右侧（追加）
	private void ShowInsertMarker(int idx)
	{
		if (Tiles == null || columns <= 0 || Tiles.ActualWidth <= 0.0)
		{
			HideInsertMarker();
			return;
		}
		int count = Tiles.Children.Count;
		int cell = Math.Min(idx, count);
		double cellW = Tiles.ActualWidth / (double)columns;
		int rowCount = TileRowCount();
		double cellH = (rowCount > 0 && Tiles.ActualHeight > 0.0) ? Tiles.ActualHeight / (double)rowCount : 96.0;
		int col = cell % columns;
		int row = cell / columns;
		double x = col * cellW + ((cell >= count) ? cellW : 0.0) - 1.5;
		double y = row * cellH + 6.0 - ((tileScroller != null) ? tileScroller.VerticalOffset : 0.0);
		if (tileScroller != null && (y < 0.0 || y > tileScroller.ViewportHeight))
		{
			HideInsertMarker();
			return;
		}
		TranslateTransform t = (TranslateTransform)insertMarker.RenderTransform;
		t.X = x;
		t.Y = y;
		insertMarker.Height = Math.Max(24.0, cellH - 12.0);
		insertMarker.Visibility = Visibility.Visible;
		insertIndex = idx;
	}

	private void HideInsertMarker()
	{
		if (insertMarker != null)
		{
			insertMarker.Visibility = Visibility.Collapsed;
		}
		insertIndex = -1;
		liveIndex = -1;
		livePreview = false;
	}

	// 当前视图磁贴按容器顺序取出的路径序列（拖放落点以它为准提交）
	private string[] CurrentPageSequence()
	{
		List<string> list = new List<string>();
		foreach (Border b in Tiles.Children.OfType<Border>())
		{
			if (b.Tag is Entry entry)
			{
				list.Add(entry.Path);
			}
		}
		return list.ToArray();
	}

	// 拖动实时让位：其余图标即时滑开（纯格网数学，不做视觉树测量），被拖图标半透明放到目标位置。
	// 位移叠加到磁贴当前的动画位移上，避免拖动中频繁重排产生跳动；落点确认后由 Render 播放收尾动画。
	private void ApplyLivePreview(string[] paths, int idx)
	{
		if (Tiles == null)
		{
			return;
		}
		List<Border> items = Tiles.Children.OfType<Border>().ToList();
		if (items.Count == 0)
		{
			return;
		}
		int cols = Math.Max(1, Tiles.Columns);
		int rowCount = Math.Max(1, TileRowCount());
		double cellW = (Tiles.ActualWidth > 0.0 ? Tiles.ActualWidth : Width) / cols;
		double cellH = (Tiles.ActualHeight > 0.0 ? Tiles.ActualHeight : Height) / rowCount;
		Dictionary<Border, int> oldIndex = new Dictionary<Border, int>();
		for (int i = 0; i < items.Count; i++)
		{
			oldIndex[items[i]] = i;
		}
		List<Border> draggedTiles = new List<Border>();
		foreach (string p in paths)
		{
			Border border = items.FirstOrDefault((Border b) => b.Tag is Entry entry && string.Equals(entry.Path, p, StringComparison.OrdinalIgnoreCase));
			if (border != null && !draggedTiles.Contains(border))
			{
				draggedTiles.Add(border);
			}
		}
		List<Border> rest = items.Except(draggedTiles).ToList();
		List<Border> order = new List<Border>(rest);
		int insert = Math.Max(0, Math.Min(idx, order.Count));
		for (int i = 0; i < draggedTiles.Count; i++)
		{
			order.Insert(insert + i, draggedTiles[i]);
		}
		for (int i = 0; i < order.Count; i++)
		{
			if (Tiles.Children.IndexOf(order[i]) != i)
			{
				Tiles.Children.Remove(order[i]);
				Tiles.Children.Insert(i, order[i]);
			}
		}
		CubicEase ease = new CubicEase
		{
			EasingMode = EasingMode.EaseOut
		};
		foreach (Border b in order)
		{
			int from = oldIndex[b];
			int to = order.IndexOf(b);
			double dx = ((double)(from % cols) - (to % cols)) * cellW;
			double dy = ((double)(from / cols) - (to / cols)) * cellH;
			if (b.Child is FrameworkElement content)
			{
				TranslateTransform slide = content.RenderTransform as TranslateTransform;
				if (slide == null || slide.IsFrozen)
				{
					slide = new TranslateTransform();
					content.RenderTransform = slide;
				}
				// 叠加未收尾的动画位移，消除重复重排时的跳动
				slide.X += dx;
				slide.Y += dy;
				if (Math.Abs(slide.X) > 0.5 || Math.Abs(slide.Y) > 0.5)
				{
					slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120.0))
					{
						EasingFunction = ease
					});
					slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120.0))
					{
						EasingFunction = ease
					});
				}
			}
			b.Opacity = (draggedTiles.Contains(b) ? 0.3 : 1.0);
		}
		ShowInsertMarker(insert);
		livePreview = true;
	}

	private void ExternalDrop(object s, DragEventArgs e)
	{
		if (e.Data.GetDataPresent(DataFormats.FileDrop))
		{
			C.Add((string[])e.Data.GetData(DataFormats.FileDrop), manual: true);
			ClearSearch();
			e.Handled = true;
		}
	}

	public void UpdateRunning()
	{
		if (Tiles == null || !IsVisible)
		{
			return;
		}
		foreach (Border item in Tiles.Children.OfType<Border>())
		{
			Entry entry = item.Tag as Entry;
			Grid grid = item.Child as Grid;
			if (entry == null || grid == null)
			{
				continue;
			}
			foreach (Border item2 in from b in grid.Children.OfType<Border>()
				where b.Tag as string == "running"
				select b)
			{
				item2.Visibility = ((!C.Prefs.Dock.ShowRunning || !RunningWindows.IsRunning(entry)) ? Visibility.Collapsed : Visibility.Visible);
			}
		}
	}

	private void InitDragExperience()
	{
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		dragPreviewIcon = new Image
		{
			Width = 28.0,
			Height = 28.0,
			Stretch = Stretch.Uniform,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
		};
		dragPreviewText = new TextBlock
		{
			Foreground = Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			MaxWidth = 230.0,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		stackPanel.Children.Add(dragPreviewIcon);
		stackPanel.Children.Add(dragPreviewText);
		dragPreview = new Border
		{
			Child = stackPanel,
			Padding = new Thickness(12.0, 9.0, 12.0, 9.0),
			CornerRadius = new CornerRadius(14.0),
			Background = Polish.Color("#E52A3447"),
			BorderBrush = Polish.Color("#80B8CEEF"),
			BorderThickness = new Thickness(1.0),
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			IsHitTestVisible = false,
			Visibility = Visibility.Collapsed
		};
		Root.Children.Add(dragPreview);
		PreviewDragOver += (object s, DragEventArgs e) =>
		{
			string[] array = DraggedEntries(e.Data);
			bool dataPresent = e.Data.GetDataPresent(DataFormats.FileDrop);
			if (array.Length != 0 || dataPresent)
			{
				activeDrag = true;
				Point position = e.GetPosition(Root);
				if (dragPreviewText.Text.Length == 0)
				{
					if (dataPresent)
					{
						string[] array2 = (string[])e.Data.GetData(DataFormats.FileDrop);
						dragPreviewText.Text = ((array2.Length == 1) ? Path.GetFileName(array2[0]) : ("添加 " + array2.Length + " 个文件"));
					}
					else
					{
						dragPreviewText.Text = "正在移动图标";
					}
				}
				dragPreview.RenderTransform = new TranslateTransform(position.X + 16.0, position.Y + 16.0);
				dragPreview.Visibility = Visibility.Visible;
			}
		};
		PreviewDragLeave += (object s, DragEventArgs e) =>
		{
			Point position = e.GetPosition(Root);
			if (position.X < 0.0 || position.Y < 0.0 || position.X > ActualWidth || position.Y > ActualHeight)
			{
				EndDragPreview();
				Render();
			}
		};
		PreviewDrop += (object s, DragEventArgs e) =>
		{
			EndDragPreview();
			Dispatcher.BeginInvoke((Action)(() =>
			{
				if (!activeDrag && IsVisible)
				{
					Render();
				}
			}));
		};
	}

	private void EndDragPreview()
	{
		activeDrag = false;
		dragPreview.Visibility = Visibility.Collapsed;
		dragPreviewText.Text = "";
		dragPreviewIcon.Source = null;
	}

	private void StartEntryDrag(FrameworkElement tile, Entry entry)
	{
		string[] array = ((editing && selected.Contains(entry.Path)) ? selected.ToArray() : new string[1] { entry.Path });
		DataObject dataObject = new DataObject();
		dataObject.SetData("GlassBoard.Entry", entry.Path);
		dataObject.SetData("GlassBoard.Entries", array);
		dragPreviewText.Text = ((array.Length == 1) ? entry.Name : (entry.Name + " 等 " + array.Length + " 个"));
		try
		{
			dragPreviewIcon.Source = FeatureUi.Icon(entry, C.Prefs.Dock.ShowShortcutOverlays, true);
		}
		catch
		{
			dragPreviewIcon.Source = null;
		}
		bool modalBusy = ModalBusy;
		ModalBusy = true;
		activeDrag = true;
		C.Dock.BeginBoardPinDrag();
		try
		{
			DragDrop.DoDragDrop(tile, dataObject, DragDropEffects.Copy | DragDropEffects.Move);
		}
		finally
		{
			C.Dock.EndBoardPinDrag();
			EndDragPreview();
			ModalBusy = modalBusy;
			Render();
		}
	}

	public void ApplyDisplayOptions()
	{
		if (C.Prefs.BoardHideSearch && Search != null)
		{
			Search.Clear();
			if (searchPopup != null)
			{
				searchPopup.IsOpen = false;
			}
		}
		if (searchHost != null)
		{
			searchHost.Visibility = (C.Prefs.BoardHideSearch ? Visibility.Collapsed : Visibility.Visible);
		}
		if (IsVisible)
		{
			BuildForSize();
			QueueSizeRefresh();
		}
	}

	private void MoreMenu()
	{
		ContextMenu menu = MenuTheme.Create(C.Prefs.Light, C.Prefs.Dock.Accent, C.Prefs.BoardMaterial.Tint, (int)(C.Prefs.OpacityPercent ?? 72.0));
		iconMenu = menu;
		menu.Closed += (object s, RoutedEventArgs e) =>
		{
			if (iconMenu == menu)
			{
				iconMenu = null;
			}
		};
		Action<string, Action, bool> action = (string label, Action action2, bool enabled) =>
		{
			MenuItem menuItem = new MenuItem
			{
				Header = label,
				IsEnabled = enabled
			};
			menuItem.Click += (object s, RoutedEventArgs e) =>
			{
				bool modalBusy = ModalBusy;
				ModalBusy = true;
				try
				{
					menu.IsOpen = false;
					action2();
				}
				finally
				{
					ModalBusy = modalBusy;
				}
			};
			menu.Items.Add(menuItem);
		};
		action("撤销 · Ctrl+Z", () =>
		{
			C.UndoBoard();
		}, C.CanUndoRemoval);
		action("重做 · Ctrl+Y", () =>
		{
			C.RedoBoard();
		}, C.CanRedoBoard);
		menu.Items.Add(new Separator());
		action("图标间距与默认大小", () =>
		{
			IconSettings();
		}, arg3: true);
		action("工作区", () =>
		{
			C.ManageWorkspaces();
		}, arg3: true);
		action("外观方案", () =>
		{
			C.ManageSchemes();
		}, arg3: true);
		menu.Items.Add(new Separator());
		action("导出完整备份", () =>
		{
			C.ExportFullBackup();
		}, arg3: true);
		action("恢复完整备份", () =>
		{
			C.RestoreFullBackup();
		}, arg3: true);
		menu.PlacementTarget = this;
		menu.IsOpen = true;
	}

	private void InitResizeRefresh()
	{
		sizeRefresh.Tick += (object s, EventArgs e) =>
		{
			sizeRefresh.Stop();
			ApplySizeRefresh();
		};
	}

	private void QueueSizeRefresh()
	{
		if (Tiles != null && !resizingPanel && !refreshingSize && (IsVisible || preparingView) && !sizeRefresh.IsEnabled)
		{
			sizeRefresh.Start();
		}
	}

	private void ApplySizeRefresh()
	{
		if (Tiles == null || resizingPanel || refreshingSize || (!IsVisible && !preparingView))
		{
			return;
		}
		refreshingSize = true;
		try
		{
			int num = BoardIconRules.Columns((Tiles.ActualWidth > 0.0) ? Tiles.ActualWidth : (Width - 104.0), C.Prefs.BoardIconSize, C.Prefs.BoardIconGap);
			if (iconsDirty || columns != num)
			{
				iconsDirty = false;
				columns = num;
				Tiles.Columns = num;
				Render();
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			SetStatus("面板刷新未完成，请重新打开面板。");
		}
		finally
		{
			refreshingSize = false;
		}
	}

	internal void CheckResizeRefresh()
	{
		Open();
		string text = Search.Text;
		TextBox search = Search;
		UniformGrid tiles = Tiles;
		double[] array = new double[4] { 900.0, 1200.0, 920.0, 1260.0 };
		foreach (double width in array)
		{
			Width = width;
			Height = 620.0;
			UpdateLayout();
			QueueSizeRefresh();
			ApplySizeRefresh();
			UpdateLayout();
		}
		sizeRefresh.Stop();
		if (search != Search || tiles != Tiles || Search.Text != text)
		{
			throw new Exception("缩放不应重建搜索框或图标宿主");
		}
		Hide();
	}
}
