using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

public static class SelfTest
{
	private sealed class ButtonTestWindow : GlassWindow
	{
		public ButtonTestWindow()
			: base(12.0)
		{
		}

		private void RequireCaptureAllowed()
		{
			if (!Native.GetWindowDisplayAffinity(new WindowInteropHelper(this).EnsureHandle(), out var value))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowDisplayAffinity self-test");
			}
			if (value != 0)
			{
				throw new Exception("玻璃窗口仍被标记为限制屏幕捕获：" + value);
			}
		}

		public void CheckCaptureCompatibility()
		{
			Width = 240.0;
			Height = 180.0;
			Left = SystemParameters.WorkArea.Left + 20.0;
			Top = SystemParameters.WorkArea.Top + 20.0;
			RequireCaptureAllowed();
			Backdrop(light: false);
			RequireCaptureAllowed();
			Show();
			Backdrop(light: true);
			RequireCaptureAllowed();
			Hide();
			RequireCaptureAllowed();
		}

		public void CheckHandlers()
		{
			Button button = Button("编辑", null, 60.0);
			bool deferredCalled = false;
			button.ApplyTemplate();
			button.Click += (object s, RoutedEventArgs e) =>
			{
				deferredCalled = true;
			};
			button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
			bool inlineCalled = false;
			Button button2 = Button("添加", (object s, RoutedEventArgs e) =>
			{
				inlineCalled = true;
			});
			button2.ApplyTemplate();
			button2.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
			if (!deferredCalled || !inlineCalled)
			{
				throw new Exception("按钮事件绑定失败");
			}
			string[] array = new string[8] { "play", "pause", "next", "previous", "music", "shuffle", "repeat", "sun" };
			foreach (string text in array)
			{
				Button button3 = Button("", null, 52.0);
				button3.Content = Polish.Icon(text, Polish.Ink(light: true), 22.0);
				button3.ApplyTemplate();
				button3.Measure(new Size(52.0, 52.0));
				button3.Arrange(new Rect(0.0, 0.0, 52.0, 52.0));
				if (button3.DesiredSize.Width <= 0.0)
				{
					throw new Exception("组件图标布局失败：" + text);
				}
			}
			ToggleButton toggleButton = Controller.VisibilitySwitch();
			toggleButton.ApplyTemplate();
			Border border = (Border)toggleButton.Template.FindName("Thumb", toggleButton);
			toggleButton.IsChecked = true;
			if (border.HorizontalAlignment != HorizontalAlignment.Right)
			{
				throw new Exception("开关开启状态失败");
			}
			toggleButton.IsChecked = false;
			if (border.HorizontalAlignment != HorizontalAlignment.Left)
			{
				throw new Exception("开关关闭状态失败");
			}
			int clicks = 0;
			toggleButton.Click += (object s, RoutedEventArgs e) =>
			{
				clicks++;
			};
			toggleButton.IsChecked = true;
			toggleButton.IsChecked = false;
			if (clicks != 0)
			{
				throw new Exception("状态同步触发了用户点击");
			}
			toggleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
			if (clicks != 1)
			{
				throw new Exception("组件点击命令没有执行");
			}
		}
	}

	public static int Run()
	{
		try
		{
			CheckMemoryResources();
			MenuAppearanceChecks.Run();
			DockBehaviorChecks.Run();
			FeedbackChecks.Run();
			PersonalizationChecks.Run();
			ButtonTestWindow buttonTestWindow = new ButtonTestWindow();
			try
			{
				buttonTestWindow.CheckHandlers();
				buttonTestWindow.CheckCaptureCompatibility();
			}
			finally
			{
				buttonTestWindow.Close();
			}
			bool[] array = new bool[2] { false, true };
			foreach (bool light in array)
			{
				Window window = new Window
				{
					Width = 440.0,
					Height = 350.0
				};
				try
				{
					Polish.Apply(window, light);
					StackPanel stackPanel = (StackPanel)(window.Content = new StackPanel());
					Button button = new Button
					{
						Content = "样式"
					};
					ComboBox comboBox = new ComboBox();
					comboBox.ItemsSource = new string[2] { "A", "B" };
					comboBox.SelectedIndex = 0;
					ComboBox comboBox2 = comboBox;
					TextBox textBox = new TextBox
					{
						Text = new string('长', 120),
						FontSize = 17.0,
						TextWrapping = TextWrapping.Wrap,
						MinHeight = 48.0,
						Padding = new Thickness(12.0, 9.0, 12.0, 9.0)
					};
					CheckBox checkBox = new CheckBox
					{
						Content = "显示",
						IsChecked = true
					};
					Slider slider = new Slider
					{
						Minimum = 0.0,
						Maximum = 100.0,
						Value = 25.0
					};
					stackPanel.Children.Add(button);
					stackPanel.Children.Add(comboBox2);
					stackPanel.Children.Add(textBox);
					stackPanel.Children.Add(checkBox);
					stackPanel.Children.Add(slider);
					new WindowInteropHelper(window).EnsureHandle();
					stackPanel.Measure(new Size(400.0, 300.0));
					stackPanel.Arrange(new Rect(0.0, 0.0, 400.0, 300.0));
					stackPanel.UpdateLayout();
					Control[] array2 = new Control[5] { button, comboBox2, textBox, checkBox, slider };
					for (int j = 0; j < array2.Length; j++)
					{
						array2[j].ApplyTemplate();
					}
					double[] array3 = new double[3] { 13.0, 17.0, 24.0 };
					for (int j = 0; j < array3.Length; j++)
					{
						double fontSize = array3[j];
						TextBox textBox2 = new TextBox
						{
							Text = "闹钟 08:30 中文测试",
							FontSize = fontSize
						};
						stackPanel.Children.Add(textBox2);
						textBox2.ApplyTemplate();
						textBox2.Measure(new Size(360.0, double.PositiveInfinity));
						textBox2.Arrange(new Rect(0.0, 0.0, 360.0, textBox2.DesiredSize.Height));
						textBox2.UpdateLayout();
						ScrollViewer scrollViewer = (ScrollViewer)textBox2.Template.FindName("PART_ContentHost", textBox2);
						TextBlock textBlock = new TextBlock
						{
							Text = textBox2.Text,
							FontFamily = textBox2.FontFamily,
							FontSize = fontSize
						};
						textBlock.Measure(new Size(360.0, double.PositiveInfinity));
						if (scrollViewer == null || scrollViewer.ViewportHeight + 0.5 < textBlock.DesiredSize.Height)
						{
							throw new Exception("输入框文字被裁切：" + fontSize);
						}
						stackPanel.Children.Remove(textBox2);
					}
					Track track = (Track)slider.Template.FindName("PART_Track", slider);
					if (track == null || track.Thumb == null)
					{
						throw new Exception("新滑块模板无 Thumb");
					}
					slider.Value = 75.0;
					if (Math.Abs(track.Value - 75.0) > 0.01)
					{
						throw new Exception("新滑块值绑定失败");
					}
					textBox.Measure(new Size(320.0, double.PositiveInfinity));
					if (textBox.DesiredSize.Height <= 48.0)
					{
						throw new Exception("长便签标题没有按行增高");
					}
					Border border = (Border)checkBox.Template.FindName("Box", checkBox);
					if (border == null)
					{
						throw new Exception("勾选框缺少点击区域");
					}
					bool[] array4 = new bool[4] { false, true, false, true };
					for (int j = 0; j < array4.Length; j++)
					{
						bool value = array4[j];
						checkBox.IsChecked = value;
						stackPanel.UpdateLayout();
						Point point = border.TranslatePoint(new Point(border.ActualWidth / 2.0, border.ActualHeight / 2.0), checkBox);
						if (checkBox.InputHitTest(point) == null)
						{
							throw new Exception("勾选框中心不能点击，状态=" + value);
						}
						FrameworkElement frameworkElement = (FrameworkElement)VisualTreeHelper.GetChild(checkBox, 0);
						Point point2 = frameworkElement.TranslatePoint(new Point(23.0, frameworkElement.ActualHeight / 2.0), checkBox);
						if (checkBox.InputHitTest(point2) == null)
						{
							throw new Exception("勾选框与文字间隙不能点击");
						}
					}
					if (comboBox2.Template.FindName("PART_Popup", comboBox2) == null)
					{
						throw new Exception("新下拉模板无 Popup");
					}
				}
				finally
				{
					window.Close();
				}
			}
			Preferences preferences = new Preferences
			{
				Light = true,
				DockGap = 25.0,
				Imported = true,
				OpacityPercent = 40.0
			};
			preferences.Removed.Add("C:\\工具.lnk");
			preferences.Apps.Add(new Entry
			{
				Name = "中文 软件",
				Path = "C:\\测试目录\\工具.lnk",
				Pinned = true
			});
			using (MemoryStream memoryStream = new MemoryStream())
			{
				DataContractJsonSerializer dataContractJsonSerializer = new DataContractJsonSerializer(typeof(Preferences));
				dataContractJsonSerializer.WriteObject((Stream)memoryStream, (object)preferences);
				memoryStream.Position = 0L;
				Preferences preferences2 = (Preferences)dataContractJsonSerializer.ReadObject((Stream)memoryStream);
				if (preferences2.Apps.Count != 1 || preferences2.Apps[0].Name != "中文 软件" || !preferences2.Apps[0].Pinned || preferences2.DockGap != 25.0 || preferences2.OpacityPercent != 40.0 || preferences2.Removed.Count != 1)
				{
					throw new Exception("配置往返失败");
				}
			}
			if (LayoutRules.Pages(0, 35) != 1 || LayoutRules.Pages(35, 35) != 1 || LayoutRules.Pages(36, 35) != 2 || LayoutRules.Pages(70, 35) != 2)
			{
				throw new Exception("分页边界失败");
			}
			if (!LayoutRules.Matches("Microsoft Visual Studio Code", "code MICROSOFT") || !LayoutRules.Matches("剪映专业版", "剪映") || LayoutRules.Matches("Steam", "maya"))
			{
				throw new Exception("搜索失败");
			}
			if (LayoutRules.GlassOpacity(-1.0) != 0.0 || LayoutRules.GlassOpacity(100.0) != 100.0 || LayoutRules.GlassOpacity(double.NaN) != 72.0)
			{
				throw new Exception("透明度边界失败");
			}
			File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-result.txt"), "PASS: transparent material bounds and widget recovery menu; Dock rendering/order/mouse-capture release and native process path/reuse; light/dark nested menus, submenu open/close, checkmarks, disabled items, header sliders, shortcut overlay cache and legacy configuration; shared theme styles, bounded thumbnails and stream-independent pixels, hidden board release/reopen; recordable glass window affinity before/after backdrop/show/hide; WPF light/dark button/combo/text/check/slider templates, value bindings, button handlers, visibility switch, configuration, removed entries, opacity bounds, pages and search\r\n", Encoding.UTF8);
			return 0;
		}
		catch (Exception ex)
		{
			File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-result.txt"), ex.ToString());
			return 1;
		}
	}

	private static void CheckMemoryResources()
	{
		Window window = new Window();
		Window window2 = new Window();
		try
		{
			bool[] array = new bool[2] { false, true };
			foreach (bool light in array)
			{
				Polish.Apply(window, light);
				Polish.Apply(window2, light);
				Type[] array2 = new Type[5]
				{
					typeof(Button),
					typeof(TextBox),
					typeof(ComboBox),
					typeof(Slider),
					typeof(CheckBox)
				};
				foreach (Type type in array2)
				{
					Style style = (Style)window.Resources[type];
					if (!style.IsSealed || style != window2.Resources[type])
					{
						throw new Exception("主题样式未共享：" + type.Name);
					}
				}
				Polish.Apply(window, light);
			}
		}
		finally
		{
			window.Close();
			window2.Close();
		}
		int[][] array3 = new int[3][]
		{
			new int[2] { 512, 4096 },
			new int[2] { 4096, 512 },
			new int[2] { 64, 64 }
		};
		foreach (int[] array4 in array3)
		{
			byte[] pixels = new byte[array4[0] * array4[1]];
			BitmapSource source = BitmapSource.Create(array4[0], array4[1], 96.0, 96.0, PixelFormats.Gray8, null, pixels, array4[0]);
			PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
			pngBitmapEncoder.Frames.Add(BitmapFrame.Create(source));
			using MemoryStream memoryStream = new MemoryStream();
			pngBitmapEncoder.Save(memoryStream);
			BitmapImage bitmapImage = ThumbnailImages.Read(memoryStream.ToArray(), 600);
			if (!bitmapImage.IsFrozen || bitmapImage.PixelWidth > 600 || bitmapImage.PixelHeight > 600 || bitmapImage.PixelWidth > array4[0] || bitmapImage.PixelHeight > array4[1])
			{
				throw new Exception("缩略图尺寸或冻结状态无效");
			}
			int num = (bitmapImage.PixelWidth * bitmapImage.Format.BitsPerPixel + 7) / 8;
			bitmapImage.CopyPixels(new byte[num * bitmapImage.PixelHeight], num, 0);
		}
		Controller controller = new Controller(desktop: false);
		try
		{
			Entry probe = new Entry
			{
				Name = "内存检查",
				Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")
			};
			controller.Prefs.Apps.Add(probe);
			controller.Board.Open();
			if (!controller.Board.HasView || !controller.Board.IsVisible)
			{
				throw new Exception("面板未创建");
			}
			controller.Board.Hide();
			controller.Board.ModalBusy = true;
			if (controller.Board.ReleaseHiddenView())
			{
				throw new Exception("忙碌时清理了面板");
			}
			controller.Board.ModalBusy = false;
			if (!controller.Board.ReleaseHiddenView() || controller.Board.HasView)
			{
				throw new Exception("隐藏面板资源未释放");
			}
			controller.Board.Render();
			controller.Board.Open();
			if (!controller.Board.HasView || !controller.Board.IsVisible || !controller.Prefs.Apps.Contains(probe))
			{
				throw new Exception("面板重开或配置保留失败");
			}
		}
		finally
		{
			controller.ShuttingDown = true;
			controller.Dock.ReleaseHotkey();
			controller.Board.Close();
			controller.Dock.Close();
		}
	}
}
