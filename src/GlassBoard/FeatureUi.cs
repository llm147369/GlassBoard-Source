using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

public static class FeatureUi
{
	private static readonly BoundedCache<string, ImageSource> customIconCache = new BoundedCache<string, ImageSource>(128, StringComparer.OrdinalIgnoreCase);

	public static string PickFolder(Window window, string description)
	{
		using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
		{
			Description = description,
			ShowNewFolderButton = false
		};
		NativeWindow nativeWindow = new NativeWindow();
		nativeWindow.AssignHandle(new WindowInteropHelper(window).EnsureHandle());
		try
		{
			return (folderBrowserDialog.ShowDialog(nativeWindow) == DialogResult.OK) ? folderBrowserDialog.SelectedPath : null;
		}
		finally
		{
			nativeWindow.ReleaseHandle();
		}
	}

	public static HotkeySetting Record(Window owner, int action)
	{
		Window w = Dialog("录制快捷键", owner, 480.0, 230.0);
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(24.0)
		};
		w.Content = stackPanel;
		stackPanel.Children.Add(new TextBlock
		{
			Text = "按下一组快捷键",
			FontSize = 22.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
		});
		TextBlock hint = new TextBlock
		{
			Text = "需要 Ctrl / Alt / Shift，搭配字母、数字、功能键等。\nEsc 取消；占用情况会在保存时检查。",
			TextWrapping = TextWrapping.Wrap
		};
		stackPanel.Children.Add(hint);
		HotkeySetting captured = null;
		w.PreviewKeyDown += (object s, System.Windows.Input.KeyEventArgs e) =>
		{
			e.Handled = true;
			Key key = ((e.Key == Key.System) ? e.SystemKey : e.Key);
			if (key == Key.Escape)
			{
				w.Close();
			}
			else if (captured == null && key != Key.LeftCtrl && key != Key.RightCtrl && key != Key.LeftAlt && key != Key.RightAlt && key != Key.LeftShift && key != Key.RightShift)
			{
				uint modifiers = (uint)Keyboard.Modifiers;
				uint num = (uint)KeyInterop.VirtualKeyFromKey(key);
				if (!HotkeyRules.ModifierValues.Contains(modifiers) || !HotkeyRules.Keys().ContainsValue(num))
				{
					hint.Text = "不支持这组按键，请使用列表支持的组合。";
				}
				else
				{
					captured = new HotkeySetting
					{
						ActionId = action,
						Modifiers = modifiers,
						Key = num,
						Enabled = true
					};
					hint.Text = "已录制：" + HotkeyRules.Label(captured) + "\n释放按键完成录制。";
				}
			}
		};
		w.PreviewKeyUp += (object s, System.Windows.Input.KeyEventArgs e) =>
		{
			e.Handled = true;
			if (captured != null && Keyboard.Modifiers == ModifierKeys.None && !Keyboard.IsKeyDown(KeyInterop.KeyFromVirtualKey((int)captured.Key)))
			{
				w.DialogResult = true;
			}
		};
		w.ShowDialog();
		if (w.DialogResult != true)
		{
			return null;
		}
		return captured;
	}

	public static Window Dialog(string title, Window owner, double width = 650.0, double height = 560.0)
	{
		Window window = new Window
		{
			Title = "GlassBoard · " + title,
			Owner = owner,
			Width = Math.Min(width, SystemParameters.WorkArea.Width - 40.0),
			Height = Math.Min(height, SystemParameters.WorkArea.Height - 50.0),
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = new SolidColorBrush(Color.FromRgb(30, 33, 42)),
			Foreground = Brushes.White,
			Topmost = true
		};
		Polish.Apply(window, light: false);
		return window;
	}

	public static System.Windows.Controls.Button Button(string text, Action action)
	{
		System.Windows.Controls.Button button = new System.Windows.Controls.Button();
		button.Content = text;
		button.Padding = new Thickness(12.0, 7.0, 12.0, 7.0);
		button.Margin = new Thickness(0.0, 0.0, 8.0, 8.0);
		button.Click += (object s, RoutedEventArgs e) =>
		{
			action();
		};
		return button;
	}

	public static string Ask(Window owner, string title, string value = "")
	{
		Window w = Dialog(title, owner, 440.0, 180.0);
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(22.0)
		};
		w.Content = stackPanel;
		System.Windows.Controls.TextBox text = new System.Windows.Controls.TextBox
		{
			Text = value,
			MinHeight = 38.0,
			FontSize = 14.0
		};
		stackPanel.Children.Add(text);
		string result = null;
		System.Windows.Controls.Button apply = Button("确定", () =>
		{
			if (!string.IsNullOrWhiteSpace(text.Text))
			{
				result = text.Text.Trim();
				w.DialogResult = true;
			}
		});
		apply.Margin = new Thickness(0.0, 18.0, 0.0, 0.0);
		stackPanel.Children.Add(apply);
		w.Loaded += (object s, RoutedEventArgs e) =>
		{
			text.Focus();
			text.SelectAll();
		};
		text.KeyDown += (object s, System.Windows.Input.KeyEventArgs e) =>
		{
			if (e.Key == Key.Return)
			{
				apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
			}
		};
		w.ShowDialog();
		return result;
	}

	public static string StoreIcon(string path)
	{
		Directory.CreateDirectory(Path.Combine(Store.Folder, "icons"));
		BitmapImage bitmapImage = new BitmapImage();
		bitmapImage.BeginInit();
		bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
		bitmapImage.UriSource = new Uri(path);
		bitmapImage.DecodePixelWidth = 128;
		bitmapImage.EndInit();
		bitmapImage.Freeze();
		PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
		pngBitmapEncoder.Frames.Add(BitmapFrame.Create(bitmapImage));
		string text = Path.Combine(Store.Folder, "icons", Guid.NewGuid().ToString("N") + ".png");
		using FileStream stream = File.Create(text);
		pngBitmapEncoder.Save(stream);
		return text;
	}

	public static ImageSource Icon(Entry entry, bool showShortcutOverlays = false, bool sharper = false)
	{
		ImageSource result = null;
		if (RecycleBin.IsEntry(entry))
		{
			result = Native.RecycleIcon();
		}
		else if (SystemPlaces.IsComputer(entry))
		{
			result = Native.MyComputerIcon();
		}
		else if (!string.IsNullOrWhiteSpace(entry.IconPath) && File.Exists(entry.IconPath))
		{
			try
			{
				FileInfo fileInfo = new FileInfo(entry.IconPath);
				string key = fileInfo.FullName + "/" + fileInfo.Length + "/" + fileInfo.LastWriteTimeUtc.Ticks;
				if (customIconCache.TryGet(key, out var value))
				{
					result = value;
				}
				else
				{
					BitmapImage bitmapImage = new BitmapImage();
					bitmapImage.BeginInit();
					bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
					bitmapImage.UriSource = new Uri(entry.IconPath);
					bitmapImage.DecodePixelWidth = 128;
					bitmapImage.EndInit();
					bitmapImage.Freeze();
					customIconCache.Put(key, bitmapImage);
					result = bitmapImage;
				}
			}
			catch
			{
			}
		}
		if (result == null)
		{
			result = Native.Icon(entry.Path, showShortcutOverlays, sharper);
		}
		if (result == null && (entry.IsFolder || Directory.Exists(entry.Path)))
		{
			DrawingImage drawingImage = new DrawingImage(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(240, 191, 77)), new Pen(new SolidColorBrush(Color.FromRgb(204, 149, 45)), 1.0), Geometry.Parse("M 2,5 L 8,5 L 10,7 L 18,7 L 18,17 L 2,17 Z")));
			drawingImage.Freeze();
			result = drawingImage;
		}
		return IconNormalizer.Normalize(result);
	}
}
