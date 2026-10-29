using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassBoard;

public static class MenuAppearanceChecks
{
	private static void Drain()
	{
		DispatcherFrame frame = new DispatcherFrame();
		Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)(() =>
		{
			frame.Continue = false;
		}));
		Dispatcher.PushFrame(frame);
	}

	public static void Run()
	{
		bool[] array = new bool[2] { false, true };
		foreach (bool flag in array)
		{
			Window window = new Window
			{
				Width = 400.0,
				Height = 240.0,
				ShowInTaskbar = false
			};
			Button placementTarget = (Button)(window.Content = new Button
			{
				Content = "菜单检查"
			});
			Polish.Apply(window, flag);
			ContextMenu contextMenu = MenuTheme.Create(flag);
			MenuItem menuItem = new MenuItem
			{
				Header = "子菜单标题"
			};
			MenuItem menuItem2 = new MenuItem
			{
				Header = "创作 / 中文长标题名称"
			};
			MenuItem menuItem3 = new MenuItem
			{
				Header = "嵌套菜单"
			};
			MenuItem menuItem4 = new MenuItem
			{
				Header = "游戏"
			};
			menuItem3.Items.Add(menuItem4);
			menuItem.Items.Add(menuItem2);
			menuItem.Items.Add(menuItem3);
			menuItem.Items.Add(new Separator());
			MenuItem menuItem5 = new MenuItem
			{
				Header = "系统音乐模式",
				IsCheckable = true,
				IsChecked = true
			};
			contextMenu.Items.Add(menuItem5);
			contextMenu.Items.Add(new Separator());
			contextMenu.Items.Add(menuItem);
			bool clicked = false;
			menuItem2.Click += (object s, RoutedEventArgs e) =>
			{
				clicked = true;
			};
			try
			{
				window.Show();
				contextMenu.PlacementTarget = placementTarget;
				contextMenu.IsOpen = true;
				Drain();
				contextMenu.UpdateLayout();
				menuItem.ApplyTemplate();
				menuItem.IsSubmenuOpen = true;
				Drain();
				MenuItem[] array2 = new MenuItem[3] { menuItem, menuItem2, menuItem3 };
				foreach (MenuItem obj in array2)
				{
					obj.ApplyTemplate();
					if (obj.Style != contextMenu.Resources[typeof(MenuItem)])
					{
						throw new Exception("子菜单未使用共享主题");
					}
					if (obj.Foreground != Polish.Ink(flag))
					{
						throw new Exception("菜单文字未使用主题颜色");
					}
				}
				Popup popup = menuItem.Template.FindName("PART_Popup", menuItem) as Popup;
				Border border = ((popup == null) ? null : (popup.Child as Border));
				SolidColorBrush solidColorBrush = ((border == null) ? null : (border.Background as SolidColorBrush));
				// 菜单底色在 MenuTheme.Colors 中会掺入 10% 强调色（有意设计），断言须与主题实际产出一致
				if (solidColorBrush == null || solidColorBrush.Color != MenuTheme.Palette(flag).bg)
				{
					throw new Exception("二级菜单背景配色错误");
				}
				menuItem.IsSubmenuOpen = false;
				Drain();
				if (popup.IsOpen)
				{
					throw new Exception("子菜单关闭绑定失败");
				}
				menuItem.IsSubmenuOpen = true;
				Drain();
				menuItem3.IsSubmenuOpen = true;
				Drain();
				menuItem4.ApplyTemplate();
				if (menuItem4.Style != menuItem2.Style)
				{
					throw new Exception("三级菜单未继承主题");
				}
				menuItem5.ApplyTemplate();
				if (!(menuItem5.Template.FindName("Check", menuItem5) is TextBlock { Visibility: Visibility.Visible } textBlock))
				{
					throw new Exception("菜单选中标记丢失");
				}
				menuItem5.IsChecked = false;
				if (textBlock.Visibility != Visibility.Collapsed)
				{
					throw new Exception("菜单取消选中标记失败");
				}
				menuItem2.IsEnabled = false;
				if (((Border)menuItem2.Template.FindName("Chrome", menuItem2)).Opacity >= 1.0)
				{
					throw new Exception("菜单禁用样式丢失");
				}
				menuItem2.IsEnabled = true;
				menuItem2.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
				if (!clicked)
				{
					throw new Exception("子菜单点击事件丢失");
				}
				Slider slider = new Slider
				{
					Minimum = 28.0,
					Maximum = 80.0,
					Value = 48.0,
					Width = 180.0
				};
				contextMenu.Items.Add(new MenuItem
				{
					Header = slider,
					StaysOpenOnClick = true
				});
				Drain();
				slider.Value = 56.0;
				if (slider.Value != 56.0)
				{
					throw new Exception("菜单内图标大小滑块失败");
				}
			}
			finally
			{
				contextMenu.IsOpen = false;
				window.Close();
			}
		}
		using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"IconSize\":56,\"ShowRunning\":false}")))
		{
			DockOptions dockOptions = (DockOptions)new DataContractJsonSerializer(typeof(DockOptions)).ReadObject((Stream)stream);
			if (dockOptions.ShowShortcutOverlays || dockOptions.SharperIcons || dockOptions.IconSize != 56.0 || dockOptions.ShowRunning)
			{
				throw new Exception("旧配置角标默认值或原设置被改变");
			}
		}
		string text = Path.Combine(Path.GetTempPath(), "GlassBoard-icon-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(text);
		object obj2 = null;
		object obj3 = null;
		try
		{
			string text2 = Path.Combine(text, "中文 快捷方式.lnk");
			Type typeFromProgID = Type.GetTypeFromProgID("WScript.Shell");
			obj2 = Activator.CreateInstance(typeFromProgID);
			obj3 = typeFromProgID.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, obj2, new object[1] { text2 });
			Type type = obj3.GetType();
			type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, obj3, new object[1] { Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\notepad.exe" });
			type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, obj3, new object[1] { Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\shell32.dll,3" });
			type.InvokeMember("Save", BindingFlags.InvokeMethod, null, obj3, null);
			byte[] inArray = File.ReadAllBytes(text2);
			ImageSource imageSource = Native.Icon(text2);
			ImageSource imageSource2 = Native.Icon(text2, showShortcutOverlays: true);
			if (imageSource == null || imageSource2 == null || imageSource != Native.Icon(text2) || imageSource == imageSource2)
			{
				throw new Exception("快捷方式角标开关或图标缓存失败");
			}
			if (Convert.ToBase64String(inArray) != Convert.ToBase64String(File.ReadAllBytes(text2)))
			{
				throw new Exception("读取图标改动了快捷方式");
			}
			BitmapSource bitmapSource = Native.Icon(text2, showShortcutOverlays: false, sharper: true) as BitmapSource;
			ImageSource imageSource3 = Native.Icon(text2, showShortcutOverlays: true, sharper: true);
			// Jumbo 通道输出固定 256×256，因此高清图标的上限是 256（此检查过去为 128，已被 Jumbo 设计取代）
			if (bitmapSource == null || imageSource3 == null || !bitmapSource.IsFrozen || bitmapSource.PixelWidth > 256 || bitmapSource.PixelHeight > 256 || bitmapSource == imageSource)
			{
				throw new Exception("高清图标尺寸、角标组合或缓存失败");
			}
			Native.ClearIconCache();
			if (imageSource == Native.Icon(text2))
			{
				throw new Exception("关闭高清图标没有清除缓存");
			}
			if (Native.Icon(text) == null)
			{
				throw new Exception("文件夹无角标图标失败");
			}
		}
		finally
		{
			if (obj3 != null && Marshal.IsComObject(obj3))
			{
				Marshal.ReleaseComObject(obj3);
			}
			if (obj2 != null && Marshal.IsComObject(obj2))
			{
				Marshal.ReleaseComObject(obj2);
			}
			Directory.Delete(text, recursive: true);
		}
	}
}
