using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GlassBoard;

public sealed class WidgetWindow : GlassWindow
{
	private TextBlock weekday;

	private TextBlock clockHours;

	private TextBlock clockMinutes;

	private TextBlock clockSeconds;

	private FrameworkElement weatherIcon;

	private string musicLayoutSignature = "";

	private readonly WidgetManager M;

	private readonly Thumb moveGrip;

	private readonly Thumb resizeGrip;

	public readonly int PanelId;

	public List<string> Kinds = new List<string>();

	private bool stopped;

	private bool editingNotes;

	private bool building;

	private bool resizing;

	private string signature = "";

	private StackPanel stack;

	private ScrollViewer viewport;

	private Viewbox contentScale;

	private double minimumWidgetHeight = 200.0;

	private ImageSource shownArtwork;

	private bool? shownPlaying;

	private TextBlock clock;

	private TextBlock date;

	private TextBlock clockStatus;

	private TextBlock weatherTemperature;

	private TextBlock weatherPlace;

	private TextBlock weatherDetails;

	private TextBlock weatherStatus;

	private TextBlock musicTitle;

	private TextBlock musicArtist;

	private TextBlock musicStatus;

	private TextBlock noteStatus;

	private Border cover;

	private Button play;

	private ComboBox notePicker;

	private TextBox noteTitle;

	private TextBox noteBody;

	private NoteItem currentNote;

	private TextBlock posterDay;

	private TextBlock posterMonth;

	private TextBlock orbitLabel;

	private RotateTransform hourRotation;

	private RotateTransform minuteRotation;

	private System.Windows.Shapes.Path orbitArc;

	private string clockStamp = "";

	private bool BareShell => Kinds.Any((string k) => StyleFor(k).Frameless);

	private string AppearanceSignature => string.Join("|", Kinds.Select((string k) => StyleFor(k).Signature())) + "/" + M.C.Prefs.Widgets.Locked;

	public bool IsStopped => stopped;

	private bool ControlsHidden => M.C.Prefs.Widgets.Position(PanelId).HideControls ?? M.C.Prefs.Widgets.HideControls;

	private WidgetStyle StyleFor(string kind)
	{
		return M.C.Prefs.Widgets.Style(kind);
	}

	private void InitializeBareDragging()
	{
		Root.PreviewMouseLeftButtonDown += (object a, MouseButtonEventArgs e) =>
		{
			if (BareShell && !M.C.Prefs.Widgets.Locked && e.ClickCount == 1 && e.ButtonState == MouseButtonState.Pressed)
			{
				DependencyObject dependencyObject = e.OriginalSource as DependencyObject;
				while (dependencyObject != null && dependencyObject != Root)
				{
					if (dependencyObject is ButtonBase || dependencyObject is TextBoxBase || dependencyObject is ComboBox || dependencyObject is Thumb || dependencyObject is ScrollBar)
					{
						return;
					}
					dependencyObject = ((dependencyObject is Visual) ? VisualTreeHelper.GetParent(dependencyObject) : LogicalTreeHelper.GetParent(dependencyObject));
				}
				if (dependencyObject == Root)
				{
					e.Handled = true;
					try
					{
						DragMove();
						Rect rect = DesktopWindowPolicy.WorkArea(this);
						Left = FeatureRules.Position(Left, rect.Left, rect.Width, Width);
						Top = FeatureRules.Position(Top, rect.Top, rect.Height, Height);
						WidgetPanelPosition widgetPanelPosition = M.C.Prefs.Widgets.Position(PanelId);
						widgetPanelPosition.Left = Left;
						widgetPanelPosition.Top = Top;
						M.C.Save();
					}
					catch (InvalidOperationException)
					{
					}
				}
			}
		};
	}

	private Brush StyleInk(string kind)
	{
		WidgetStyle widgetStyle = StyleFor(kind);
		Brush brush;
		if (widgetStyle.TextColor == "")
		{
			brush = (M.C.Prefs.Light ? Polish.Ink(light: true) : Brushes.White);
		}
		else
		{
			brush = (Brush)new BrushConverter().ConvertFromString(widgetStyle.TextColor);
		}
		if (brush is SolidColorBrush { Color: var color })
		{
			color.A = (byte)Math.Round(255.0 * widgetStyle.TextOpacity / 100.0);
			SolidColorBrush solidColorBrush2 = new SolidColorBrush(color);
			solidColorBrush2.Freeze();
			return solidColorBrush2;
		}
		return brush;
	}

	private void PaintText(DependencyObject root, WidgetStyle s, Brush ink)
	{
		if (root is TextBlock textBlock)
		{
			textBlock.Foreground = ink;
			textBlock.Opacity = 1.0;
			textBlock.Effect = (s.TextShadow ? new DropShadowEffect
			{
				BlurRadius = 3.0,
				ShadowDepth = 1.0,
				Opacity = 0.65,
				Color = Colors.Black
			} : null);
		}
		if (root is Control control)
		{
			control.Foreground = ink;
		}
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			PaintText(VisualTreeHelper.GetChild(root, i), s, ink);
		}
	}

	private void PaintCard(Border card, string kind)
	{
		PaintText(card, StyleFor(kind), StyleInk(kind));
	}

	private void ApplyCardAppearance()
	{
		if (stack == null)
		{
			return;
		}
		foreach (Border item in stack.Children.OfType<Border>())
		{
			string text = item.Tag as string;
			if (WidgetRules.Kinds.Contains(text))
			{
				WidgetStyle widgetStyle = StyleFor(text);
				double num = WidgetSizing.Background(M.C.Prefs.Widgets, M.C.Prefs.Widgets.Position(PanelId));
				item.Background = ((widgetStyle.Frameless || num == 0.0) ? null : GlassMaterials.Fill(CurrentMaterial, LightTheme, num));
				item.BorderBrush = (widgetStyle.Frameless ? null : Polish.Line(LightTheme));
				item.BorderThickness = new Thickness((!widgetStyle.Frameless) ? 1 : 0);
				PaintCard(item, text);
			}
		}
		if (BareShell)
		{
			ReleaseBackdrop();
			Surface.Visibility = Visibility.Collapsed;
			{
				foreach (Border item2 in from b in Root.Children.OfType<Border>()
					where b.Tag as string == "material"
					select b)
				{
					item2.Visibility = Visibility.Collapsed;
				}
				return;
			}
		}
		Surface.Visibility = Visibility.Visible;
		foreach (Border item3 in from b in Root.Children.OfType<Border>()
			where b.Tag as string == "material"
			select b)
		{
			item3.Visibility = Visibility.Visible;
		}
	}

	private void AddAppearanceMenu(ContextMenu menu, string kind)
	{
		MenuItem menuItem = new MenuItem
		{
			Header = "外观 DIY · 文字颜色 / 无框 / 样式"
		};
		menuItem.Click += (object s, RoutedEventArgs e) =>
		{
			M.C.CustomizeWidgets(kind, this);
		};
		menu.Items.Insert(0, menuItem);
		MenuItem bare = new MenuItem
		{
			Header = "无框模式",
			IsCheckable = true,
			IsChecked = StyleFor(kind).Frameless
		};
		bare.Click += (object s, RoutedEventArgs e) =>
		{
			StyleFor(kind).Frameless = bare.IsChecked;
			M.C.Save();
			M.Sync();
		};
		menu.Items.Insert(1, bare);
	}

	private void ClockDesign(StackPanel parent)
	{
		WidgetStyle widgetStyle = StyleFor("clock");
		ResetAdvancedClock();
		weekday = (clockHours = (clockMinutes = null));
		clock = new TextBlock
		{
			FontSize = widgetStyle.ClockSize,
			FontWeight = FontWeights.Light,
			Foreground = TextBrush
		};
		date = new TextBlock
		{
			FontSize = 13.0,
			Margin = new Thickness(2.0, 4.0, 0.0, 0.0),
			Foreground = MutedBrush
		};
		clockStatus = new TextBlock
		{
			FontSize = 11.0,
			Margin = new Thickness(2.0, 9.0, 0.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis,
			Foreground = MutedBrush
		};
		if (!string.IsNullOrEmpty(widgetStyle.Font))
		{
			clock.FontFamily = new FontFamily(widgetStyle.Font);
		}
		StackPanel stackPanel = new StackPanel
		{
			Cursor = Cursors.SizeAll
		};
		if (widgetStyle.ClockStyle >= 5)
		{
			AdvancedClock(stackPanel, widgetStyle);
		}
		else if (widgetStyle.ClockStyle == 1)
		{
			Grid grid = new Grid
			{
				Height = 126.0
			};
			weekday = new TextBlock
			{
				FontFamily = new FontFamily("Segoe Script"),
				FontSize = 70.0,
				FontStyle = FontStyles.Italic,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
			grid.Children.Add(new Viewbox
			{
				Child = weekday,
				Stretch = Stretch.Uniform,
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
				MaxHeight = 112.0
			});
			StackPanel stackPanel2 = new StackPanel
			{
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0.0, 18.0, 0.0, 0.0)
			};
			TextBlock textBlock = clock;
			HorizontalAlignment horizontalAlignment = (date.HorizontalAlignment = HorizontalAlignment.Center);
			textBlock.HorizontalAlignment = horizontalAlignment;
			stackPanel2.Children.Add(clock);
			stackPanel2.Children.Add(date);
			grid.Children.Add(stackPanel2);
			stackPanel.Children.Add(grid);
		}
		else if (widgetStyle.ClockStyle == 4)
		{
			StackPanel stackPanel3 = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Center
			};
			clockHours = new TextBlock
			{
				FontSize = widgetStyle.ClockSize,
				FontFamily = clock.FontFamily,
				FontWeight = FontWeights.Light
			};
			clockMinutes = new TextBlock
			{
				FontSize = widgetStyle.ClockSize,
				FontFamily = clock.FontFamily,
				FontWeight = FontWeights.Light
			};
			TextBlock[] array = new TextBlock[2] { clockHours, clockMinutes };
			foreach (TextBlock child in array)
			{
				stackPanel3.Children.Add(new Border
				{
					CornerRadius = new CornerRadius(12.0),
					Padding = new Thickness(14.0, 8.0, 14.0, 8.0),
					Margin = new Thickness(3.0),
					Background = new SolidColorBrush(Color.FromArgb(35, 150, 160, 180)),
					Child = child
				});
			}
			stackPanel.Children.Add(stackPanel3);
			date.HorizontalAlignment = HorizontalAlignment.Center;
			stackPanel.Children.Add(date);
		}
		else
		{
			if (widgetStyle.ClockStyle == 3)
			{
				weekday = new TextBlock
				{
					FontSize = 28.0,
					FontWeight = FontWeights.SemiBold
				};
				stackPanel.Children.Add(weekday);
			}
			if (widgetStyle.ClockStyle == 2)
			{
				clock.FontWeight = FontWeights.UltraLight;
				clock.HorizontalAlignment = HorizontalAlignment.Center;
				date.HorizontalAlignment = HorizontalAlignment.Center;
			}
			stackPanel.Children.Add(clock);
			stackPanel.Children.Add(date);
		}
		date.Visibility = ((!widgetStyle.ShowDate) ? Visibility.Collapsed : Visibility.Visible);
		if (!widgetStyle.Frameless)
		{
			stackPanel.Children.Add(clockStatus);
		}
		stackPanel.MouseLeftButtonDown += (object a, MouseButtonEventArgs e) =>
		{
			if (e.ClickCount != 1 || M.C.Prefs.Widgets.Locked)
			{
				return;
			}
			e.Handled = true;
			try
			{
				DragMove();
				Rect rect = DesktopWindowPolicy.WorkArea(this);
				Left = FeatureRules.Position(Left, rect.Left, rect.Width, Width);
				Top = FeatureRules.Position(Top, rect.Top, rect.Height, Height);
				WidgetPanelPosition widgetPanelPosition = M.C.Prefs.Widgets.Position(PanelId);
				widgetPanelPosition.Left = Left;
				widgetPanelPosition.Top = Top;
				M.C.Save();
				Backdrop(LightTheme);
				ApplyWidgetGlass();
			}
			catch (InvalidOperationException)
			{
			}
		};
		parent.Children.Add(stackPanel);
	}

	private static bool SetText(TextBlock text, string value)
	{
		if (text == null || text.Text == value)
		{
			return false;
		}
		text.Text = value;
		return true;
	}

	private void ClockTick()
	{
		WidgetStyle widgetStyle = StyleFor("clock");
		DateTime now = DateTime.Now;
		CultureInfo cultureInfo = CultureInfo.GetCultureInfo("en-US");
		SetText(clock, now.ToString(widgetStyle.Hour24 ? "HH:mm:ss" : "hh:mm:ss tt", cultureInfo));
		SetText(clockHours, now.ToString(widgetStyle.Hour24 ? "HH" : "hh"));
		SetText(clockMinutes, now.ToString("mm") + (widgetStyle.Hour24 ? "" : (" " + now.ToString("tt", cultureInfo))));
		SetText(clockSeconds, now.ToString("ss"));
		if (widgetStyle.ClockStyle >= 5)
		{
			AdvancedClockTick(now, widgetStyle);
		}
		else
		{
			SetText(date, (widgetStyle.ClockStyle == 1) ? now.ToString("dd MMMM", cultureInfo) : now.ToString("M月d日  dddd"));
			SetText(weekday, now.ToString("dddd", cultureInfo));
		}
		if (weekday != null)
		{
			weekday.Opacity = ((widgetStyle.ClockStyle == 1) ? 0.3 : 1.0);
		}
		if (clockStatus != null)
		{
			SetText(clockStatus, ClockRules.Display(M.C.Prefs.Widgets, DateTime.UtcNow));
			clockStatus.ToolTip = clockStatus.Text;
		}
	}

	private Button MusicGlyph(string icon, Action action, double size, bool primary)
	{
		int musicButtons = StyleFor("music").MusicButtons;
		Button button = Button("", (object a, RoutedEventArgs e) =>
		{
			action();
		}, (musicButtons == 4) ? (size + 12.0) : size);
		button.Height = size;
		button.Margin = new Thickness(3.0);
		button.Tag = "musicTransport";
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(Border));
		frameworkElementFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(musicButtons switch
		{
			0 => size / 2.0, 
			4 => size / 2.0, 
			3 => 9.0, 
			_ => 0.0, 
		}));
		DependencyProperty backgroundProperty = Border.BackgroundProperty;
		SolidColorBrush value;
		switch (musicButtons)
		{
		default:
			value = ((primary && StyleFor("music").TextColor == "") ? Brushes.White : new SolidColorBrush(Color.FromArgb(35, 180, 190, 210)));
			break;
		case 4:
			value = new SolidColorBrush(Color.FromArgb(44, 180, 200, 240));
			break;
		case 1:
		case 2:
			value = new SolidColorBrush(Color.FromArgb(1, byte.MaxValue, byte.MaxValue, byte.MaxValue));
			break;
		}
		frameworkElementFactory.SetValue(backgroundProperty, value);
		frameworkElementFactory.SetValue(Border.BorderBrushProperty, (musicButtons == 4) ? Polish.Line(LightTheme) : Brushes.Transparent);
		frameworkElementFactory.SetValue(Border.BorderThicknessProperty, new Thickness((musicButtons == 4) ? 1 : 0));
		FrameworkElementFactory frameworkElementFactory2 = new FrameworkElementFactory(typeof(ContentPresenter));
		frameworkElementFactory2.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		frameworkElementFactory2.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		frameworkElementFactory.AppendChild(frameworkElementFactory2);
		button.Template = new ControlTemplate(typeof(Button))
		{
			VisualTree = frameworkElementFactory
		};
		button.Content = TransportIcon(icon, primary);
		return button;
	}

	private FrameworkElement TransportIcon(string icon, bool primary)
	{
		int musicButtons = StyleFor("music").MusicButtons;
		Brush brush = ((primary && musicButtons == 0 && StyleFor("music").TextColor == "") ? Polish.Ink(light: true) : StyleInk("music"));
		FrameworkElement frameworkElement = Polish.Icon(icon, brush, primary ? 22 : 17);
		System.Windows.Shapes.Path path = frameworkElement as System.Windows.Shapes.Path;
		if (musicButtons == 1 && path != null)
		{
			path.Stroke = brush;
			path.StrokeThickness = 1.4;
			path.Fill = Brushes.Transparent;
		}
		return frameworkElement;
	}

	private FrameworkElement WeatherSymbol(string icon)
	{
		string text = icon switch
		{
			"cloud" => "M 3,15 C -1,8 5,5 7,7 C 9,0 17,3 16,8 C 22,8 22,15 17,15 Z", 
			"fog" => "M 2,6 L 18,6 M 0,10 L 20,10 M 3,14 L 17,14 M 1,18 L 19,18", 
			"storm" => "M 3,12 C -1,6 5,4 7,6 C 9,0 17,3 16,7 C 22,7 22,12 17,12 M 11,10 L 7,16 L 12,16 L 9,22", 
			"snow" => "M 10,1 L 10,19 M 2,5 L 18,15 M 2,15 L 18,5 M 7,3 L 10,6 L 13,3 M 7,17 L 10,14 L 13,17", 
			"rain" => "M 3,13 C -1,6 5,4 7,6 C 9,0 17,3 16,7 C 22,7 22,13 17,13 Z M 6,16 L 4,20 M 12,16 L 10,20 M 18,16 L 16,20", 
			_ => null, 
		};
		if (text == null)
		{
			return Polish.Icon((icon == "sun") ? "sun" : "calendar", StyleInk("weather"), 44.0);
		}
		return new System.Windows.Shapes.Path
		{
			Data = Geometry.Parse(text),
			Stroke = StyleInk("weather"),
			StrokeThickness = 1.5,
			Fill = Brushes.Transparent,
			StrokeLineJoin = PenLineJoin.Round,
			Width = 44.0,
			Height = 44.0,
			Stretch = Stretch.Uniform
		};
	}

	public WidgetWindow(WidgetManager manager, int id)
		: base(29.0)
	{
		M = manager;
		PanelId = id;
		InitializeBareDragging();
		Title = "GlassBoard 组件面板 " + (char)(65 + id);
		ShowActivated = false;
		moveGrip = new Thumb
		{
			Width = 48.0,
			Height = 5.0,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0.0, 9.0, 0.0, 0.0),
			Cursor = Cursors.SizeAll,
			ToolTip = "拖动组件面板"
		};
		moveGrip.Template = GlassWindow.GripTemplate();
		Root.Children.Add(moveGrip);
		moveGrip.DragDelta += (object s, DragDeltaEventArgs e) =>
		{
			if (!M.C.Prefs.Widgets.Locked)
			{
				Rect rect = DesktopWindowPolicy.WorkArea(this, pointer: true);
				Left = FeatureRules.Position(Left + e.HorizontalChange, rect.Left, rect.Width, Width);
				Top = FeatureRules.Position(Top + e.VerticalChange, rect.Top, rect.Height, Height);
			}
		};
		moveGrip.DragCompleted += (object s, DragCompletedEventArgs e) =>
		{
			WidgetPanelPosition widgetPanelPosition = M.C.Prefs.Widgets.Position(PanelId);
			widgetPanelPosition.Left = Left;
			widgetPanelPosition.Top = Top;
			if (PanelId == 0)
			{
				M.C.Prefs.Placement.WidgetLeft = Left;
				M.C.Prefs.Placement.WidgetTop = Top;
			}
			M.C.Save();
			Backdrop(LightTheme);
			ApplyWidgetGlass();
		};
		resizeGrip = new Thumb
		{
			Width = 20.0,
			Height = 20.0,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(0.0, 0.0, 7.0, 7.0),
			Cursor = Cursors.SizeNWSE,
			ToolTip = "拖动调整组件大小；内容自动适配，最小宽度 240，最小高度按内容计算"
		};
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(Border));
		frameworkElementFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
		FrameworkElementFactory frameworkElementFactory2 = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
		frameworkElementFactory2.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 4,16 L 16,4 M 10,16 L 16,10"));
		frameworkElementFactory2.SetValue(Shape.StrokeProperty, Polish.Muted(light: false));
		frameworkElementFactory2.SetValue(Shape.StrokeThicknessProperty, 1.5);
		frameworkElementFactory.AppendChild(frameworkElementFactory2);
		resizeGrip.Template = new ControlTemplate(typeof(Thumb))
		{
			VisualTree = frameworkElementFactory
		};
		Root.Children.Add(resizeGrip);
		resizeGrip.DragDelta += (object s, DragDeltaEventArgs e) =>
		{
			if (!M.C.Prefs.Widgets.Locked)
			{
				Rect rect = DesktopWindowPolicy.WorkArea(this);
				WidgetPanelPosition widgetPanelPosition = M.C.Prefs.Widgets.Position(PanelId);
				widgetPanelPosition.Width = Math.Max(Math.Min(240.0, rect.Width - 30.0), Math.Min(rect.Width - 30.0, Width + e.HorizontalChange));
				widgetPanelPosition.Height = Math.Max(Math.Min(minimumWidgetHeight, rect.Height - 30.0), Math.Min(rect.Height - 30.0, Height + e.VerticalChange));
				Width = widgetPanelPosition.Width.Value;
				FitContent();
				Left = FeatureRules.Position(Left, rect.Left, rect.Width, Width);
				Top = FeatureRules.Position(Top, rect.Top, rect.Height, Height);
			}
		};
		resizeGrip.DragCompleted += (object s, DragCompletedEventArgs e) =>
		{
			WidgetPanelPosition widgetPanelPosition = M.C.Prefs.Widgets.Position(PanelId);
			widgetPanelPosition.Left = Left;
			widgetPanelPosition.Top = Top;
			M.C.Save();
			Backdrop(LightTheme);
			ApplyWidgetGlass();
		};
		Closed += (object s, EventArgs e) =>
		{
			// 只有用户主动关闭才禁用组件；程序退出/系统关机时保持启用状态
			bool disable = !stopped && !M.C.ShuttingDown;
			stopped = true;
			M.Closed(this, disable);
		};
	}

	public void Stop()
	{
		if (!stopped)
		{
			stopped = true;
			Close();
		}
	}

	// 系统会话结束（关机/注销）：在窗口被销毁前标记退出，避免组件被当作"用户关闭"而禁用
	public void MarkSessionEnding()
	{
		M.C.ShuttingDown = true;
	}

	public void UpdateOptions(List<string> kinds)
	{
		WidgetOptions widgets = M.C.Prefs.Widgets;
		Kinds = kinds.ToList();
		bool flag = signature != string.Join(",", Kinds) + "/" + M.C.Prefs.Light + "/" + widgets.CompactMusic + "/" + ControlsHidden + "/" + AppearanceSignature;
		LightTheme = M.C.Prefs.Light;
		MotionEnabled = M.C.Prefs.Dock.MotionEnabled;
		MotionLevel = M.C.Prefs.Dock.MotionLevel;
		ConfigureGlass(M.C.Prefs.Dock, widgets.Material);
		GlassPercent = (BareShell ? 0.0 : WidgetSizing.Background(widgets, widgets.Position(PanelId)));
		Topmost = widgets.Topmost;
		moveGrip.Visibility = ((BareShell || widgets.Locked || Kinds.SequenceEqual(new string[1] { "clock" })) ? Visibility.Collapsed : Visibility.Visible);
		resizeGrip.Visibility = ((BareShell || widgets.Locked) ? Visibility.Collapsed : Visibility.Visible);
		Polish.Apply(this, LightTheme);
		Rect rect = DesktopWindowPolicy.WorkArea(this);
		Width = Math.Max(Math.Min(240.0, rect.Width - 30.0), Math.Min(widgets.Position(PanelId).Width ?? Math.Min(450.0, rect.Width * 0.32), rect.Width - 30.0));
		if (flag)
		{
			Build();
			signature = string.Join(",", Kinds) + "/" + LightTheme + "/" + widgets.CompactMusic + "/" + ControlsHidden + "/" + AppearanceSignature;
		}
		Tick();
		UpdateMusic();
		UpdateWeather();
		FitContent();
		WidgetPanelPosition widgetPanelPosition = widgets.Position(PanelId);
		double? num = widgetPanelPosition.Left;
		double? num2 = widgetPanelPosition.Top;
		if (PanelId == 0)
		{
			num = num ?? M.C.Prefs.Placement.WidgetLeft;
			num2 = num2 ?? M.C.Prefs.Placement.WidgetTop;
		}
		Left = FeatureRules.Position(num ?? (rect.Right - Width - 18.0 - (double)(PanelId % 2) * (Width - 8.0)), rect.Left, rect.Width, Width);
		Top = FeatureRules.Position(num2 ?? (rect.Top + 18.0 + (double)(PanelId / 2 * 245)), rect.Top, rect.Height, Height);
		Backdrop(LightTheme);
		ApplyWidgetGlass();
		RootWidgetMenu();
		Tick();
		UpdateMusic();
		UpdateWeather();
	}

	private void FitContent()
	{
		if (stack == null || contentScale == null || building || resizing)
		{
			return;
		}
		resizing = true;
		try
		{
			Rect rect = DesktopWindowPolicy.WorkArea(this);
			double num = Math.Max(100.0, Width - 28.0);
			stack.Width = 390.0;
			stack.Measure(new Size(422.0, double.PositiveInfinity));
			double num2 = Math.Ceiling(stack.DesiredSize.Height);
			minimumWidgetHeight = WidgetSizing.MinimumHeight(num2, rect.Height - 30.0);
			double num3 = num2 * (num / 422.0) + 28.0;
			WidgetPanelPosition widgetPanelPosition = M.C.Prefs.Widgets.Position(PanelId);
			double num4 = (BareShell ? Math.Min(rect.Height - 30.0, Math.Max(72.0, num3)) : Math.Max(minimumWidgetHeight, Math.Min(rect.Height - 30.0, widgetPanelPosition.Height ?? num3)));
			contentScale.Width = num;
			contentScale.Height = Math.Max(BareShell ? 44 : 100, num4 - 28.0);
			viewport.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
			viewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
			if (!BareShell && widgetPanelPosition.Height.HasValue)
			{
				widgetPanelPosition.Height = num4;
			}
			if (Math.Abs(Height - num4) > 0.5 || double.IsNaN(Height))
			{
				Height = num4;
				if (IsVisible)
				{
					Top = FeatureRules.Position(Top, rect.Top, rect.Height, Height);
					Backdrop(LightTheme);
					ApplyWidgetGlass();
				}
			}
		}
		finally
		{
			resizing = false;
		}
	}

	private void ApplyWidgetGlass()
	{
		SetGlassOpacity(BareShell ? 0.0 : WidgetSizing.Background(M.C.Prefs.Widgets, M.C.Prefs.Widgets.Position(PanelId)));
		Surface.BorderBrush = ((GlassPercent == 0.0) ? Brushes.Transparent : Polish.Line(LightTheme));
		ApplyCardAppearance();
	}

	private Button Small(string text, Action action, double width = 30.0)
	{
		Button button = Button(text, (object s, RoutedEventArgs e) =>
		{
			action();
		}, width);
		button.Height = 28.0;
		button.Margin = new Thickness(2.0, 0.0, 2.0, 0.0);
		button.FontSize = 12.0;
		return button;
	}

	private Button Glyph(string icon, Action action, double size = 34.0, bool primary = false)
	{
		Button button = Button("", (object s, RoutedEventArgs e) =>
		{
			action();
		}, size);
		button.Height = size;
		button.Margin = new Thickness(3.0);
		button.Content = Polish.Icon(icon, primary ? Polish.Ink(light: true) : TextBrush, (size >= 48.0) ? 22 : 17);
		if (primary)
		{
			button.Style = Polish.ButtonStyle(light: true);
			button.Background = new LinearGradientBrush(Color.FromRgb(224, 234, byte.MaxValue), Colors.White, 90.0);
			button.BorderBrush = Brushes.Transparent;
			FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(Border));
			frameworkElementFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2.0));
			frameworkElementFactory.SetBinding(Border.BackgroundProperty, new Binding("Background")
			{
				RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
			});
			FrameworkElementFactory frameworkElementFactory2 = new FrameworkElementFactory(typeof(ContentPresenter));
			frameworkElementFactory2.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
			frameworkElementFactory2.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
			frameworkElementFactory.AppendChild(frameworkElementFactory2);
			button.Template = new ControlTemplate(typeof(Button))
			{
				VisualTree = frameworkElementFactory
			};
		}
		return button;
	}

	private void Build()
	{
		building = true;
		try
		{
			musicLayoutSignature = "";
			shownArtwork = null;
			shownPlaying = null;
			ContentGrid.Children.Clear();
			clock = (date = (clockStatus = (weatherTemperature = (weatherPlace = (weatherDetails = (weatherStatus = (musicTitle = (musicArtist = (musicStatus = (noteStatus = null))))))))));
			cover = null;
			play = null;
			notePicker = null;
			noteTitle = (noteBody = null);
			currentNote = null;
			stack = new StackPanel
			{
				Margin = (BareShell ? new Thickness(2.0) : new Thickness(16.0, Kinds.SequenceEqual(new string[1] { "clock" }) ? 16 : 25, 16.0, 16.0))
			};
			contentScale = new Viewbox
			{
				Child = stack,
				Stretch = Stretch.Uniform,
				StretchDirection = StretchDirection.Both,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Top
			};
			viewport = new ScrollViewer
			{
				Content = contentScale,
				VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
			};
			ContentGrid.Children.Add(viewport);
			foreach (string kind in Kinds)
			{
				StackPanel stackPanel = new StackPanel();
				Border border = new Border
				{
					Background = Polish.Card(LightTheme),
					BorderBrush = Polish.Line(LightTheme),
					BorderThickness = new Thickness(1.0),
					CornerRadius = new CornerRadius(22.0),
					Tag = kind,
					Padding = (StyleFor(kind).Frameless ? new Thickness(4.0) : new Thickness(18.0)),
					Margin = new Thickness(0.0, 0.0, 0.0, 14.0),
					Child = stackPanel
				};
				stack.Children.Add(border);
				if (!StyleFor(kind).Frameless)
				{
					Header(stackPanel, kind);
				}
				switch (kind)
				{
				case "clock":
					ClockDesign(stackPanel);
					break;
				case "weather":
					Weather(stackPanel);
					break;
				case "music":
					Music(stackPanel);
					break;
				default:
					Notes(stackPanel);
					break;
				}
				PaintCard(border, kind);
			}
			if (stack.Children.Count > 0)
			{
				((Border)stack.Children[stack.Children.Count - 1]).Margin = new Thickness(0.0);
			}
		}
		finally
		{
			building = false;
		}
	}

	private ContextMenu WidgetMenu(string kind)
	{
		ContextMenu contextMenu = MenuTheme.Create(LightTheme, M.C.Prefs.Dock.Accent, M.C.Prefs.Widgets.Material.Tint, (int)WidgetSizing.Background(M.C.Prefs.Widgets, M.C.Prefs.Widgets.Position(PanelId)));
		if (kind == "clock")
		{
			MenuItem menuItem = new MenuItem
			{
				Header = "闹钟与倒计时"
			};
			menuItem.Click += (object ss, RoutedEventArgs ee) =>
			{
				ClockSettings();
			};
			contextMenu.Items.Add(menuItem);
			contextMenu.Items.Add(new Separator());
		}
		if (kind == "music")
		{
			MenuItem menuItem2 = new MenuItem
			{
				Header = "系统音乐模式",
				IsCheckable = true,
				IsChecked = !M.Music.Local
			};
			menuItem2.Click += (object ss, RoutedEventArgs ee) =>
			{
				M.Music.Mode(local: false);
			};
			contextMenu.Items.Add(menuItem2);
			MenuItem menuItem3 = new MenuItem
			{
				Header = "本地音乐模式",
				IsCheckable = true,
				IsChecked = M.Music.Local
			};
			menuItem3.Click += (object ss, RoutedEventArgs ee) =>
			{
				M.Music.Mode(local: true);
			};
			contextMenu.Items.Add(menuItem3);
			MenuItem compatibility = new MenuItem
			{
				Header = "自动补充音乐封面",
				IsCheckable = true,
				IsChecked = M.C.Prefs.Widgets.AutoNativeMediaCompatibility
			};
			compatibility.Click += (object ss, RoutedEventArgs ee) =>
			{
				M.C.Prefs.Widgets.AutoNativeMediaCompatibility = compatibility.IsChecked;
				M.C.Prefs.Widgets.NativeMediaCompatibility = false;
				M.C.Save();
				M.Music.Update();
			};
			contextMenu.Items.Add(compatibility);
			MenuItem menuItem4 = new MenuItem
			{
				Header = "查看音乐读取状态"
			};
			menuItem4.Click += (object ss, RoutedEventArgs ee) =>
			{
				MessageBox.Show(this, M.Music.Diagnostics(), "音乐读取状态");
			};
			contextMenu.Items.Add(menuItem4);
			MenuItem menuItem5 = new MenuItem
			{
				Header = "播放列表"
			};
			menuItem5.Click += (object ss, RoutedEventArgs ee) =>
			{
				Playlist();
			};
			contextMenu.Items.Add(menuItem5);
			MenuItem menuItem6 = new MenuItem
			{
				Header = "添加音乐"
			};
			menuItem6.Click += (object ss, RoutedEventArgs ee) =>
			{
				M.Music.Pick(this, append: true);
			};
			contextMenu.Items.Add(menuItem6);
			contextMenu.Items.Add(new Separator());
		}
		MenuItem menuItem7 = new MenuItem
		{
			Header = "分离为独立面板"
		};
		menuItem7.Click += (object ss, RoutedEventArgs ee) =>
		{
			M.Assign(kind, WidgetRules.FreePanel(M.C.Prefs.Widgets, kind));
		};
		contextMenu.Items.Add(menuItem7);
		for (int num = 0; num < 4; num++)
		{
			int panel = num;
			MenuItem menuItem8 = new MenuItem
			{
				Header = "融合到面板 " + (char)(65 + num),
				IsCheckable = true,
				IsChecked = (WidgetRules.Panel(M.C.Prefs.Widgets, kind) == num)
			};
			menuItem8.Click += (object ss, RoutedEventArgs ee) =>
			{
				M.Assign(kind, panel);
			};
			contextMenu.Items.Add(menuItem8);
		}
		contextMenu.Items.Add(new Separator());
		MenuItem lockPosition = new MenuItem
		{
			Header = "锁定组件位置和尺寸",
			IsCheckable = true,
			IsChecked = M.C.Prefs.Widgets.Locked
		};
		lockPosition.Click += (object ss, RoutedEventArgs ee) =>
		{
			M.C.Prefs.Widgets.Locked = lockPosition.IsChecked;
			M.C.Save();
			M.Sync();
		};
		contextMenu.Items.Add(lockPosition);
		MenuItem menuItem9 = new MenuItem
		{
			Header = "恢复自动尺寸"
		};
		menuItem9.Click += (object ss, RoutedEventArgs ee) =>
		{
			WidgetPanelPosition widgetPanelPosition = M.C.Prefs.Widgets.Position(PanelId);
			widgetPanelPosition.Width = (widgetPanelPosition.Height = null);
			M.C.Save();
			M.Sync();
		};
		contextMenu.Items.Add(menuItem9);
		MenuItem newItem = new MenuItem
		{
			Header = "背景透明度（100% 为全透明）",
			IsEnabled = false
		};
		contextMenu.Items.Add(newItem);
		Slider transparency = new Slider
		{
			Width = 190.0,
			Minimum = 0.0,
			Maximum = 100.0,
			Value = 100.0 - WidgetSizing.Background(M.C.Prefs.Widgets, M.C.Prefs.Widgets.Position(PanelId)),
			IsSnapToTickEnabled = true,
			TickFrequency = 5.0
		};
		transparency.ValueChanged += (object ss, RoutedPropertyChangedEventArgs<double> ee) =>
		{
			M.C.Prefs.Widgets.Position(PanelId).OpacityPercent = 100.0 - transparency.Value;
			ApplyWidgetGlass();
		};
		transparency.PreviewMouseLeftButtonUp += (object ss, MouseButtonEventArgs ee) =>
		{
			M.C.Save();
		};
		transparency.KeyUp += (object ss, KeyEventArgs ee) =>
		{
			M.C.Save();
		};
		contextMenu.Items.Add(new MenuItem
		{
			Header = transparency,
			StaysOpenOnClick = true
		});
		contextMenu.Closed += (object ss, RoutedEventArgs ee) =>
		{
			M.C.Save();
		};
		MenuItem menuItem10 = new MenuItem
		{
			Header = "组件设置"
		};
		menuItem10.Click += (object ss, RoutedEventArgs ee) =>
		{
			M.C.ConfigureWidgets();
		};
		contextMenu.Items.Add(menuItem10);
		MenuItem menuItem11 = new MenuItem
		{
			Header = (ControlsHidden ? "显示右上角关闭和更多按钮" : "隐藏右上角关闭和更多按钮")
		};
		menuItem11.Click += (object s, RoutedEventArgs e) =>
		{
			M.C.Prefs.Widgets.Position(PanelId).HideControls = !ControlsHidden;
			M.C.Save();
			M.Sync();
		};
		contextMenu.Items.Insert(0, menuItem11);
		AddAppearanceMenu(contextMenu, kind);
		return contextMenu;
	}

	private void RootWidgetMenu()
	{
		ContextMenu contextMenu = MenuTheme.Create(LightTheme, M.C.Prefs.Dock.Accent, M.C.Prefs.Widgets.Material.Tint, (int)WidgetSizing.Background(M.C.Prefs.Widgets, M.C.Prefs.Widgets.Position(PanelId)));
		MenuItem menuItem = new MenuItem
		{
			Header = (ControlsHidden ? "显示右上角关闭和更多按钮" : "隐藏右上角关闭和更多按钮")
		};
		menuItem.Click += (object ss, RoutedEventArgs ee) =>
		{
			M.C.Prefs.Widgets.Position(PanelId).HideControls = !ControlsHidden;
			M.C.Save();
			M.Sync();
		};
		contextMenu.Items.Add(menuItem);
		contextMenu.Closed += (object ss, RoutedEventArgs ee) =>
		{
			M.C.Save();
		};
		foreach (string kind in Kinds)
		{
			MenuItem menuItem2 = new MenuItem
			{
				Header = WidgetRules.Names[Array.IndexOf(WidgetRules.Kinds, kind)]
			};
			ContextMenu contextMenu2 = WidgetMenu(kind);
			while (contextMenu2.Items.Count > 0)
			{
				object newItem = contextMenu2.Items[0];
				contextMenu2.Items.RemoveAt(0);
				menuItem2.Items.Add(newItem);
			}
			contextMenu.Items.Add(menuItem2);
		}
		ContextMenu = contextMenu;
	}

	private void Header(StackPanel parent, string kind)
	{
		int num = Array.IndexOf(WidgetRules.Kinds, kind);
		Grid grid = new Grid
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
		};
		TextBlock textBlock = new TextBlock
		{
			Text = WidgetRules.Names[num],
			Foreground = MutedBrush,
			FontSize = 11.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Left,
			Cursor = Cursors.SizeAll,
			ToolTip = "拖动标题移动组件"
		};
		textBlock.MouseLeftButtonDown += (object s, MouseButtonEventArgs e) =>
		{
			if (!M.C.Prefs.Widgets.Locked && e.ButtonState == MouseButtonState.Pressed)
			{
				DragMove();
				WidgetPanelPosition widgetPanelPosition = M.C.Prefs.Widgets.Position(PanelId);
				Rect rect = DesktopWindowPolicy.WorkArea(this);
				Left = FeatureRules.Position(Left, rect.Left, rect.Width, Width);
				Top = FeatureRules.Position(Top, rect.Top, rect.Height, Height);
				widgetPanelPosition.Left = Left;
				widgetPanelPosition.Top = Top;
				M.C.Save();
				Backdrop(LightTheme);
				ApplyWidgetGlass();
			}
		};
		grid.Children.Add(textBlock);
		StackPanel stackPanel = new StackPanel
		{
			Tag = "widgetControls",
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right
		};
		Button options = Small("•••", () =>
		{
		});
		options.ToolTip = "融合、分离与组件设置";
		options.Click += (object s, RoutedEventArgs e) =>
		{
			ContextMenu contextMenu = WidgetMenu(kind);
			options.ContextMenu = contextMenu;
			contextMenu.PlacementTarget = options;
			contextMenu.IsOpen = true;
		};
		stackPanel.Children.Add(options);
		Button button = Glyph("close", () =>
		{
			M.Enable(kind, value: false);
		}, 26.0);
		button.ToolTip = "关闭这个组件";
		stackPanel.Children.Add(button);
		stackPanel.Visibility = (ControlsHidden ? Visibility.Collapsed : Visibility.Visible);
		grid.Children.Add(stackPanel);
		parent.Children.Add(grid);
	}

	private void Clock(StackPanel parent)
	{
		clock = new TextBlock
		{
			FontSize = 48.0,
			FontWeight = FontWeights.Light,
			Foreground = TextBrush
		};
		date = new TextBlock
		{
			FontSize = 12.0,
			Foreground = MutedBrush,
			Margin = new Thickness(3.0, 5.0, 0.0, 0.0)
		};
		clockStatus = new TextBlock
		{
			FontSize = 11.0,
			Foreground = MutedBrush,
			Margin = new Thickness(3.0, 9.0, 0.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		StackPanel stackPanel = new StackPanel
		{
			Background = Brushes.Transparent,
			Cursor = Cursors.SizeAll,
			ToolTip = "拖动时间或日期移动组件；锁定时不可移动"
		};
		stackPanel.Children.Add(clock);
		stackPanel.Children.Add(date);
		stackPanel.Children.Add(clockStatus);
		stackPanel.MouseLeftButtonDown += (object s, MouseButtonEventArgs e) =>
		{
			if (e.ClickCount != 1 || M.C.Prefs.Widgets.Locked)
			{
				return;
			}
			e.Handled = true;
			try
			{
				DragMove();
				Rect rect = DesktopWindowPolicy.WorkArea(this);
				Left = FeatureRules.Position(Left, rect.Left, rect.Width, Width);
				Top = FeatureRules.Position(Top, rect.Top, rect.Height, Height);
				WidgetPanelPosition widgetPanelPosition = M.C.Prefs.Widgets.Position(PanelId);
				widgetPanelPosition.Left = Left;
				widgetPanelPosition.Top = Top;
				M.C.Save();
				Backdrop(LightTheme);
				ApplyWidgetGlass();
			}
			catch (InvalidOperationException)
			{
			}
		};
		parent.Children.Add(stackPanel);
	}

	private void ClockSettings()
	{
		WidgetOptions o = M.C.Prefs.Widgets;
		Window w = FeatureUi.Dialog("闹钟与倒计时", this, 540.0, 650.0);
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(24.0)
		};
		w.Content = new ScrollViewer
		{
			Content = stackPanel,
			VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "闹钟",
			FontSize = 21.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
		});
		ListBox list = new ListBox
		{
			Height = 150.0
		};
		stackPanel.Children.Add(list);
		Action fill = () =>
		{
			int selectedIndex = list.SelectedIndex;
			list.ItemsSource = o.Alarms.Select((ClockAlarm a) => (a.Enabled ? "开启  " : "关闭  ") + a.Hour.ToString("00") + ":" + a.Minute.ToString("00") + " · " + (a.Daily ? "每天" : "单次") + " · " + a.Label).ToList();
			if (selectedIndex >= 0 && selectedIndex < o.Alarms.Count)
			{
				list.SelectedIndex = selectedIndex;
			}
		};
		fill();
		WrapPanel wrapPanel = new WrapPanel
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 12.0)
		};
		stackPanel.Children.Add(wrapPanel);
		wrapPanel.Children.Add(FeatureUi.Button("开启 / 关闭", () =>
		{
			int selectedIndex = list.SelectedIndex;
			if (selectedIndex >= 0)
			{
				ClockAlarm clockAlarm = o.Alarms[selectedIndex];
				clockAlarm.Enabled = !clockAlarm.Enabled;
				if (clockAlarm.Enabled)
				{
					clockAlarm.DueLocal = ClockRules.Next(clockAlarm.Hour, clockAlarm.Minute, DateTime.Now);
				}
				M.C.Save();
				fill();
			}
		}));
		wrapPanel.Children.Add(FeatureUi.Button("删除已选", () =>
		{
			int selectedIndex = list.SelectedIndex;
			if (selectedIndex >= 0)
			{
				o.Alarms.RemoveAt(selectedIndex);
				M.C.Save();
				fill();
			}
		}));
		WrapPanel wrapPanel2 = new WrapPanel();
		stackPanel.Children.Add(wrapPanel2);
		TextBox time = new TextBox
		{
			Text = DateTime.Now.AddMinutes(1.0).ToString("HH:mm"),
			Width = 75.0,
			MinHeight = 38.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
		};
		TextBox label = new TextBox
		{
			Text = "闹钟",
			Width = 180.0,
			MinHeight = 38.0
		};
		wrapPanel2.Children.Add(time);
		wrapPanel2.Children.Add(label);
		CheckBox daily = new CheckBox
		{
			Content = "每天重复",
			IsChecked = false,
			Margin = new Thickness(0.0, 10.0, 0.0, 8.0)
		};
		stackPanel.Children.Add(daily);
		stackPanel.Children.Add(FeatureUi.Button("添加闹钟", () =>
		{
			if (!DateTime.TryParseExact(time.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
			{
				MessageBox.Show(w, "请填写 24 小时制时间，例如 08:30。");
			}
			else if (o.Alarms.Count >= 50)
			{
				MessageBox.Show(w, "最多添加 50 个闹钟。");
			}
			else
			{
				o.Alarms.Add(new ClockAlarm
				{
					Hour = result.Hour,
					Minute = result.Minute,
					Label = (string.IsNullOrWhiteSpace(label.Text) ? "闹钟" : label.Text.Trim()),
					Daily = (daily.IsChecked == true),
					DueLocal = ClockRules.Next(result.Hour, result.Minute, DateTime.Now)
				});
				M.C.Save();
				fill();
			}
		}));
		stackPanel.Children.Add(new TextBlock
		{
			Text = "倒计时",
			FontSize = 21.0,
			Margin = new Thickness(0.0, 22.0, 0.0, 10.0)
		});
		TextBlock countdown = new TextBlock
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
		};
		stackPanel.Children.Add(countdown);
		TextBox minutes = new TextBox
		{
			Text = "5",
			Width = 80.0,
			MinHeight = 38.0,
			HorizontalAlignment = HorizontalAlignment.Left
		};
		stackPanel.Children.Add(minutes);
		stackPanel.Children.Add(new TextBlock
		{
			Text = "分钟 · 1–1440",
			Margin = new Thickness(0.0, 5.0, 0.0, 8.0)
		});
		WrapPanel wrapPanel3 = new WrapPanel();
		stackPanel.Children.Add(wrapPanel3);
		wrapPanel3.Children.Add(FeatureUi.Button("开始", () =>
		{
			if (!int.TryParse(minutes.Text, out var result) || result < 1 || result > 1440)
			{
				MessageBox.Show(w, "请输入 1 到 1440 分钟。");
			}
			else
			{
				ClockRules.Start(o, result * 60, DateTime.UtcNow);
				M.C.Save();
			}
		}));
		wrapPanel3.Children.Add(FeatureUi.Button("暂停 / 继续", () =>
		{
			if (o.TimerDueUtc.HasValue)
			{
				ClockRules.Pause(o, DateTime.UtcNow);
			}
			else
			{
				ClockRules.Resume(o, DateTime.UtcNow);
			}
			M.C.Save();
		}));
		wrapPanel3.Children.Add(FeatureUi.Button("取消", () =>
		{
			ClockRules.Cancel(o);
			M.C.Save();
		}));
		stackPanel.Children.Add(new TextBlock
		{
			Text = "到时弹窗并播放系统提示音，可稍后 5 分钟再提醒。GlassBoard 需保持运行；隐藏时钟仍会提醒。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 12.0,
			Margin = new Thickness(0.0, 16.0, 0.0, 0.0)
		});
		DispatcherTimer refresh = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		refresh.Tick += (object s, EventArgs e) =>
		{
			countdown.Text = ClockRules.Display(o, DateTime.UtcNow);
		};
		w.Closed += (object s, EventArgs e) =>
		{
			refresh.Stop();
		};
		countdown.Text = ClockRules.Display(o, DateTime.UtcNow);
		refresh.Start();
		w.ShowDialog();
		Tick();
	}

	private void Weather(StackPanel parent)
	{
		weatherPlace = new TextBlock
		{
			Foreground = TextBrush,
			FontSize = 14.0,
			FontWeight = FontWeights.Medium,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		parent.Children.Add(weatherPlace);
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(60.0)
		});
		StackPanel stackPanel = new StackPanel();
		grid.Children.Add(stackPanel);
		weatherTemperature = new TextBlock
		{
			Foreground = TextBrush,
			FontSize = 43.0,
			FontWeight = FontWeights.Light,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		stackPanel.Children.Add(weatherTemperature);
		weatherIcon = new ContentControl
		{
			Content = WeatherSymbol("cloud"),
			Width = 44.0,
			Height = 44.0
		};
		Grid.SetColumn(weatherIcon, 1);
		grid.Children.Add(weatherIcon);
		parent.Children.Add(grid);
		weatherDetails = new TextBlock
		{
			Foreground = TextBrush,
			FontSize = 12.0,
			Margin = new Thickness(0.0, 5.0, 0.0, 8.0)
		};
		parent.Children.Add(weatherDetails);
		Grid grid2 = new Grid();
		weatherStatus = new TextBlock
		{
			Foreground = MutedBrush,
			FontSize = 10.0,
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
			MaxHeight = 56.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		};
		parent.Children.Add(weatherStatus);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right
		};
		stackPanel2.Children.Add(Small("刷新", () =>
		{
			M.Weather.Update(force: true);
		}, 45.0));
		Button button = Small("天气来源", () =>
		{
			Process.Start(new ProcessStartInfo((M.Weather.Snapshot != null && M.Weather.Snapshot.Provider == "wttr.in") ? "https://wttr.in/" : "https://open-meteo.com/")
			{
				UseShellExecute = true
			});
		}, 88.0);
		button.FontSize = 10.0;
		stackPanel2.Children.Add(button);
		grid2.Children.Add(stackPanel2);
		parent.Children.Add(grid2);
	}

	private void Music(StackPanel parent)
	{
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(72.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		cover = new Border
		{
			Width = 72.0,
			Height = 72.0,
			CornerRadius = new CornerRadius(16.0),
			Background = Polish.Card(LightTheme),
			Clip = new RectangleGeometry(new Rect(0.0, 0.0, 72.0, 72.0), 16.0, 16.0)
		};
		grid.Children.Add(cover);
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(14.0, 2.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(stackPanel, 1);
		grid.Children.Add(stackPanel);
		musicTitle = new TextBlock
		{
			Foreground = TextBrush,
			FontSize = 17.0,
			FontWeight = FontWeights.SemiBold,
			TextWrapping = TextWrapping.Wrap,
			MaxHeight = 48.0,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		musicArtist = new TextBlock
		{
			Foreground = MutedBrush,
			FontSize = 12.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0.0, 5.0, 0.0, 0.0)
		};
		stackPanel.Children.Add(musicTitle);
		stackPanel.Children.Add(musicArtist);
		parent.Children.Add(grid);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Center,
			Margin = new Thickness(0.0, 8.0, 0.0, 3.0)
		};
		Button button = MusicGlyph("previous", () =>
		{
			M.Music.Action("previous");
		}, 34.0, primary: false);
		button.ToolTip = "上一首";
		stackPanel2.Children.Add(button);
		play = MusicGlyph("play", () =>
		{
			if (M.Music.Local && M.C.Prefs.Widgets.Playlist.Count == 0)
			{
				M.Music.Pick(this);
			}
			else
			{
				M.Music.Action("toggle");
			}
		}, 44.0, primary: true);
		play.ToolTip = "播放 / 暂停";
		stackPanel2.Children.Add(play);
		Button button2 = MusicGlyph("next", () =>
		{
			M.Music.Action("next");
		}, 34.0, primary: false);
		button2.ToolTip = "下一首";
		stackPanel2.Children.Add(button2);
		parent.Children.Add(stackPanel2);
		musicStatus = new TextBlock
		{
			Foreground = MutedBrush,
			FontSize = 10.0,
			TextWrapping = TextWrapping.Wrap,
			MaxHeight = 42.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		parent.Children.Add(musicStatus);
	}

	private void Playlist()
	{
		if (M.Music == null)
		{
			return;
		}
		Window w = FeatureUi.Dialog("播放列表", this, 540.0, 510.0);
		DockPanel dockPanel = new DockPanel
		{
			Margin = new Thickness(22.0)
		};
		w.Content = dockPanel;
		WrapPanel wrapPanel = new WrapPanel();
		DockPanel.SetDock(wrapPanel, Dock.Bottom);
		dockPanel.Children.Add(wrapPanel);
		ListBox list = new ListBox();
		dockPanel.Children.Add(list);
		Action fill = () =>
		{
			list.ItemsSource = null;
			list.ItemsSource = M.C.Prefs.Widgets.Playlist.Select((string p) => System.IO.Path.GetFileNameWithoutExtension(p)).ToList();
			list.SelectedIndex = M.C.Prefs.Widgets.TrackIndex;
		};
		fill();
		wrapPanel.Children.Add(FeatureUi.Button("添加", () =>
		{
			M.Music.Pick(w, append: true);
			fill();
		}));
		wrapPanel.Children.Add(FeatureUi.Button("播放已选", () =>
		{
			if (list.SelectedIndex >= 0)
			{
				M.Music.Select(list.SelectedIndex);
			}
			fill();
		}));
		wrapPanel.Children.Add(FeatureUi.Button("移除已选", () =>
		{
			if (list.SelectedIndex >= 0)
			{
				M.Music.Remove(list.SelectedIndex);
			}
			fill();
		}));
		list.MouseDoubleClick += (object s, MouseButtonEventArgs e) =>
		{
			if (list.SelectedIndex >= 0)
			{
				M.Music.Select(list.SelectedIndex);
			}
		};
		w.ShowDialog();
	}

	private void Notes(StackPanel parent)
	{
		notePicker = new ComboBox
		{
			MinHeight = 42.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(TextBlock));
		frameworkElementFactory.SetBinding(TextBlock.TextProperty, new Binding("DisplayTitle"));
		frameworkElementFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
		frameworkElementFactory.SetValue(TextBlock.ForegroundProperty, StyleInk("notes"));
		frameworkElementFactory.SetValue(TextBlock.FontSizeProperty, 14.0);
		frameworkElementFactory.SetValue(FrameworkElement.MaxWidthProperty, Math.Max(80.0, Width - 160.0));
		notePicker.ItemTemplate = new DataTemplate
		{
			VisualTree = frameworkElementFactory
		};
		parent.Children.Add(notePicker);
		noteTitle = new TextBox
		{
			FontSize = 17.0,
			MaxLength = 120,
			FontWeight = FontWeights.SemiBold,
			MinHeight = 48.0,
			Padding = new Thickness(12.0, 9.0, 12.0, 9.0),
			TextWrapping = TextWrapping.Wrap,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			VerticalContentAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
			ToolTip = "备忘录标题"
		};
		parent.Children.Add(noteTitle);
		noteBody = new TextBox
		{
			Height = 170.0,
			VerticalContentAlignment = VerticalAlignment.Top,
			AcceptsReturn = true,
			AcceptsTab = true,
			TextWrapping = TextWrapping.Wrap,
			VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
			FontSize = 14.0,
			ToolTip = "随手记下想法，内容自动保存"
		};
		parent.Children.Add(noteBody);
		noteTitle.TextChanged += (object s, TextChangedEventArgs e) =>
		{
			NoteEdited();
		};
		noteBody.TextChanged += (object s, TextChangedEventArgs e) =>
		{
			NoteEdited();
		};
		notePicker.DropDownOpened += (object s, EventArgs e) =>
		{
			M.Notes.Flush();
			notePicker.Items.Refresh();
		};
		notePicker.SelectionChanged += (object s, SelectionChangedEventArgs e) =>
		{
			if (!editingNotes && notePicker.SelectedItem is NoteItem noteItem)
			{
				M.Notes.Select(noteItem.Id);
				LoadNote();
			}
		};
		WrapPanel wrapPanel = new WrapPanel
		{
			Margin = new Thickness(0.0, 10.0, 0.0, 0.0)
		};
		wrapPanel.Children.Add(Small("新建", () =>
		{
			M.Notes.Add();
			M.RefreshNotes();
		}, 51.0));
		wrapPanel.Children.Add(Small("清单", () =>
		{
			noteBody.SelectedText = "☐ ";
			noteBody.Focus();
		}, 51.0));
		wrapPanel.Children.Add(Small("导出", () =>
		{
			SaveFileDialog saveFileDialog = new SaveFileDialog
			{
				FileName = "备忘录.txt",
				Filter = "文本文件|*.txt"
			};
			if (saveFileDialog.ShowDialog(this) == true)
			{
				try
				{
					M.Notes.Export(saveFileDialog.FileName);
				}
				catch (Exception ex)
				{
					MessageBox.Show(this, ex.Message, "导出失败");
				}
			}
		}, 51.0));
		wrapPanel.Children.Add(Small("删除", () =>
		{
			if (MessageBox.Show(this, "删除这条备忘录？", "GlassBoard", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
			{
				M.Notes.Remove();
				M.RefreshNotes();
			}
		}, 51.0));
		parent.Children.Add(wrapPanel);
		noteStatus = new TextBlock
		{
			Foreground = MutedBrush,
			FontSize = 10.0,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		parent.Children.Add(noteStatus);
		ReloadNotes();
	}

	private void NoteEdited()
	{
		if (editingNotes || currentNote == null)
		{
			return;
		}
		bool flag = currentNote.Title != noteTitle.Text;
		currentNote.Title = noteTitle.Text;
		currentNote.Body = noteBody.Text;
		if (flag)
		{
			editingNotes = true;
			try
			{
				notePicker.Items.Refresh();
			}
			finally
			{
				editingNotes = false;
			}
		}
		M.Notes.Changed();
		if (noteStatus != null)
		{
			SetText(noteStatus, M.Notes.Status);
		}
		FitContent();
	}

	public void ReloadNotes()
	{
		if (notePicker != null)
		{
			editingNotes = true;
			notePicker.ItemsSource = null;
			notePicker.ItemsSource = M.Notes.Book.Items;
			notePicker.SelectedItem = M.Notes.Book.Selected;
			editingNotes = false;
			LoadNote();
		}
	}

	private void LoadNote()
	{
		editingNotes = true;
		currentNote = M.Notes.Book.Selected;
		noteTitle.Text = currentNote.Title;
		noteBody.Text = currentNote.Body;
		editingNotes = false;
		FitContent();
	}

	public void Tick()
	{
		if (clock != null)
		{
			ClockTick();
		}
		if (noteStatus != null)
		{
			SetText(noteStatus, M.Notes.Status);
		}
	}

	public void UpdateWeather()
	{
		if (weatherTemperature != null)
		{
			WeatherSnapshot snapshot = M.Weather.Snapshot;
			weatherTemperature.Text = ((snapshot == null) ? "—°" : (snapshot.Current.Temperature.ToString("0.#") + "°"));
			TextBlock textBlock = weatherPlace;
			string text;
			if (snapshot != null)
			{
				text = WeatherPresentation.Place(M.C.Prefs.Widgets.City, snapshot.Place);
			}
			else
			{
				text = (string.IsNullOrWhiteSpace(M.C.Prefs.Widgets.City) ? "选择你的城市" : M.C.Prefs.Widgets.City);
			}
			textBlock.Text = text;
			if (weatherIcon != null)
			{
				((ContentControl)weatherIcon).Content = WeatherSymbol((snapshot == null) ? "cloud" : WeatherPresentation.Icon(snapshot.Current.Code, snapshot.Current.Description));
			}
			weatherDetails.Text = ((snapshot == null) ? "" : (WeatherPresentation.Description(snapshot.Current.Code, snapshot.Current.Description) + "  ·  风速 " + snapshot.Current.Wind.ToString("0.#") + " km/h"));
			weatherStatus.Text = M.Weather.Message;
			weatherStatus.ToolTip = M.Weather.Message;
			weatherPlace.ToolTip = weatherPlace.Text;
			FitContent();
		}
	}

	public void UpdateMusic()
	{
		if (building || musicStatus == null || play == null || M.Music == null)
		{
			return;
		}
		MusicEngine music = M.Music;
		string text = music.Title + "\n" + music.Artist + "\n" + music.Album + "\n" + music.Status + "/" + music.Playing + "/" + music.Local + "/" + M.C.Prefs.Widgets.Playlist.Count;
		if (!(musicLayoutSignature == text) || shownArtwork != (music.Artwork ?? music.AppIcon))
		{
			musicLayoutSignature = text;
			musicTitle.Text = music.Title;
			musicTitle.ToolTip = music.Title;
			musicArtist.Text = music.Artist;
			musicArtist.ToolTip = music.Album;
			TextBlock textBlock = musicStatus;
			string text2;
			if (string.IsNullOrWhiteSpace(music.Status))
			{
				if (music.Local)
				{
					text2 = "本地播放 · " + M.C.Prefs.Widgets.Playlist.Count + " 首";
				}
				else if (music.Session == null || !music.Session.Available)
				{
					text2 = ((!string.IsNullOrWhiteSpace(music.Title) && music.Title != "等待系统音乐") ? "窗口标题读取 · 播放中" : "暂无音乐播放");
				}
				else
				{
					string friendlyName = MusicPlatforms.FriendlyName(music.Session?.Source ?? "");
					text2 = ((friendlyName.Length == 0) ? "系统媒体会话" : (friendlyName + " · 媒体会话"));
				}
			}
			else
			{
				text2 = music.Status;
			}
			textBlock.Text = text2;
			musicStatus.ToolTip = music.Status;
			if (shownArtwork != (music.Artwork ?? music.AppIcon) || cover.Child == null)
			{
				shownArtwork = music.Artwork ?? music.AppIcon;
				cover.Child = ((shownArtwork == null) ? Polish.Icon("music", Polish.Color("#E8EDFF"), 34.0) : new Image
				{
					Source = shownArtwork,
					Stretch = ((music.Artwork == null) ? Stretch.Uniform : Stretch.UniformToFill)
				});
			}
			if (shownPlaying != music.Playing)
			{
				shownPlaying = music.Playing;
				play.Content = TransportIcon(music.Playing ? "pause" : "play", primary: true);
			}
			FitContent();
		}
	}

	private void ResetAdvancedClock()
	{
		posterDay = (posterMonth = (orbitLabel = null));
		hourRotation = (minuteRotation = null);
		orbitArc = null;
		clockStamp = "";
	}

	private TextBlock ClockText(double size, string font = null)
	{
		TextBlock textBlock = new TextBlock
		{
			FontSize = size,
			Foreground = StyleInk("clock"),
			FontWeight = FontWeights.Light
		};
		if (!string.IsNullOrEmpty(font))
		{
			textBlock.FontFamily = new FontFamily(font);
		}
		return textBlock;
	}

	private Border ClockRule(double width = 40.0)
	{
		return new Border
		{
			Width = width,
			Height = 1.0,
			Background = StyleInk("clock"),
			Opacity = 0.45,
			Margin = new Thickness(0.0, 10.0, 0.0, 10.0),
			HorizontalAlignment = HorizontalAlignment.Center
		};
	}

	private void AdvancedClock(StackPanel body, WidgetStyle s)
	{
		string text = (string.IsNullOrEmpty(s.Font) ? null : s.Font);
		if (s.ClockStyle == 5)
		{
			weekday = ClockText(34.0);
			weekday.FontWeight = FontWeights.SemiBold;
			body.Children.Add(new Viewbox
			{
				Child = weekday,
				Stretch = Stretch.Uniform,
				MaxHeight = 45.0,
				Margin = new Thickness(0.0, 4.0, 0.0, 4.0)
			});
			body.Children.Add(ClockRule(34.0));
			TextBlock textBlock = date;
			HorizontalAlignment horizontalAlignment = (clock.HorizontalAlignment = HorizontalAlignment.Center);
			textBlock.HorizontalAlignment = horizontalAlignment;
			date.FontSize = 12.0;
			clock.FontSize = s.ClockSize * 0.68;
			body.Children.Add(date);
			body.Children.Add(clock);
		}
		else if (s.ClockStyle == 6 || s.ClockStyle == 12)
		{
			Grid grid = new Grid();
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength((s.ClockStyle == 6) ? 125 : 90)
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(18.0)
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition());
			StackPanel stackPanel = new StackPanel
			{
				VerticalAlignment = VerticalAlignment.Center
			};
			posterMonth = ClockText(12.0);
			posterMonth.HorizontalAlignment = HorizontalAlignment.Center;
			posterDay = ClockText((s.ClockStyle == 6) ? 84 : 56, (s.ClockStyle == 6) ? "Georgia" : null);
			posterDay.HorizontalAlignment = HorizontalAlignment.Center;
			stackPanel.Children.Add(posterMonth);
			stackPanel.Children.Add(posterDay);
			grid.Children.Add(stackPanel);
			Border border = new Border
			{
				Width = 1.0,
				Background = StyleInk("clock"),
				Opacity = 0.3,
				Margin = new Thickness(0.0, 12.0, 0.0, 12.0)
			};
			Grid.SetColumn(border, 1);
			grid.Children.Add(border);
			Visibility visibility = (border.Visibility = ((!s.ShowDate) ? Visibility.Collapsed : Visibility.Visible));
			stackPanel.Visibility = visibility;
			if (!s.ShowDate)
			{
				grid.ColumnDefinitions[0].Width = new GridLength(0.0);
				grid.ColumnDefinitions[1].Width = new GridLength(0.0);
			}
			StackPanel stackPanel2 = new StackPanel
			{
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(8.0, 0.0, 0.0, 0.0)
			};
			Grid.SetColumn(stackPanel2, 2);
			weekday = ClockText((s.ClockStyle == 6) ? 17 : 13);
			weekday.FontWeight = FontWeights.Medium;
			stackPanel2.Children.Add(weekday);
			clock.FontSize = ((s.ClockStyle == 6) ? (s.ClockSize * 0.83) : (s.ClockSize * 0.7));
			stackPanel2.Children.Add(clock);
			stackPanel2.Children.Add(date);
			body.Children.Add(grid);
		}
		else if (s.ClockStyle == 7)
		{
			Grid grid2 = new Grid();
			grid2.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(135.0)
			});
			grid2.ColumnDefinitions.Add(new ColumnDefinition());
			StackPanel stackPanel3 = new StackPanel();
			clockHours = ClockText(s.ClockSize * 1.55, text ?? "Segoe UI Light");
			clockMinutes = ClockText(s.ClockSize * 1.55, text ?? "Segoe UI Light");
			TextBlock textBlock2 = clockHours;
			double lineHeight = (clockMinutes.LineHeight = s.ClockSize * 1.5);
			textBlock2.LineHeight = lineHeight;
			stackPanel3.Children.Add(clockHours);
			stackPanel3.Children.Add(clockMinutes);
			clockSeconds = ClockText(s.ClockSize * 0.45, text ?? "Segoe UI Light");
			clockSeconds.HorizontalAlignment = HorizontalAlignment.Center;
			clockSeconds.Opacity = 0.6;
			stackPanel3.Children.Add(clockSeconds);
			grid2.Children.Add(stackPanel3);
			StackPanel stackPanel4 = new StackPanel
			{
				VerticalAlignment = VerticalAlignment.Bottom,
				Margin = new Thickness(16.0, 0.0, 0.0, 12.0)
			};
			Grid.SetColumn(stackPanel4, 1);
			weekday = ClockText(19.0);
			weekday.FontWeight = FontWeights.SemiBold;
			stackPanel4.Children.Add(weekday);
			stackPanel4.Children.Add(ClockRule(30.0));
			date.TextWrapping = TextWrapping.Wrap;
			stackPanel4.Children.Add(date);
			grid2.Children.Add(stackPanel4);
			body.Children.Add(grid2);
		}
		else if (s.ClockStyle == 8 || s.ClockStyle == 9)
		{
			Grid grid3 = new Grid
			{
				Width = 184.0,
				Height = 184.0,
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0.0, 4.0, 0.0, 8.0)
			};
			grid3.Children.Add(new Ellipse
			{
				Stroke = StyleInk("clock"),
				StrokeThickness = 1.0,
				Opacity = 0.25,
				Margin = new Thickness(5.0)
			});
			if (s.ClockStyle == 8)
			{
				Canvas canvas = new Canvas
				{
					Width = 184.0,
					Height = 184.0
				};
				for (int i = 0; i < 12; i++)
				{
					double num2 = (double)i * Math.PI / 6.0;
					double num3 = ((i % 3 == 0) ? 74 : 79);
					canvas.Children.Add(new Line
					{
						X1 = 92.0 + Math.Sin(num2) * num3,
						Y1 = 92.0 - Math.Cos(num2) * num3,
						X2 = 92.0 + Math.Sin(num2) * 84.0,
						Y2 = 92.0 - Math.Cos(num2) * 84.0,
						Stroke = StyleInk("clock"),
						StrokeThickness = ((i % 3 != 0) ? 1 : 2),
						Opacity = 0.65
					});
				}
				grid3.Children.Add(canvas);
				hourRotation = new RotateTransform(0.0, 92.0, 92.0);
				minuteRotation = new RotateTransform(0.0, 92.0, 92.0);
				Line element = new Line
				{
					X1 = 92.0,
					Y1 = 100.0,
					X2 = 92.0,
					Y2 = 45.0,
					Stroke = StyleInk("clock"),
					StrokeThickness = 3.0,
					StrokeStartLineCap = PenLineCap.Round,
					StrokeEndLineCap = PenLineCap.Round,
					RenderTransform = hourRotation
				};
				Line element2 = new Line
				{
					X1 = 92.0,
					Y1 = 103.0,
					X2 = 92.0,
					Y2 = 22.0,
					Stroke = StyleInk("clock"),
					StrokeThickness = 1.5,
					StrokeStartLineCap = PenLineCap.Round,
					StrokeEndLineCap = PenLineCap.Round,
					RenderTransform = minuteRotation
				};
				canvas.Children.Add(element);
				canvas.Children.Add(element2);
				grid3.Children.Add(new Ellipse
				{
					Width = 6.0,
					Height = 6.0,
					Fill = StyleInk("clock"),
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				});
				body.Children.Add(grid3);
				clock.FontSize = s.ClockSize * 0.62;
				clock.HorizontalAlignment = HorizontalAlignment.Center;
				body.Children.Add(clock);
			}
			else
			{
				orbitArc = new System.Windows.Shapes.Path
				{
					Stroke = StyleInk("clock"),
					StrokeThickness = 3.0,
					StrokeStartLineCap = PenLineCap.Round,
					StrokeEndLineCap = PenLineCap.Round
				};
				grid3.Children.Add(orbitArc);
				StackPanel stackPanel5 = new StackPanel
				{
					VerticalAlignment = VerticalAlignment.Center,
					HorizontalAlignment = HorizontalAlignment.Center
				};
				clock.FontSize = s.ClockSize * 0.65;
				clock.HorizontalAlignment = HorizontalAlignment.Center;
				orbitLabel = ClockText(10.0);
				orbitLabel.HorizontalAlignment = HorizontalAlignment.Center;
				stackPanel5.Children.Add(clock);
				stackPanel5.Children.Add(orbitLabel);
				grid3.Children.Add(new Viewbox
				{
					Child = stackPanel5,
					Width = 150.0,
					Height = 95.0,
					Stretch = Stretch.Uniform,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				});
				body.Children.Add(grid3);
			}
			date.HorizontalAlignment = HorizontalAlignment.Center;
			body.Children.Add(date);
		}
		else if (s.ClockStyle == 10)
		{
			clock.FontFamily = new FontFamily(text ?? "Georgia");
			clock.FontStyle = FontStyles.Italic;
			clock.FontSize = s.ClockSize * 1.15;
			clock.HorizontalAlignment = HorizontalAlignment.Center;
			body.Children.Add(ClockRule(80.0));
			body.Children.Add(clock);
			body.Children.Add(ClockRule(80.0));
			date.FontSize = 12.0;
			date.HorizontalAlignment = HorizontalAlignment.Center;
			body.Children.Add(date);
		}
		else
		{
			Grid grid4 = new Grid();
			grid4.ColumnDefinitions.Add(new ColumnDefinition());
			grid4.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			weekday = ClockText(12.0, "Consolas");
			weekday.FontWeight = FontWeights.Bold;
			grid4.Children.Add(weekday);
			TextBlock textBlock3 = ClockText(10.0, "Consolas");
			textBlock3.Text = (s.Hour24 ? "24 H" : "12 H");
			Grid.SetColumn(textBlock3, 1);
			grid4.Children.Add(textBlock3);
			body.Children.Add(grid4);
			clock.FontFamily = new FontFamily(text ?? "Consolas");
			clock.FontSize = s.ClockSize * 1.08;
			body.Children.Add(clock);
			date.FontFamily = new FontFamily("Consolas");
			date.FontSize = 12.0;
			body.Children.Add(date);
		}
	}

	private void AdvancedClockTick(DateTime now, WidgetStyle style)
	{
		if (style.ClockStyle < 5)
		{
			return;
		}
		SetText(weekday, ClockCatalog.Weekday(now, style.ClockStyle));
		SetText(date, ClockCatalog.DateLabel(now, style.ClockStyle));
		SetText(posterDay, now.ToString("dd"));
		SetText(posterMonth, now.ToString("MMM yyyy", CultureInfo.GetCultureInfo("en-US")).ToUpperInvariant());
		string text = now.ToString("yyyyMMddHHmm");
		if (clockStamp == text)
		{
			return;
		}
		clockStamp = text;
		if (hourRotation != null)
		{
			hourRotation.Angle = ClockCatalog.HourAngle(now);
		}
		if (minuteRotation != null)
		{
			minuteRotation.Angle = ClockCatalog.MinuteAngle(now);
		}
		if (orbitArc != null)
		{
			double num = ClockCatalog.DayFraction(now);
			double num2 = num * Math.PI * 2.0;
			PathGeometry pathGeometry = new PathGeometry();
			if (num > 0.0)
			{
				PathFigure pathFigure = new PathFigure
				{
					StartPoint = new Point(92.0, 5.0),
					IsClosed = false
				};
				pathFigure.Segments.Add(new ArcSegment(new Point(92.0 + 87.0 * Math.Sin(num2), 92.0 - 87.0 * Math.Cos(num2)), new Size(87.0, 87.0), 0.0, num > 0.5, SweepDirection.Clockwise, isStroked: true));
				pathGeometry.Figures.Add(pathFigure);
			}
			orbitArc.Data = pathGeometry;
			SetText(orbitLabel, "DAY  " + Math.Floor(num * 100.0) + "%");
		}
	}

	internal void CheckAppearance()
	{
		string[] kinds = WidgetRules.Kinds;
		foreach (string text in kinds)
		{
			WidgetStyle widgetStyle = M.C.Prefs.Widgets.Style(text);
			widgetStyle.Frameless = true;
			widgetStyle.TextColor = "#33CCFF";
			if (text == "music" && M.Music == null)
			{
				M.Music = new MusicEngine(M.C);
			}
			for (int j = 0; j < ((text == "clock") ? ClockCatalog.Names.Length : ((!(text == "music")) ? 1 : 5)); j++)
			{
				widgetStyle.ClockStyle = (widgetStyle.MusicButtons = j);
				UpdateOptions(new List<string> { text });
				UpdateLayout();
				if (Surface.Visibility != Visibility.Collapsed || moveGrip.Visibility != Visibility.Collapsed || resizeGrip.Visibility != Visibility.Collapsed)
				{
					throw new Exception("无框组件仍有外框或编辑控件");
				}
				Border border = stack.Children.OfType<Border>().Single();
				if (border.Background != null || border.BorderThickness.Left != 0.0 || border.Padding.Left > 4.0)
				{
					throw new Exception("无框组件仍有卡片底板或过多留白");
				}
				if (ContextMenu == null || ContextMenu.Items.Count == 0)
				{
					throw new Exception("无框组件缺少恢复入口");
				}
				if (!((text switch
				{
					"weather" => weatherPlace, 
					"music" => musicTitle, 
					"clock" => date, 
					_ => noteStatus, 
				}).Foreground is SolidColorBrush { Color: { R: 51 }, Color: { G: 204 } }))
				{
					throw new Exception("文字配色未应用");
				}
				if (text == "clock" && j == 1 && (weekday == null || weekday.Opacity >= 0.5))
				{
					throw new Exception("星期叠字层次错误");
				}
			}
			widgetStyle.Frameless = false;
			UpdateOptions(new List<string> { text });
			if (Surface.Visibility != Visibility.Visible)
			{
				throw new Exception("组件外框无法恢复");
			}
		}
	}
}
