using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace GlassBoard;

[DataContract]
public class ControlCenterOptions
{
	[DataMember]
	public bool ShowNetworks = true;

	[DataMember]
	public bool ShowBluetooth = true;

	[DataMember]
	public bool ShowVolume = true;

	[DataMember]
	public bool ShowBrightness = true;

	[DataMember]
	public bool ShowOutput = true;

	[OnDeserializing]
	private void Defaults(StreamingContext context)
	{
		ShowNetworks = true;
		ShowBluetooth = true;
		ShowVolume = true;
		ShowBrightness = true;
		ShowOutput = true;
	}
}

// 控制中心：Wi-Fi / 蓝牙 / 音量 / 输出设备 / 亮度，位于 Dock 上方，主题跟随设置。
public class ControlCenterWindow : GlassWindow
{
	private static Controller C;

	private readonly ControlCenterBridge Bridge = new ControlCenterBridge();

	private readonly DispatcherTimer refreshTimer;

	private bool refreshing;
	private bool suppressHide;
	private bool closing;
	private bool wifiSupported = true;
	private bool btSupported = true;
	private bool wifiOn;
	private bool btOn;
	private bool volumeSupported = true;
	private bool muted;
	private bool connecting;
	private bool volumeDragging;
	private bool brightnessDragging;
	private string currentSsid = "";
	private string pendingSsid = "";
	private string volumeDeviceName = "输出设备";
	private TextBlock outputHint;
	private HashSet<string> savedProfiles = new HashSet<string>(StringComparer.Ordinal);

	private System.Windows.Media.Color accentColor;
	private System.Windows.Media.Brush accentBrush;
	private System.Windows.Media.Brush cardBrush;
	private System.Windows.Media.Brush hoverBrush;
	private System.Windows.Media.Brush lineBrush;

	// 控件
	private StackPanel body;
	private Border wifiTile;
	private Border btTile;
	private ContentControl wifiIconHost;
	private ContentControl btIconHost;
	private TextBlock wifiStatus;
	private TextBlock btStatus;
	private Border wifiSection;
	private TextBlock currentNetText;
	private StackPanel networksPanel;
	private TextBlock netErrorText;
	private Border passwordPanel;
	private TextBlock passwordPrompt;
	private TextBox passwordBox;
	private Border volumeSection;
	private Slider volumeSlider;
	private TextBlock volumeValue;
	private System.Windows.Controls.ContentControl muteIcon;
	private Border outputSection;
	private Button outputButton;
	private Border brightnessSection;
	private Slider brightnessSlider;
	private TextBlock brightnessValue;
	private Border btSection;
	private StackPanel btListPanel;

	public ControlCenterWindow(Controller c)
		: base(24.0)
	{
		C = c;
		Width = 384.0;
		Height = 600.0;
		ShowInTaskbar = false;
		BuildUi();
		refreshTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(3.5)
		};
		refreshTimer.Tick += (object s, EventArgs e) =>
		{
			if (IsVisible)
			{
				RefreshDynamic();
			}
		};
		Deactivated += (object s, EventArgs e) =>
		{
			if (!suppressHide && !ShellContextMenu.Active)
			{
				ClosePanel();
			}
		};
	}

	private Brush LineBrush()
	{
		return new SolidColorBrush(LightTheme ? System.Windows.Media.Color.FromArgb(36, 25, 28, 34) : System.Windows.Media.Color.FromArgb(40, byte.MaxValue, byte.MaxValue, byte.MaxValue));
	}

	private void RefreshStyle()
	{
		LightTheme = C.Prefs.Light;
		DockOptions dock = C.Prefs.Dock;
		MotionEnabled = dock.MotionEnabled;
		MotionLevel = dock.MotionLevel;
		ConfigureGlass(dock);
		GlassPercent = dock.OpacityPercent;
		Polish.Apply(this, LightTheme);
		accentColor = IconPacks.AccentColor(dock.Accent);
		accentBrush = new SolidColorBrush(accentColor);
		cardBrush = new SolidColorBrush(LightTheme ? System.Windows.Media.Color.FromArgb(120, byte.MaxValue, byte.MaxValue, byte.MaxValue) : System.Windows.Media.Color.FromArgb(26, byte.MaxValue, byte.MaxValue, byte.MaxValue));
		hoverBrush = new SolidColorBrush(LightTheme ? System.Windows.Media.Color.FromArgb(80, 60, 70, 90) : System.Windows.Media.Color.FromArgb(34, byte.MaxValue, byte.MaxValue, byte.MaxValue));
		lineBrush = LineBrush();
		wifiTile.Background = cardBrush;
		wifiTile.BorderBrush = lineBrush;
		btTile.Background = cardBrush;
		btTile.BorderBrush = lineBrush;
		wifiSection.Background = cardBrush;
		wifiSection.BorderBrush = lineBrush;
		volumeSection.Background = cardBrush;
		volumeSection.BorderBrush = lineBrush;
		outputSection.Background = cardBrush;
		outputSection.BorderBrush = lineBrush;
		brightnessSection.Background = cardBrush;
		brightnessSection.BorderBrush = lineBrush;
		btSection.Background = cardBrush;
		btSection.BorderBrush = lineBrush;
		passwordPanel.Background = cardBrush;
		passwordPanel.BorderBrush = lineBrush;
	}

	// ==================== UI 构建 ====================

	private TextBlock Label(string text, double size, FontWeight weight, System.Windows.Media.Brush brush)
	{
		return new TextBlock
		{
			Text = text,
			FontSize = size,
			FontWeight = weight,
			Foreground = brush,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
	}

	private Border SectionCard()
	{
		return new Border
		{
			CornerRadius = new CornerRadius(16.0),
			Padding = new Thickness(16.0),
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
			BorderThickness = new Thickness(1.0)
		};
	}

	private Border BuildTile(string kind, string title, out ContentControl iconHost, out TextBlock status, MouseButtonEventHandler toggle)
	{
		Border tile = new Border
		{
			CornerRadius = new CornerRadius(16.0),
			Padding = new Thickness(14.0),
			Margin = new Thickness(4.0, 0.0, 4.0, 0.0),
			BorderThickness = new Thickness(1.0),
			Cursor = Cursors.Hand,
			Tag = kind
		};
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		iconHost = new ContentControl
		{
			Width = 24.0,
			Height = 24.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 12.0, 0.0)
		};
		Grid.SetColumn(iconHost, 0);
		grid.Children.Add(iconHost);
		StackPanel stack = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center
		};
		stack.Children.Add(Label(title, 13.0, FontWeights.SemiBold, TextBrush));
		status = Label("", 10.5, FontWeights.Normal, MutedBrush);
		status.Margin = new Thickness(0.0, 3.0, 0.0, 0.0);
		stack.Children.Add(status);
		Grid.SetColumn(stack, 1);
		grid.Children.Add(stack);
		tile.Child = grid;
		tile.MouseLeftButtonUp += toggle;
		return tile;
	}

	private void BuildUi()
	{
		ScrollViewer scroll = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};
		ContentGrid.Children.Add(scroll);
		body = new StackPanel
		{
			Margin = new Thickness(16.0, 14.0, 16.0, 18.0)
		};
		scroll.Content = body;

		Grid header = new Grid
		{
			Margin = new Thickness(4.0, 0.0, 4.0, 10.0)
		};
		header.ColumnDefinitions.Add(new ColumnDefinition());
		header.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		header.Children.Add(Label("控制中心", 16.0, FontWeights.SemiBold, TextBrush));
		TextBlock tip = Label("跟随 Dock 主题", 10.5, FontWeights.Normal, MutedBrush);
		tip.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(tip, 1);
		header.Children.Add(tip);
		body.Children.Add(header);

		Grid tiles = new Grid
		{
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		tiles.ColumnDefinitions.Add(new ColumnDefinition());
		tiles.ColumnDefinitions.Add(new ColumnDefinition());
		wifiTile = BuildTile("wifi", "Wi-Fi", out wifiIconHost, out wifiStatus, (object s, MouseButtonEventArgs e) =>
		{
			ToggleRadio(wifi: true);
		});
		Grid.SetColumn(wifiTile, 0);
		tiles.Children.Add(wifiTile);
		btTile = BuildTile("bluetooth", "蓝牙", out btIconHost, out btStatus, (object s, MouseButtonEventArgs e) =>
		{
			ToggleRadio(wifi: false);
		});
		Grid.SetColumn(btTile, 1);
		tiles.Children.Add(btTile);
		body.Children.Add(tiles);

		// Wi-Fi 区块
		wifiSection = SectionCard();
		StackPanel wifiStack = new StackPanel();
		wifiSection.Child = wifiStack;
		currentNetText = Label("", 12.5, FontWeights.SemiBold, TextBrush);
		currentNetText.Margin = new Thickness(0.0, 4.0, 0.0, 4.0);
		wifiStack.Children.Add(currentNetText);
		networksPanel = new StackPanel();
		wifiStack.Children.Add(networksPanel);
		netErrorText = Label("", 11.0, FontWeights.Normal, new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 150, 178)));
		netErrorText.TextWrapping = TextWrapping.Wrap;
		netErrorText.Margin = new Thickness(0.0, 6.0, 0.0, 0.0);
		netErrorText.Visibility = Visibility.Collapsed;
		wifiStack.Children.Add(netErrorText);
		Button scan = Button("重新扫描", (object s, RoutedEventArgs e) =>
		{
			RefreshNetworks(scan: true);
		}, 112.0);
		scan.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
		wifiStack.Children.Add(scan);
		body.Children.Add(wifiSection);

		// 密码输入
		passwordPanel = new Border
		{
			CornerRadius = new CornerRadius(14.0),
			Padding = new Thickness(14.0),
			Margin = new Thickness(0.0, 6.0, 0.0, 6.0),
			BorderThickness = new Thickness(1.0),
			Visibility = Visibility.Collapsed
		};
		StackPanel passwordStack = new StackPanel();
		passwordPanel.Child = passwordStack;
		passwordPrompt = Label("", 12.0, FontWeights.Normal, TextBrush);
		passwordPrompt.TextWrapping = TextWrapping.Wrap;
		passwordStack.Children.Add(passwordPrompt);
		passwordBox = new TextBox
		{
			Margin = new Thickness(0.0, 8.0, 0.0, 4.0)
		};
		passwordStack.Children.Add(passwordBox);
		WrapPanel passwordButtons = new WrapPanel
		{
			HorizontalAlignment = System.Windows.HorizontalAlignment.Right
		};
		Button connectBtn = Button("连接", (object s, RoutedEventArgs e) =>
		{
			StartConnect();
		}, 80.0);
		connectBtn.IsEnabled = false;
		passwordBox.TextChanged += (object s, TextChangedEventArgs e) =>
		{
			connectBtn.IsEnabled = !string.IsNullOrWhiteSpace(passwordBox.Text);
		};
		Button cancelBtn = Button("取消", (object s, RoutedEventArgs e) =>
		{
			pendingSsid = "";
			HidePasswordPrompt();
		}, 80.0);
		passwordButtons.Children.Add(cancelBtn);
		passwordButtons.Children.Add(connectBtn);
		passwordStack.Children.Add(passwordButtons);

		// 音量区块
		volumeSection = SectionCard();
		Grid volumeGrid = new Grid();
		volumeGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		volumeGrid.ColumnDefinitions.Add(new ColumnDefinition());
		volumeGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		volumeGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		volumeSection.Child = volumeGrid;
		Button muteBtn = new Button
		{
			Width = 26.0,
			Height = 26.0,
			Padding = new Thickness(0.0),
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = Cursors.Hand
		};
		muteIcon = new System.Windows.Controls.ContentControl
		{
			Content = IconPacks.Glyph("volume", C.Prefs.Dock.IconPack, C.Prefs.Dock.Accent, TextBrush, 18.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			IsHitTestVisible = false
		};
		muteBtn.Content = muteIcon;
		muteBtn.Click += (object s, RoutedEventArgs e) =>
		{
			muted = !muted;
			AudioCore.TrySetMute(muted);
			ApplyVolumeUi();
		};
		Grid.SetColumn(muteBtn, 0);
		volumeGrid.Children.Add(muteBtn);
		volumeSlider = new Slider
		{
			Minimum = 0.0,
			Maximum = 100.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		volumeSlider.ValueChanged += (object s, RoutedPropertyChangedEventArgs<double> e) =>
		{
			if (!volumeDragging || refreshing)
			{
				return;
			}
			AudioCore.TrySetVolume((float)(volumeSlider.Value / 100.0));
			volumeValue.Text = ((int)Math.Round(volumeSlider.Value)).ToString();
		};
		volumeSlider.PreviewMouseDown += (object s, MouseButtonEventArgs e) =>
		{
			volumeDragging = true;
		};
		volumeSlider.PreviewMouseUp += (object s, MouseButtonEventArgs e) =>
		{
			volumeDragging = false;
		};
		Grid.SetColumn(volumeSlider, 1);
		volumeGrid.Children.Add(volumeSlider);
		volumeValue = Label("100", 11.0, FontWeights.Normal, MutedBrush);
		volumeValue.Width = 30.0;
		volumeValue.TextAlignment = TextAlignment.Right;
		volumeValue.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(volumeValue, 2);
		volumeGrid.Children.Add(volumeValue);
		body.Children.Add(volumeSection);

		// 输出设备区块
		outputSection = SectionCard();
		Grid outputGrid = new Grid();
		outputGrid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		outputGrid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		outputGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		outputGrid.ColumnDefinitions.Add(new ColumnDefinition());
		outputSection.Child = outputGrid;
		outputGrid.Children.Add(Label("输出设备", 12.5, FontWeights.SemiBold, TextBrush));
		outputButton = new Button
		{
			Height = 30.0,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0),
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
			Padding = new Thickness(10.0, 0.0, 10.0, 0.0),
			Cursor = Cursors.Hand
		};
		outputButton.Click += (object s, RoutedEventArgs e) =>
		{
			OpenOutputMenu();
		};
		Grid.SetColumn(outputButton, 1);
		outputGrid.Children.Add(outputButton);
		outputHint = Label("当前系统不允许直接切换默认输出设备，请使用系统声音设置", 10.5, FontWeights.Normal, MutedBrush);
		outputHint.Margin = new Thickness(12.0, 4.0, 0.0, 0.0);
		outputHint.TextWrapping = TextWrapping.Wrap;
		outputHint.Visibility = Visibility.Collapsed;
		Grid.SetRow(outputHint, 1);
		Grid.SetColumn(outputHint, 1);
		outputGrid.Children.Add(outputHint);
		body.Children.Add(outputSection);

		// 亮度区块
		brightnessSection = SectionCard();
		Grid brightGrid = new Grid();
		brightGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		brightGrid.ColumnDefinitions.Add(new ColumnDefinition());
		brightGrid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		brightnessSection.Child = brightGrid;
		ContentControl sunIcon = new ContentControl
		{
			Width = 22.0,
			Height = 22.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 12.0, 0.0)
		};
		sunIcon.Content = IconPacks.Glyph("brightness", C.Prefs.Dock.IconPack, C.Prefs.Dock.Accent, TextBrush, 22.0);
		Grid.SetColumn(sunIcon, 0);
		brightGrid.Children.Add(sunIcon);
		brightnessSlider = new Slider
		{
			Minimum = 0.0,
			Maximum = 100.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		brightnessSlider.ValueChanged += (object s, RoutedPropertyChangedEventArgs<double> e) =>
		{
			if (!brightnessDragging || refreshing)
			{
				return;
			}
			brightnessValue.Text = ((int)Math.Round(brightnessSlider.Value)).ToString();
			ScheduleBrightnessSet();
		};
		brightnessSlider.PreviewMouseDown += (object s, MouseButtonEventArgs e) =>
		{
			brightnessDragging = true;
		};
		brightnessSlider.PreviewMouseUp += (object s, MouseButtonEventArgs e) =>
		{
			brightnessDragging = false;
			ScheduleBrightnessSet();
		};
		Grid.SetColumn(brightnessSlider, 1);
		brightGrid.Children.Add(brightnessSlider);
		brightnessValue = Label("100", 11.0, FontWeights.Normal, MutedBrush);
		brightnessValue.Width = 30.0;
		brightnessValue.TextAlignment = TextAlignment.Right;
		brightnessValue.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(brightnessValue, 2);
		brightGrid.Children.Add(brightnessValue);
		body.Children.Add(brightnessSection);

		// 蓝牙区块
		btSection = SectionCard();
		StackPanel btStack = new StackPanel();
		btSection.Child = btStack;
		btStack.Children.Add(Label("蓝牙设备", 12.5, FontWeights.SemiBold, TextBrush));
		btListPanel = new StackPanel
		{
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		};
		btStack.Children.Add(btListPanel);
		body.Children.Add(btSection);

		// 底部快捷入口
		WrapPanel footer = new WrapPanel
		{
			Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center
		};
		Button netBtn = Button("网络设置", (object s, RoutedEventArgs e) =>
		{
			OpenSettings("ms-settings:network");
		}, 96.0);
		Button btBtn = Button("蓝牙设置", (object s, RoutedEventArgs e) =>
		{
			OpenSettings("ms-settings:bluetooth");
		}, 96.0);
		footer.Children.Add(netBtn);
		footer.Children.Add(btBtn);
		body.Children.Add(footer);
	}

	private static void OpenSettings(string uri)
	{
		try
		{
			Process.Start(new ProcessStartInfo(uri)
			{
				UseShellExecute = true
			});
		}
		catch
		{
		}
	}

	// ==================== 显示与状态 ====================

	public void Toggle()
	{
		if (IsVisible)
		{
			ClosePanel();
			return;
		}
		ShowPanel();
	}

	private void ClosePanel()
	{
		if (closing || !IsVisible)
		{
			return;
		}
		closing = true;
		refreshTimer.Stop();
		closing = false;
		Hide();
		Close();
	}

	private void ShowPanel()
	{
		RefreshStyle();
		Rect wa = SystemParameters.WorkArea;
		double dockTop = C.Dock.Top;
		double dockLeft = C.Dock.Left;
		double dockWidth = C.Dock.Width;
		double dockHeight = C.Dock.Height;
		double left = Math.Max(wa.Left + 8.0, Math.Min(dockLeft + dockWidth - Width - 8.0, wa.Right - Width - 8.0));
		double top = dockTop - Height - 10.0;
		if (top < wa.Top + 8.0)
		{
			top = dockTop + dockHeight + 10.0;
		}
		Left = left;
		Top = top;
		closing = false;
		Show();
		Backdrop(LightTheme);
		AnimateOpen();
		RefreshAll();
		refreshTimer.Start();
	}

	protected override void OnClosed(EventArgs e)
	{
		refreshTimer.Stop();
		Bridge.Dispose();
		if (C != null)
		{
			C.ControlCenter = null;
		}
		Dispatcher.BeginInvoke(new Action(delegate
		{
			GC.Collect(2, GCCollectionMode.Optimized, false);
			Native.TrimWorkingSet();
		}), DispatcherPriority.ApplicationIdle);
		base.OnClosed(e);
	}

	private void RefreshAll()
	{
		if (refreshing)
		{
			return;
		}
		refreshing = true;
		Task.Run(delegate
		{
			try
			{
				CcReply state = Bridge.State();
				ParseRadio(state.Value, out bool wifi, out bool wifiSupported, out bool bt, out bool btSupported);
				bool volumeOk = AudioCore.TryGetVolume(out float volume, out bool isMuted);
				int? brightness = BrightnessCore.TryGet();
				List<AudioCore.AudioDevice> devices = AudioCore.Devices();
				List<WirelessCore.WifiNetwork> networks = null;
				HashSet<string> profiles = null;
				if (wifi)
				{
					try
					{
						networks = WirelessCore.Networks();
					}
					catch
					{
					}
					try
					{
						profiles = WirelessCore.KnownProfiles();
					}
					catch
					{
					}
				}
				List<string> btLines = null;
				if (bt)
				{
					try
					{
						CcReply btReply = Bridge.BluetoothDevices();
						if (btReply.Ok && !string.IsNullOrEmpty(btReply.Value))
						{
							btLines = btReply.Value.Split('\n').ToList();
						}
					}
					catch
					{
					}
				}
				Dispatcher.BeginInvoke(new Action(delegate
				{
					if (profiles != null)
					{
						savedProfiles = profiles;
					}
					ApplyState(wifi, wifiSupported, bt, btSupported, volumeOk, volume, isMuted, brightness, devices, networks);
					if (btLines != null)
					{
						RebuildBluetooth(btLines);
					}
				}));
			}
			catch (Exception ex)
			{
				Dispatcher.BeginInvoke(new Action(delegate
				{
					ShowNetError("控制中心读取失败：" + ex.Message);
				}));
			}
			finally
			{
				refreshing = false;
			}
		});
	}

	private void RefreshDynamic()
	{
		if (refreshing)
		{
			return;
		}
		refreshing = true;
		Task.Run(delegate
		{
			try
			{
				CcReply state = Bridge.State();
				ParseRadio(state.Value, out bool wifi, out bool wifiSupported, out bool bt, out bool btSupported);
				bool volumeOk = AudioCore.TryGetVolume(out float volume, out bool isMuted);
				int? brightness = BrightnessCore.TryGet();
				List<WirelessCore.WifiNetwork> networks = null;
				if (wifi && !connecting)
				{
					try
					{
						networks = WirelessCore.Networks();
					}
					catch
					{
					}
				}
				Dispatcher.BeginInvoke(new Action(delegate
				{
					ApplyState(wifi, wifiSupported, bt, btSupported, volumeOk, volume, isMuted, brightness, null, networks);
				}));
			}
			catch
			{
			}
			finally
			{
				refreshing = false;
			}
		});
	}

	private void ParseRadio(string value, out bool wifi, out bool wifiSupported, out bool bt, out bool btSupported)
	{
		wifi = false;
		bt = false;
		wifiSupported = true;
		btSupported = true;
		if (string.IsNullOrWhiteSpace(value))
		{
			return;
		}
		foreach (string part in value.Split('|'))
		{
			int idx = part.IndexOf(':');
			if (idx <= 0)
			{
				continue;
			}
			string key = part.Substring(0, idx);
			string val = part.Substring(idx + 1);
			if (key == "wifi")
			{
				wifi = val == "on";
				wifiSupported = val != "absent";
			}
			else if (key == "bt")
			{
				bt = val == "on";
				btSupported = val != "absent";
			}
		}
	}

	private void ApplyState(bool wifi, bool wifiSupported2, bool bt, bool btSupported2, bool volumeOk, float volume, bool isMuted, int? brightness, List<AudioCore.AudioDevice> devices, List<WirelessCore.WifiNetwork> networks)
	{
		wifiOn = wifi;
		btOn = bt;
		wifiSupported = wifiSupported2;
		btSupported = btSupported2;
		volumeSupported = volumeOk;
		muted = isMuted;
		UpdateTileUi();
		ControlCenterOptions options = C.Prefs.ControlCenter;
		wifiSection.Visibility = ((options.ShowNetworks && wifiSupported && wifiOn) ? Visibility.Visible : Visibility.Collapsed);
		btSection.Visibility = ((options.ShowBluetooth && btSupported && btOn) ? Visibility.Visible : Visibility.Collapsed);
		volumeSection.Visibility = ((options.ShowVolume && volumeSupported) ? Visibility.Visible : Visibility.Collapsed);
		outputSection.Visibility = ((options.ShowOutput && volumeSupported) ? Visibility.Visible : Visibility.Collapsed);
		if (brightness.HasValue)
		{
			brightnessSection.Visibility = (options.ShowBrightness ? Visibility.Visible : Visibility.Collapsed);
			if (!brightnessDragging)
			{
				refreshing = true;
				brightnessSlider.Value = brightness.Value;
				brightnessValue.Text = brightness.Value.ToString();
				refreshing = false;
			}
		}
		else
		{
			brightnessSection.Visibility = Visibility.Collapsed;
		}
		if (volumeOk && !volumeDragging)
		{
			refreshing = true;
			volumeSlider.Value = Math.Round(volume * 100.0);
			volumeValue.Text = ((int)Math.Round(volume * 100.0)).ToString();
			refreshing = false;
		}
		ApplyVolumeUi();
		if (devices != null)
		{
			AudioCore.AudioDevice currentDevice = devices.FirstOrDefault((AudioCore.AudioDevice d) => d.IsDefault) ?? devices.FirstOrDefault();
			volumeDeviceName = ((currentDevice != null) ? currentDevice.Name : "输出设备");
			outputButton.Content = volumeDeviceName;
			outputButton.ToolTip = volumeDeviceName;
		}
		if (networks != null)
		{
			RebuildNetworks(networks);
		}
	}

	private void UpdateTileUi()
	{
		RenderTile(wifiTile, wifiIconHost, wifiStatus, "wifi", wifiSupported && wifiOn, wifiSupported ? (wifiOn ? "已开启" : "已关闭") : "设备不可用");
		RenderTile(btTile, btIconHost, btStatus, "bluetooth", btSupported && btOn, btSupported ? (btOn ? "已开启" : "已关闭") : "设备不可用");
	}

	private void RenderTile(Border tile, ContentControl iconHost, TextBlock status, string kind, bool on, string text)
	{
		status.Text = text;
		status.Foreground = (on ? accentBrush : MutedBrush);
		iconHost.Content = IconPacks.Glyph(kind, C.Prefs.Dock.IconPack, C.Prefs.Dock.Accent, on ? accentBrush : TextBrush, 24.0);
		tile.BorderBrush = (on ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, accentColor.R, accentColor.G, accentColor.B)) : lineBrush);
	}

	private void ApplyVolumeUi()
	{
		muteIcon.Content = IconPacks.Glyph("volume", C.Prefs.Dock.IconPack, C.Prefs.Dock.Accent, muted ? accentBrush : TextBrush, 18.0);
		volumeSlider.IsEnabled = !muted;
		volumeSlider.Opacity = (muted ? 0.45 : 1.0);
	}

	// ==================== 网络 ====================

	private void ShowNetError(string text)
	{
		netErrorText.Text = text;
		netErrorText.Visibility = (string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible);
	}

	private Border SignalBars(int quality)
	{
		StackPanel bars = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 12.0, 0.0)
		};
		int level = (int)Math.Ceiling(Math.Max(1.0, Math.Min(3.0, quality / 34.0)));
		for (int i = 0; i < 3; i++)
		{
			System.Windows.Media.Brush brush = ((i < level) ? accentBrush : new SolidColorBrush(LightTheme ? System.Windows.Media.Color.FromArgb(60, 25, 28, 34) : System.Windows.Media.Color.FromArgb(60, byte.MaxValue, byte.MaxValue, byte.MaxValue)));
			bars.Children.Add(new Rectangle
			{
				Width = 4.0,
				Height = 5.0 + (double)(i * 3),
				RadiusX = 1.2,
				RadiusY = 1.2,
				Fill = brush,
				VerticalAlignment = VerticalAlignment.Bottom,
				Margin = new Thickness(1.5, 0.0, 1.5, 0.0)
			});
		}
		return new Border
		{
			Child = bars,
			VerticalAlignment = VerticalAlignment.Center
		};
	}

	private Border NetworkRow(WirelessCore.WifiNetwork network)
	{
		Border row = new Border
		{
			CornerRadius = new CornerRadius(10.0),
			Padding = new Thickness(10.0, 8.0, 10.0, 8.0),
			Margin = new Thickness(0.0, 3.0, 0.0, 3.0),
			Background = System.Windows.Media.Brushes.Transparent,
			Cursor = Cursors.Hand,
			Tag = network
		};
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		Border bars = SignalBars(network.Signal);
		Grid.SetColumn(bars, 0);
		grid.Children.Add(bars);
		TextBlock name = Label(network.Ssid, 12.5, FontWeights.Normal, TextBrush);
		name.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(name, 1);
		grid.Children.Add(name);
		bool known = (network.Secured && savedProfiles.Contains(network.Ssid));
		string tag = (network.Connected ? "已连接" : (connecting && pendingSsid == network.Ssid ? "连接中…" : (known ? "已保存" : (network.Secured ? "需密码" : "开放"))));
		TextBlock tagText = Label(tag, 10.5, FontWeights.Normal, (network.Connected || known ? accentBrush : MutedBrush));
		tagText.VerticalAlignment = VerticalAlignment.Center;
		tagText.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		Grid.SetColumn(tagText, 2);
		grid.Children.Add(tagText);
		row.Child = grid;
		row.MouseEnter += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			row.Background = hoverBrush;
		};
		row.MouseLeave += (object s, System.Windows.Input.MouseEventArgs e) =>
		{
			row.Background = System.Windows.Media.Brushes.Transparent;
		};
		row.MouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			WirelessCore.WifiNetwork target = row.Tag as WirelessCore.WifiNetwork;
			if (target == null || connecting)
			{
				return;
			}
			if (target.Connected)
			{
				return;
			}
			if (target.Secured)
			{
				if (savedProfiles.Contains(target.Ssid))
				{
					StartConnectTask(target.Ssid, null);
				}
				else
				{
					ShowPasswordPrompt(target.Ssid, resetText: true);
				}
			}
			else
			{
				StartConnectTask(target.Ssid, null);
			}
		};
		return row;
	}

	private void RebuildNetworks(List<WirelessCore.WifiNetwork> networks)
	{
		// 正在输入密码时跳过列表重建，保持输入框位置与输入焦点
		if (!connecting && passwordPanel.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(pendingSsid))
		{
			return;
		}
		networksPanel.Children.Clear();
		WirelessCore.WifiNetwork connected = networks.FirstOrDefault((WirelessCore.WifiNetwork n) => n.Connected);
		currentSsid = ((connected != null) ? connected.Ssid : "");
		currentNetText.Text = (currentSsid.Length == 0 ? "未连接网络" : "已连接：" + currentSsid);
		if (currentSsid.Length > 0)
		{
			wifiStatus.Text = currentSsid;
		}
		foreach (WirelessCore.WifiNetwork network in networks)
		{
			networksPanel.Children.Add(NetworkRow(network));
		}
	}

	private void RefreshNetworks(bool scan)
	{
		Task.Run(delegate
		{
			try
			{
				if (scan)
				{
					WirelessCore.Scan();
					Task.Delay(2500).Wait();
				}
				List<WirelessCore.WifiNetwork> networks = WirelessCore.Networks();
				HashSet<string> profiles = null;
				try
				{
					profiles = WirelessCore.KnownProfiles();
				}
				catch
				{
				}
				Dispatcher.BeginInvoke(new Action(delegate
				{
					if (profiles != null)
					{
						savedProfiles = profiles;
					}
					RebuildNetworks(networks);
				}));
			}
			catch (Exception ex)
			{
				Dispatcher.BeginInvoke(new Action(delegate
				{
					ShowNetError("扫描失败：" + ex.Message);
				}));
			}
		});
	}

	// 密码输入框直接显示在所点 Wi-Fi 行下方
	private void ShowPasswordPrompt(string ssid, bool resetText)
	{
		pendingSsid = ssid;
		passwordPrompt.Text = "连接「" + ssid + "」需要密码：";
		if (resetText)
		{
			passwordBox.Text = "";
		}
		networksPanel.Children.Remove(passwordPanel);
		int index = -1;
		for (int i = 0; i < networksPanel.Children.Count; i++)
		{
			if (networksPanel.Children[i] is Border border && border.Tag is WirelessCore.WifiNetwork network && network.Ssid == ssid)
			{
				index = i;
				break;
			}
		}
		if (index >= 0)
		{
			networksPanel.Children.Insert(index + 1, passwordPanel);
			passwordPanel.Visibility = Visibility.Visible;
			passwordBox.Focus();
		}
	}

	private void HidePasswordPrompt()
	{
		networksPanel.Children.Remove(passwordPanel);
		passwordPanel.Visibility = Visibility.Collapsed;
	}

	private void StartConnect()
	{
		string ssid = pendingSsid;
		string password = passwordBox.Text;
		if (string.IsNullOrWhiteSpace(ssid) || string.IsNullOrWhiteSpace(password))
		{
			return;
		}
		HidePasswordPrompt();
		StartConnectTask(ssid, password);
	}

	private void StartConnectTask(string ssid, string password)
	{
		connecting = true;
		pendingSsid = ssid;
		ShowNetError("");
		Task.Run(delegate
		{
			string error = null;
			try
			{
				error = WirelessCore.Connect(ssid, password);
				Task.Delay(4000).Wait();
				List<WirelessCore.WifiNetwork> networks = WirelessCore.Networks();
				WirelessCore.WifiNetwork connectedNow = networks.FirstOrDefault((WirelessCore.WifiNetwork n) => n.Connected);
				bool ok = connectedNow != null && connectedNow.Ssid == ssid;
				Dispatcher.BeginInvoke(new Action(delegate
				{
					connecting = false;
					if (ok && !string.IsNullOrEmpty(ssid))
					{
						savedProfiles.Add(ssid);
					}
					RebuildNetworks(networks);
					if (ok)
					{
						ShowNetError("");
						pendingSsid = "";
					}
					else if (password == null && error != null && error.Contains("未找到"))
					{
						ShowPasswordPrompt(ssid, resetText: true);
					}
					else
					{
						ShowNetError(string.IsNullOrEmpty(error) ? "未能切换到「" + ssid + "」。请确认信号与密码。" : error);
					}
				}));
			}
			catch (Exception ex)
			{
				Dispatcher.BeginInvoke(new Action(delegate
				{
					connecting = false;
					ShowNetError("连接失败：" + ex.Message);
					RefreshNetworks(scan: false);
				}));
			}
		});
	}

	private void ToggleRadio(bool wifi)
	{
		bool targetOn = !(wifi ? wifiOn : btOn);
		string kind = (wifi ? "wifi" : "bt");
		Task.Run(delegate
		{
			CcReply reply = Bridge.SetRadio(kind, targetOn);
			Dispatcher.BeginInvoke(new Action(delegate
			{
				if (!reply.Ok)
				{
					ShowNetError((wifi ? "Wi-Fi " : "蓝牙 ") + "开关失败：" + reply.Error);
				}
				else if (wifi)
				{
					wifiOn = targetOn;
					UpdateTileUi();
					RefreshDynamic();
				}
				else
				{
					btOn = targetOn;
					UpdateTileUi();
					RefreshDynamic();
				}
			}));
		});
	}

	// ==================== 输出设备 / 蓝牙 ====================

	private void OpenOutputMenu()
	{
		suppressHide = true;
		ContextMenu menu = MenuTheme.Create(LightTheme, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		List<AudioCore.AudioDevice> devices = AudioCore.Devices();
		if (devices.Count == 0)
		{
			MenuItem empty = new MenuItem
			{
				Header = "没有可用的输出设备",
				IsEnabled = false
			};
			menu.Items.Add(empty);
		}
		foreach (AudioCore.AudioDevice device in devices)
		{
			MenuItem item = new MenuItem
			{
				Header = (device.IsDefault ? "✓ " : "") + device.Name
			};
			AudioCore.AudioDevice captured = device;
			item.Click += (object s, RoutedEventArgs e) =>
			{
				if (AudioCore.TrySetDefault(captured.Id))
				{
					outputHint.Visibility = Visibility.Collapsed;
					volumeDeviceName = captured.Name;
					outputButton.Content = volumeDeviceName;
					outputButton.ToolTip = volumeDeviceName;
				}
				else
				{
					// 部分新系统（如 Win11 25H2）已移除 IPolicyConfig COM 类，
					// 无法以编程方式切换默认设备：给出提示并引导到系统设置。
					outputHint.Visibility = Visibility.Visible;
				}
			};
			menu.Items.Add(item);
		}
		MenuItem soundSettings = new MenuItem
		{
			Header = "打开系统声音设置"
		};
		soundSettings.Click += (object s, RoutedEventArgs e) =>
		{
			try
			{
				System.Diagnostics.Process.Start("ms-settings:sound");
			}
			catch
			{
			}
		};
		menu.Items.Add(new Separator());
		menu.Items.Add(soundSettings);
		menu.Closed += (object s, RoutedEventArgs e) =>
		{
			suppressHide = false;
			if (!IsKeyboardFocusWithin && !IsActive)
			{
				Hide();
			}
		};
		menu.PlacementTarget = outputButton;
		menu.Placement = PlacementMode.Bottom;
		menu.IsOpen = true;
	}

	private void RebuildBluetooth(List<string> lines)
	{
		btListPanel.Children.Clear();
		if (lines == null || lines.Count == 0)
		{
			btListPanel.Children.Add(Label("没有已配对的设备", 11.5, FontWeights.Normal, MutedBrush));
			return;
		}
		foreach (string line in lines)
		{
			string[] parts = line.Split('\t');
			if (parts.Length < 3)
			{
				continue;
			}
			string id = parts[0];
			string name = parts[1];
			bool connected = parts[2] == "True";
			Border row = new Border
			{
				CornerRadius = new CornerRadius(10.0),
				Padding = new Thickness(10.0, 7.0, 10.0, 7.0),
				Margin = new Thickness(0.0, 3.0, 0.0, 3.0),
				Background = System.Windows.Media.Brushes.Transparent
			};
			Grid grid = new Grid();
			grid.ColumnDefinitions.Add(new ColumnDefinition());
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			TextBlock nameText = Label(name, 12.0, FontWeights.Normal, TextBrush);
			nameText.VerticalAlignment = VerticalAlignment.Center;
			Grid.SetColumn(nameText, 0);
			grid.Children.Add(nameText);
			TextBlock stateText = Label(connected ? "已连接" : "未连接", 10.5, FontWeights.Normal, connected ? accentBrush : MutedBrush);
			stateText.VerticalAlignment = VerticalAlignment.Center;
			stateText.Margin = new Thickness(8.0, 0.0, 8.0, 0.0);
			Grid.SetColumn(stateText, 1);
			grid.Children.Add(stateText);
			Button more = new Button
			{
				Content = "⋯",
				Width = 30.0,
				Height = 26.0,
				Padding = new Thickness(0.0),
				Background = System.Windows.Media.Brushes.Transparent,
				BorderThickness = new Thickness(0.0),
				Foreground = MutedBrush,
				Cursor = Cursors.Hand,
				VerticalAlignment = VerticalAlignment.Center
			};
			more.Click += (object s, RoutedEventArgs e) =>
			{
				OpenDeviceMenu(more, id, name);
			};
			Grid.SetColumn(more, 2);
			grid.Children.Add(more);
			row.Child = grid;
			btListPanel.Children.Add(row);
		}
	}

	private void OpenDeviceMenu(Button anchor, string id, string name)
	{
		suppressHide = true;
		ContextMenu menu = MenuTheme.Create(LightTheme, C.Prefs.Dock.Accent, C.Prefs.Dock.Material.Tint, (int)C.Prefs.Dock.OpacityPercent);
		MenuItem unpair = new MenuItem
		{
			Header = "取消配对"
		};
		unpair.Click += (object s, RoutedEventArgs e) =>
		{
			if (System.Windows.MessageBox.Show(this, "确定要取消配对「" + name + "」吗？", "蓝牙设备", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
			{
				Task.Run(delegate
				{
					CcReply reply = Bridge.Unpair(id);
					Dispatcher.BeginInvoke(new Action(delegate
					{
						if (!reply.Ok)
						{
							System.Windows.MessageBox.Show(this, reply.Error, "取消配对失败");
						}
						RefreshBluetooth();
					}));
				});
			}
		};
		menu.Items.Add(unpair);
		MenuItem open = new MenuItem
		{
			Header = "打开蓝牙设置"
		};
		open.Click += (object s, RoutedEventArgs e) =>
		{
			OpenSettings("ms-settings:bluetooth");
		};
		menu.Items.Add(open);
		menu.Closed += (object s, RoutedEventArgs e) =>
		{
			suppressHide = false;
			if (!IsKeyboardFocusWithin && !IsActive)
			{
				Hide();
			}
		};
		menu.PlacementTarget = anchor;
		menu.Placement = PlacementMode.Bottom;
		menu.IsOpen = true;
	}

	private void RefreshBluetooth()
	{
		Task.Run(delegate
		{
			try
			{
				CcReply reply = Bridge.BluetoothDevices();
				List<string> lines = ((reply.Ok && !string.IsNullOrEmpty(reply.Value)) ? reply.Value.Split('\n').ToList() : new List<string>());
				Dispatcher.BeginInvoke(new Action(delegate
				{
					RebuildBluetooth(lines);
				}));
			}
			catch
			{
			}
		});
	}

	// ==================== 亮度 ====================

	private DispatcherTimer brightnessTimer;

	private int pendingBrightness = -1;

	private void ScheduleBrightnessSet()
	{
		pendingBrightness = (int)Math.Round(brightnessSlider.Value);
		if (brightnessTimer == null)
		{
			brightnessTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(250.0)
			};
			brightnessTimer.Tick += (object s, EventArgs e) =>
			{
				brightnessTimer.Stop();
				int value = pendingBrightness;
				pendingBrightness = -1;
				if (value >= 0)
				{
					BrightnessCore.TrySet(value);
				}
			};
		}
		brightnessTimer.Stop();
		brightnessTimer.Start();
	}
}
