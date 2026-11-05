using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

// “个性化 → 壁纸”设置页：一个总开关 + 样式 + 本地/官方图库 + 一键还原系统壁纸。
// 改动立即写入 Prefs.Wallpaper；壁纸应用/还原在后台线程执行（COM 设置大图时
// explorer 需要转码，放在主线程会卡界面），状态异步回写，不再二次应用。
public static class WallpaperSettingsUi
{
	private static readonly string[] StyleNames = new string[5] { "填充", "适应", "拉伸", "居中", "平铺" };

	// 与 Controller.Settings() 内 func2/func3/func4 类型完全一致
	public static void Build(StackPanel p, Controller C, bool light,
		Func<StackPanel, string, double, double, double, double, string, Action<double>, Slider> slider,
		Func<StackPanel, string, bool, Action<bool>, System.Windows.Controls.CheckBox> check,
		Func<StackPanel, string, string[], int, Action<int>, System.Windows.Controls.ComboBox> combo,
		Action applyLive)
	{
		WallpaperOptions d = (C.Prefs.Wallpaper ?? new WallpaperOptions()).Copy();
		d.Normalize();
		List<WallpaperCatalog.Entry> catalog = null;
		bool catalogBusy = false;

		// 状态栏在每次 Render 时重建，用“外提变量”让后台线程续写时始终指向最新控件
		TextBlock status = null;
		TextBlock galleryStatus = null;

		// 持久化当前草稿。注意：不再回调 applyLive——所有改动都已在各自处理器里实时生效，
		// 若再回调会重复应用壁纸（启用/换样式时 COM 慢操作执行两遍，界面明显卡顿）。
		void Apply()
		{
			d.Normalize();
			C.Prefs.Wallpaper = d;
			C.Save();
		}

		void Rebuild()
		{
			try
			{
				p.Children.Clear();
				Render(d);
			}
			catch (Exception ex)
			{
				Store.Trace("WALLPAPER settings rebuild failed: " + ex.GetType().FullName + " " + ex.Message + "\n" + ex.StackTrace);
			}
		}

		// 后台线程安全地回写状态文字（Rend 之后 status 指向新控件，闭包读的是变量本身）
		void PostStatus(string msg)
		{
			try
			{
				p.Dispatcher.BeginInvoke(new Action(delegate
				{
					try
					{
						if (status != null)
						{
							status.Text = msg;
						}
					}
					catch
					{
					}
				}));
			}
			catch
			{
			}
		}

		void PostGallery(string msg)
		{
			try
			{
				p.Dispatcher.BeginInvoke(new Action(delegate
				{
					try
					{
						if (galleryStatus != null)
						{
							galleryStatus.Text = msg;
						}
					}
					catch
					{
					}
				}));
			}
			catch
			{
			}
		}

		// 后台应用壁纸：不卡界面，完成后通过 PostStatus 回报结果
		void ApplyAsync(string path, int style, string okMessage, string failMessage)
		{
			Task.Run(delegate
			{
				bool r;
				try
				{
					r = WallpaperCore.Apply(path, style);
				}
				catch (Exception ex)
				{
					Store.Trace("WALLPAPER apply failed: " + ex.GetType().FullName + " " + ex.Message);
					r = false;
				}
				PostStatus(r ? okMessage : failMessage);
			});
		}

		// 后台还原系统壁纸（map 为快照副本，调用方随后可安全清空 o.Previous）
		void RestoreAsync(Dictionary<string, string> map, string okMessage, string failMessage)
		{
			Task.Run(delegate
			{
				bool r;
				try
				{
					r = WallpaperCore.Restore(map);
				}
				catch (Exception ex)
				{
					Store.Trace("WALLPAPER restore failed: " + ex.GetType().FullName + " " + ex.Message);
					r = false;
				}
				PostStatus(r ? okMessage : failMessage);
			});
		}

		void Render(WallpaperOptions o)
		{
			// —— 桌面壁纸（总开关 + 样式 + 导入/还原）——
			Border main = Polish.Section("桌面壁纸", "选择一张图片作为桌面壁纸，样式可调；关闭开关即还原系统壁纸，随时可再开启。", light);
			StackPanel mainPanel = (StackPanel)main.Child;
			status = new TextBlock
			{
				Foreground = Polish.Muted(light),
				TextWrapping = TextWrapping.Wrap,
				FontSize = 12.0,
				Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
			};
			mainPanel.Children.Add(status);
			check(mainPanel, "启用壁纸（开启后立即应用，关闭则还原系统壁纸）", o.Enabled, delegate (bool on)
			{
				o.Enabled = on;
				if (on)
				{
					if (!string.IsNullOrEmpty(o.Path) && File.Exists(o.Path))
					{
						status.Text = "正在应用壁纸…";
						ApplyAsync(o.Path, o.Style, "已启用：" + Path.GetFileName(o.Path), "壁纸应用失败，详见配置目录 error.log。");
					}
					else
					{
						status.Text = "壁纸已启用：尚未选择壁纸，从下方图库选择或导入本地图片。";
					}
				}
				else if (o.Previous.Count > 0)
				{
					status.Text = "正在还原系统壁纸…";
					RestoreAsync(o.Previous, "壁纸已关闭：已还原系统壁纸（设置保留，可随时再开）。", "壁纸已关闭，但还原原壁纸失败：原文件可能已被移动，或第三方壁纸软件拦截。");
				}
				else
				{
					status.Text = "壁纸已关闭：系统壁纸未改动。";
				}
				Apply();
			});
			check(mainPanel, "开机自动恢复上次壁纸", o.AutoRestore ?? true, delegate (bool on)
			{
				o.AutoRestore = on;
				Apply();
			});
			combo(mainPanel, "壁纸样式", StyleNames, o.Style, delegate (int v)
			{
				o.Style = v;
				Apply();
				if (o.Enabled && !string.IsNullOrEmpty(o.Path) && File.Exists(o.Path))
				{
					status.Text = "正在应用样式…";
					ApplyAsync(o.Path, v, "样式已应用。", "样式应用失败，详见配置目录 error.log。");
				}
			});
			WrapPanel actions = new WrapPanel
			{
				Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
			};
			actions.Children.Add(FeatureUi.Button("选择图片导入…", delegate
			{
				Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
				{
					Title = "选择壁纸图片",
					Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp",
					Multiselect = false
				};
				if (dialog.ShowDialog() == true)
				{
					string lib = WallpaperLibrary.Import(dialog.FileName, null, official: false);
					if (lib == null)
					{
						status.Text = "导入失败：请选择 JPG/PNG/BMP 图片（≤30MB）且文件完整。";
						return;
					}
					if (o.Previous.Count == 0)
					{
						o.Previous = WallpaperCore.Capture();
					}
					o.Path = lib;
					o.Source = 0;
					o.Enabled = true;
					Apply();
					Rebuild();
					PostStatus("正在应用壁纸…");
					ApplyAsync(lib, o.Style, "已导入并应用：" + Path.GetFileName(lib), "壁纸设置失败，详见配置目录 error.log。");
				}
			}));
			actions.Children.Add(FeatureUi.Button("恢复系统壁纸", delegate
			{
				if (o.Previous.Count == 0)
				{
					status.Text = "没有可还原的记录（尚未用本功能改过壁纸）。";
					return;
				}
				Dictionary<string, string> prev = new Dictionary<string, string>(o.Previous);
				o.Path = null;
				Apply();
				Rebuild();
				PostStatus("正在还原系统壁纸…");
				Task.Run(delegate
				{
					bool r;
					try
					{
						r = WallpaperCore.Restore(prev);
					}
					catch (Exception ex)
					{
						Store.Trace("WALLPAPER restore failed: " + ex.GetType().FullName + " " + ex.Message);
						r = false;
					}
					if (r)
					{
						// 成功后回到界面线程清掉记录，避免下次误还原
						p.Dispatcher.BeginInvoke(new Action(delegate
						{
							o.Previous.Clear();
							Apply();
							Rebuild();
						}));
					}
					PostStatus(r ? "已还原系统壁纸。" : "还原失败：原壁纸文件可能已不存在，详见配置目录 error.log。");
				});
			}));
			mainPanel.Children.Add(actions);
			mainPanel.Children.Add(new TextBlock
			{
				Text = "提示：若正在使用 Wallpaper Engine、元气桌面等第三方动态壁纸软件，本功能可能不生效或被其覆盖；动态壁纸（视频/粒子等）将在后续版本考虑，本版本仅支持静态图片。",
				Foreground = Polish.Muted(light),
				TextWrapping = TextWrapping.Wrap,
				FontSize = 11.0,
				Margin = new Thickness(0.0, 10.0, 0.0, 0.0)
			});
			p.Children.Add(main);

			// —— 壁纸图库（本地 + 官方合并，网格按宽度自适应列数）——
			Border gallery = Polish.Section("壁纸图库", "本地导入过的壁纸与官方内置壁纸都在这里；鼠标悬浮显示操作按钮，点击直接生效。", light);
			StackPanel galleryPanel = (StackPanel)gallery.Child;

			// 网格列数跟随板块宽度自适应（最小 2 列），窗口拉伸时自动重排
			Action<UniformGrid> attachAdaptive = (UniformGrid grid) =>
			{
				galleryPanel.SizeChanged += delegate
				{
					try
					{
						double w = galleryPanel.ActualWidth;
						if (w > 80.0)
						{
							grid.Columns = Math.Max(2, (int)((w - 20.0) / 200.0));
						}
					}
					catch
					{
					}
				};
			};

			// 通用卡片外壳：图片 + 悬浮操作层 + 左下角名称条（带小背景）+ 右上角状态角标
			Border TileShell(Image image, Border overlay, Border nameBar, Border badge, bool light)
			{
				Grid cell = new Grid();
				image.Stretch = Stretch.UniformToFill;
				image.Height = 120.0;
				cell.Children.Add(image);
				overlay.Visibility = Visibility.Collapsed;
				overlay.HorizontalAlignment = HorizontalAlignment.Stretch;
				overlay.VerticalAlignment = VerticalAlignment.Stretch;
				cell.Children.Add(overlay);
				nameBar.HorizontalAlignment = HorizontalAlignment.Left;
				nameBar.VerticalAlignment = VerticalAlignment.Bottom;
				nameBar.Margin = new Thickness(6.0, 0.0, 0.0, 6.0);
				cell.Children.Add(nameBar);
				if (badge != null)
				{
					badge.HorizontalAlignment = HorizontalAlignment.Right;
					badge.VerticalAlignment = VerticalAlignment.Top;
					badge.Margin = new Thickness(0.0, 6.0, 6.0, 0.0);
					cell.Children.Add(badge);
				}
				Border card = new Border
				{
					CornerRadius = new CornerRadius(12.0),
					Background = Polish.Card(light),
					BorderBrush = Polish.Line(light),
					BorderThickness = new Thickness(1.0),
					Padding = new Thickness(6.0),
					Margin = new Thickness(0.0, 0.0, 8.0, 8.0),
					Child = cell
				};
				card.MouseEnter += delegate
				{
					overlay.Visibility = Visibility.Visible;
				};
				card.MouseLeave += delegate
				{
					overlay.Visibility = Visibility.Collapsed;
				};
				return card;
			}

			// 悬浮操作层：半透明深色底 + 白色小按钮
			Border OverlayWith(params (string Label, Action Act)[] buttons)
			{
				WrapPanel row = new WrapPanel
				{
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				};
				foreach ((string Label, Action Act) item in buttons)
				{
					System.Windows.Controls.Button btn = new System.Windows.Controls.Button
					{
						Content = item.Label,
						Padding = new Thickness(14.0, 6.0, 14.0, 6.0),
						Margin = new Thickness(4.0),
						Cursor = Cursors.Hand,
						Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
						Foreground = Brushes.Black,
						BorderThickness = new Thickness(0.0),
						FontSize = 12.0
					};
					btn.Click += delegate
					{
						item.Act();
					};
					row.Children.Add(btn);
				}
				return new Border
				{
					Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)),
					CornerRadius = new CornerRadius(10.0),
					Child = row
				};
			}

			// 左下角名称条：深色小背景 + 白字
			Border NameBar(string text)
			{
				return new Border
				{
					Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
					CornerRadius = new CornerRadius(6.0),
					Padding = new Thickness(6.0, 3.0, 6.0, 3.0),
					Child = new TextBlock
					{
						Text = text,
						Foreground = Brushes.White,
						FontSize = 11.0,
						TextTrimming = TextTrimming.CharacterEllipsis,
						MaxWidth = 150.0
					}
				};
			}

			// 右上角“使用中”角标
			Border InUseBadge(bool light)
			{
				return new Border
				{
					Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
					CornerRadius = new CornerRadius(6.0),
					Padding = new Thickness(6.0, 2.0, 6.0, 2.0),
					Child = new TextBlock
					{
						Text = "使用中",
						Foreground = Polish.Accent,
						FontSize = 10.0
					}
				};
			}

			// —— 本地 ——
			galleryPanel.Children.Add(new TextBlock
			{
				Text = "本地",
				Foreground = Polish.Ink(light),
				FontWeight = FontWeights.Bold,
				FontSize = 13.0,
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
			});
			List<string> entries = WallpaperLibrary.Entries();
			if (entries.Count == 0)
			{
				galleryPanel.Children.Add(new TextBlock
				{
					Text = "暂无本地壁纸，点击上方“选择图片导入”开始。",
					Foreground = Polish.Muted(light),
					FontSize = 12.0,
					Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
				});
			}
			else
			{
				UniformGrid localGrid = new UniformGrid
				{
					Columns = 3
				};
				attachAdaptive(localGrid);
				foreach (string file in entries)
				{
					Image image = MakeThumbImage(file);
					Border overlay = OverlayWith(
						("设为壁纸", delegate
						{
							if (o.Previous.Count == 0)
							{
								o.Previous = WallpaperCore.Capture();
							}
							o.Path = file;
							o.Source = WallpaperLibrary.IsOfficial(file) ? 1 : 0;
							o.Enabled = true;
							Apply();
							Rebuild();
							PostStatus("正在应用壁纸…");
							ApplyAsync(file, o.Style, "已应用：" + DisplayName(file), "壁纸设置失败，详见配置目录 error.log。");
						}),
						("删除", delegate
						{
							if (o.Path == file)
							{
								if (o.Previous.Count > 0)
								{
									PostStatus("正在还原系统壁纸…");
									RestoreAsync(new Dictionary<string, string>(o.Previous), "已删除并还原系统壁纸。", "已删除，但还原原壁纸失败。");
								}
								o.Previous.Clear();
								o.Path = null;
								Apply();
							}
							WallpaperLibrary.Delete(file);
							Rebuild();
						}));
					Border nameBar = NameBar(DisplayName(file));
					Border badge = (o.Path == file && o.Enabled) ? InUseBadge(light) : null;
					localGrid.Children.Add(TileShell(image, overlay, nameBar, badge, light));
				}
				galleryPanel.Children.Add(localGrid);
			}

			// —— 官方 ——
			galleryPanel.Children.Add(new TextBlock
			{
				Text = "官方",
				Foreground = Polish.Ink(light),
				FontWeight = FontWeights.Bold,
				FontSize = 13.0,
				Margin = new Thickness(0.0, 8.0, 0.0, 8.0)
			});
			galleryStatus = new TextBlock
			{
				Foreground = Polish.Muted(light),
				TextWrapping = TextWrapping.Wrap,
				FontSize = 12.0,
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
			};
			galleryPanel.Children.Add(galleryStatus);
			galleryPanel.Children.Add(FeatureUi.Button("刷新官方图库", async delegate
			{
				if (catalogBusy)
				{
					return;
				}
				catalogBusy = true;
				galleryStatus.Text = "正在连接 GitHub 图库…";
				try
				{
					catalog = await WallpaperCatalog.FetchAsync();
					o.CatalogFetchedUtc = DateTime.UtcNow.Ticks;
					Apply();
					Rebuild();
				}
				catch (Exception ex)
				{
					galleryStatus.Text = "图库加载失败：" + ex.Message;
				}
				finally
				{
					catalogBusy = false;
				}
			}));
			if (catalog != null && catalog.Count > 0)
			{
				galleryStatus.Text = "共 " + catalog.Count + " 张官方壁纸，悬浮后点击“下载并应用”直接设为桌面。";
				UniformGrid officialGrid = new UniformGrid
				{
					Columns = 3
				};
				attachAdaptive(officialGrid);
				foreach (WallpaperCatalog.Entry entry in catalog)
				{
					Image image = new Image
					{
						Stretch = Stretch.UniformToFill,
						Height = 120.0
					};
					string thumbUrl = WallpaperCatalog.ThumbUrl(entry);
					if (thumbUrl != null)
					{
						try
						{
							BitmapImage bi = new BitmapImage();
							bi.BeginInit();
							bi.UriSource = new Uri(thumbUrl);
							bi.DecodePixelWidth = 352;
							bi.CacheOption = BitmapCacheOption.OnDemand;
							bi.EndInit();
							image.Source = bi;
						}
						catch
						{
						}
					}
					Border overlay = OverlayWith(
						("下载并应用", delegate
						{
							DownloadAndApply(entry);
						}));
					Border nameBar = NameBar(entry.Name + " · " + (entry.Size / 1048576.0).ToString("0.0") + " MB");
					officialGrid.Children.Add(TileShell(image, overlay, nameBar, null, light));
				}
				galleryPanel.Children.Add(officialGrid);
			}
			else
			{
				galleryStatus.Text = "点击“刷新官方图库”加载内置壁纸清单。";
			}
			p.Children.Add(gallery);

			async void DownloadAndApply(WallpaperCatalog.Entry e)
			{
				galleryStatus.Text = "正在下载 " + e.Name + " …";
				string tmp = Path.Combine(Path.GetTempPath(), "gb-wallpaper-" + Guid.NewGuid().ToString("N") + Path.GetExtension(e.File));
				try
				{
					await WallpaperCatalog.DownloadAsync(e, tmp, delegate (long done, long total)
					{
						galleryStatus.Text = "下载中 " + e.Name + "：" + (done / 1048576.0).ToString("0.0") + " / " + (total / 1048576.0).ToString("0.0") + " MB";
					});
					string lib = WallpaperLibrary.Import(tmp, e.Name, official: true);
					if (lib == null)
					{
						galleryStatus.Text = "导入失败：壁纸格式不受支持或文件损坏。";
						return;
					}
					if (o.Previous.Count == 0)
					{
						o.Previous = WallpaperCore.Capture();
					}
					o.Path = lib;
					o.Source = 1;
					o.Enabled = true;
					Apply();
					Rebuild();
					galleryStatus.Text = "已下载：" + e.Name + "，正在应用…";
					PostStatus("正在应用壁纸…");
					ApplyAsync(lib, o.Style, "已应用：" + e.Name, "壁纸设置失败，详见配置目录 error.log。");
				}
				catch (Exception ex)
				{
					galleryStatus.Text = "下载失败（已尝试直连与镜像）：" + ex.Message;
				}
				finally
				{
					try
					{
						if (File.Exists(tmp))
						{
							File.Delete(tmp);
						}
					}
					catch
					{
					}
				}
			}
		}

		Render(d);
	}

	// 本地缩略图（文件存在则读 .thumbs 缓存）
	private static Image MakeThumbImage(string path)
	{
		Image image = new Image
		{
			Stretch = Stretch.UniformToFill
		};
		string thumb = WallpaperLibrary.Thumb(path);
		if (thumb != null)
		{
			try
			{
				using (FileStream fs = new FileStream(thumb, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					BitmapImage bi = new BitmapImage();
					bi.BeginInit();
					bi.CacheOption = BitmapCacheOption.OnLoad;
					bi.StreamSource = fs;
					bi.EndInit();
					bi.Freeze();
					image.Source = bi;
				}
			}
			catch
			{
			}
		}
		return image;
	}

	// 库内文件名格式：<sha256前12位>_<显示名>.<ext>，显示时去掉前缀
	private static string DisplayName(string path)
	{
		string name = Path.GetFileNameWithoutExtension(path);
		if (name.Length > 13 && name[12] == '_')
		{
			return name.Substring(13);
		}
		return name;
	}
}
