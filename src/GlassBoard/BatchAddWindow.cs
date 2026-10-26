using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace GlassBoard;

public class BatchAddWindow : Window
{
	private readonly Controller C;

	private readonly string targetGroup;

	private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

	private readonly List<AddCandidate> candidates = new List<AddCandidate>();

	// 搜索过滤后的可见候选（未过滤时与 candidates 相同）
	private readonly List<AddCandidate> visible = new List<AddCandidate>();

	private System.Windows.Controls.ListBox list;

	private TextBlock status;

	private System.Windows.Controls.Button add;

	private WrapPanel sources;

	private System.Windows.Controls.CheckBox recursive;

	private System.Windows.Controls.CheckBox launchOnly;

	private System.Windows.Controls.TextBox filter;

	private bool busy;

	private bool closed;

	private bool changing;

	public BatchAddWindow(Controller controller)
	{
		C = controller;
		targetGroup = (C.Board.IsVisible ? C.Board.CurrentGroup : "全部");
		Title = "GlassBoard · 添加到 " + targetGroup;
		Width = Math.Min(800.0, SystemParameters.WorkArea.Width - 40.0);
		Height = Math.Min(670.0, SystemParameters.WorkArea.Height - 50.0);
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Owner = (C.Board.IsVisible ? ((GlassWindow)C.Board) : ((GlassWindow)C.Dock));
		Topmost = true;
		Background = new SolidColorBrush(Color.FromRgb(32, 34, 42));
		Foreground = Brushes.White;
		Grid grid = (Grid)(Content = new Grid
		{
			Margin = new Thickness(24.0)
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition());
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		StackPanel stackPanel = new StackPanel();
		grid.Children.Add(stackPanel);
		stackPanel.Children.Add(new TextBlock
		{
			Text = "一次加入应用、文件与文件夹",
			FontSize = 22.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = "添加位置：" + targetGroup + " · 选择来源并勾选，已有入口会自动去重。也可以直接把文件或文件夹拖进这个窗口。",
			Foreground = Brushes.LightGray,
			Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
		});
		sources = new WrapPanel();
		stackPanel.Children.Add(sources);
		sources.Children.Add(MakeButton("选择多个文件", PickFiles));
		sources.Children.Add(MakeButton("添加文件夹入口", () =>
		{
			string text = FeatureUi.PickFolder(this, "添加文件夹本身作为入口（不扫描里面的文件）");
			if (text != null)
			{
				Merge(new string[1] { text });
			}
		}));
		sources.Children.Add(MakeButton("扫描文件夹", PickFolder));
		sources.Children.Add(MakeButton("读取桌面", () =>
		{
			Scan(new string[2]
			{
				Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
				Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
			}, recurse: false);
		}));
		sources.Children.Add(MakeButton("读取开始菜单", () =>
		{
			Scan(new string[2]
			{
				Environment.GetFolderPath(Environment.SpecialFolder.Programs),
				Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
			}, recurse: true);
		}));
		recursive = new System.Windows.Controls.CheckBox
		{
			Content = "扫描文件夹时包含子文件夹",
			IsChecked = true,
			Foreground = Brushes.White,
			Margin = new Thickness(0.0, 8.0, 0.0, 12.0)
		};
		stackPanel.Children.Add(recursive);
		launchOnly = new System.Windows.Controls.CheckBox
		{
			Content = "只扫描启动入口（过滤卸载、更新、辅助进程和网页书签）",
			IsChecked = true,
			Foreground = Brushes.White,
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
		};
		stackPanel.Children.Add(launchOnly);
		filter = new System.Windows.Controls.TextBox
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
			Padding = new Thickness(8.0, 6.0, 8.0, 6.0),
			Background = new SolidColorBrush(Color.FromRgb(25, 27, 34)),
			Foreground = Brushes.White,
			BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 76)),
			ToolTip = "输入关键词过滤列表（按名称或路径）"
		};
		filter.TextChanged += (object s, TextChangedEventArgs e) =>
		{
			ApplyFilter();
		};
		stackPanel.Children.Add(filter);
		list = new System.Windows.Controls.ListBox
		{
			Background = new SolidColorBrush(Color.FromRgb(25, 27, 34)),
			BorderThickness = new Thickness(0.0),
			Foreground = Brushes.White,
			Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
		};
		VirtualizingPanel.SetIsVirtualizing(list, value: true);
		VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
		ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
		Style itemContainerStyle = new Style(typeof(ListBoxItem))
		{
			Setters = { (SetterBase)new Setter(System.Windows.Controls.Control.HorizontalContentAlignmentProperty, System.Windows.HorizontalAlignment.Stretch) }
		};
		list.ItemContainerStyle = itemContainerStyle;
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.CheckBox));
		frameworkElementFactory.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("Selected")
		{
			Mode = BindingMode.TwoWay
		});
		frameworkElementFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(6.0));
		frameworkElementFactory.SetValue(System.Windows.Controls.Control.ForegroundProperty, Brushes.White);
		FrameworkElementFactory frameworkElementFactory2 = new FrameworkElementFactory(typeof(StackPanel));
		FrameworkElementFactory frameworkElementFactory3 = new FrameworkElementFactory(typeof(TextBlock));
		frameworkElementFactory3.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
		frameworkElementFactory3.SetValue(TextBlock.FontSizeProperty, 14.0);
		frameworkElementFactory2.AppendChild(frameworkElementFactory3);
		FrameworkElementFactory frameworkElementFactory4 = new FrameworkElementFactory(typeof(TextBlock));
		frameworkElementFactory4.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Path"));
		frameworkElementFactory4.SetValue(TextBlock.FontSizeProperty, 11.0);
		frameworkElementFactory4.SetValue(TextBlock.ForegroundProperty, Brushes.LightGray);
		frameworkElementFactory4.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
		frameworkElementFactory2.AppendChild(frameworkElementFactory4);
		frameworkElementFactory.AppendChild(frameworkElementFactory2);
		list.ItemTemplate = new DataTemplate
		{
			VisualTree = frameworkElementFactory
		};
		Grid.SetRow(list, 1);
		grid.Children.Add(list);
		StackPanel stackPanel2 = new StackPanel();
		Grid.SetRow(stackPanel2, 2);
		grid.Children.Add(stackPanel2);
		status = new TextBlock
		{
			Text = "请选择文件，或读取桌面 / 开始菜单。",
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
			Foreground = Brushes.LightGray
		};
		stackPanel2.Children.Add(status);
		WrapPanel wrapPanel = new WrapPanel();
		stackPanel2.Children.Add(wrapPanel);
		wrapPanel.Children.Add(MakeButton("全选", () =>
		{
			SelectAll(value: true);
		}));
		wrapPanel.Children.Add(MakeButton("取消全选", () =>
		{
			SelectAll(value: false);
		}));
		add = MakeButton("添加已选（0）", () =>
		{
			string[] paths = (from x in candidates
				where x.Selected
				select x.Path).ToArray();
			int num = C.Prefs.Apps.Count((Entry a) => FeatureRules.InGroup(a, targetGroup));
			C.Add(paths, manual: true, targetGroup);
			C.Board.ClearSearch();
			int added = C.Prefs.Apps.Count((Entry a) => FeatureRules.InGroup(a, targetGroup)) - num;
			C.Board.SetStatus("已添加 " + added + " 个入口");
			// 已添加的从列表移除，窗口保留，可继续添加其他来源
			candidates.RemoveAll((AddCandidate c) => c.Selected);
			ApplyFilter();
			status.Text = "已添加 " + added + " 个入口，可继续选择其他文件或文件夹。";
		});
		add.IsEnabled = false;
		wrapPanel.Children.Add(add);
		wrapPanel.Children.Add(MakeButton("关闭", () =>
		{
			Close();
		}));
		// 拖拽支持：文件 → 候选列表；文件夹 → 按「包含子文件夹」设置扫描
		AllowDrop = true;
		DragOver += (object s, System.Windows.DragEventArgs e) =>
		{
			e.Effects = (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None);
			e.Handled = true;
		};
		Drop += (object s, System.Windows.DragEventArgs e) =>
		{
			if (busy || !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
			{
				return;
			}
			string[] paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
			if (paths == null || paths.Length == 0)
			{
				return;
			}
			List<string> files = new List<string>();
			List<string> dirs = new List<string>();
			foreach (string path in paths)
			{
				if (Directory.Exists(path))
				{
					dirs.Add(path);
				}
				else if (File.Exists(path))
				{
					files.Add(path);
				}
			}
			if (files.Count > 0)
			{
				Merge(files);
			}
			if (dirs.Count > 0)
			{
				Scan(dirs.ToArray(), recursive.IsChecked == true);
			}
		};
		Closed += (object s, EventArgs e) =>
		{
			closed = true;
			cancellation.Cancel();
		};
	}

	private static System.Windows.Controls.Button MakeButton(string title, Action action)
	{
		System.Windows.Controls.Button button = new System.Windows.Controls.Button();
		button.Content = title;
		button.Padding = new Thickness(12.0, 7.0, 12.0, 7.0);
		button.Margin = new Thickness(0.0, 0.0, 8.0, 4.0);
		button.Click += (object s, RoutedEventArgs e) =>
		{
			action();
		};
		return button;
	}

	private void SelectAll(bool value)
	{
		if (busy)
		{
			return;
		}
		changing = true;
		foreach (AddCandidate candidate in visible)
		{
			candidate.Selected = value;
		}
		changing = false;
		UpdateCount();
	}

	// 按搜索框内容过滤可见列表（名称或路径包含关键词，忽略大小写）
	private void ApplyFilter()
	{
		string query = filter?.Text ?? "";
		visible.Clear();
		if (string.IsNullOrWhiteSpace(query))
		{
			visible.AddRange(candidates);
		}
		else
		{
			visible.AddRange(candidates.Where((AddCandidate c) => c.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || c.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
		}
		list.ItemsSource = null;
		list.ItemsSource = visible;
		UpdateCount();
	}

	private void UpdateCount()
	{
		int num = visible.Count((AddCandidate x) => x.Selected);
		add.Content = "添加已选（" + num + "）";
		add.IsEnabled = !busy && num > 0;
		if (!busy)
		{
			status.Text = "共 " + visible.Count + " 个入口 · 已选 " + num + " 个" + (string.IsNullOrWhiteSpace(filter?.Text) ? "" : "（已按关键词过滤）");
		}
	}

	private void Merge(IEnumerable<string> paths)
	{
		HashSet<string> hashSet = new HashSet<string>(candidates.Select((AddCandidate c) => c.Path), StringComparer.OrdinalIgnoreCase);
		foreach (string item in EntryRules.Candidates(paths, C.Prefs.Apps.Where((Entry a) => targetGroup == "全部" || FeatureRules.InGroup(a, targetGroup))))
		{
			if (!hashSet.Add(item))
			{
				continue;
			}
			AddCandidate addCandidate = new AddCandidate
			{
				Path = item
			};
			addCandidate.PropertyChanged += (object s, PropertyChangedEventArgs e) =>
			{
				if (!changing)
				{
					UpdateCount();
				}
			};
			candidates.Add(addCandidate);
		}
		candidates.Sort((AddCandidate a, AddCandidate b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
		ApplyFilter();
	}

	private void PickFiles()
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "按 Ctrl / Shift 多选软件、快捷方式或文件",
			Multiselect = true,
			Filter = "应用、文档与表格|*.exe;*.lnk;*.appref-ms;*.url;*.cmd;*.bat;*.doc;*.docx;*.docm;*.xls;*.xlsx;*.xlsm;*.csv;*.ppt;*.pptx;*.pptm;*.wps;*.et;*.dps;*.xlsb;*.pdf;*.txt;*.rtf;*.md|所有文件|*.*"
		};
		if (openFileDialog.ShowDialog(this) == true)
		{
			if (openFileDialog.FileNames.Any(DocumentRules.Supported))
			{
				launchOnly.IsChecked = false;
			}
			Merge(openFileDialog.FileNames);
		}
	}

	private void PickFolder()
	{
		using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog
		{
			Description = "选择要扫描的软件或快捷方式文件夹",
			ShowNewFolderButton = false
		};
		NativeWindow nativeWindow = new NativeWindow();
		nativeWindow.AssignHandle(new WindowInteropHelper(this).Handle);
		try
		{
			if (folderBrowserDialog.ShowDialog(nativeWindow) == System.Windows.Forms.DialogResult.OK)
			{
				Scan(new string[1] { folderBrowserDialog.SelectedPath }, recursive.IsChecked == true);
			}
		}
		finally
		{
			nativeWindow.ReleaseHandle();
		}
	}

	private async void Scan(string[] roots, bool recurse)
	{
		if (busy)
		{
			return;
		}
		busy = true;
		sources.IsEnabled = false;
		recursive.IsEnabled = false;
		add.IsEnabled = false;
		list.IsEnabled = false;
		status.Text = "正在扫描，可关闭窗口取消…";
		try
		{
			bool only = launchOnly.IsChecked == true;
			ScanResult scanResult = await Task.Run(() =>
			{
				ScanResult scanResult2 = ImportScanner.Scan(roots, recurse, cancellation.Token, 2500, 10000, only);
				if (only)
				{
					scanResult2.Paths = scanResult2.Paths.Where((string path) =>
					{
						cancellation.Token.ThrowIfCancellationRequested();
						return LaunchInfoService.Primary(LaunchInfoService.Read(path));
					}).ToList();
				}
				return scanResult2;
			}, cancellation.Token);
			if (!closed)
			{
				busy = false;
				Merge(scanResult.Paths);
				if (scanResult.Skipped > 0)
				{
					TextBlock textBlock = status;
					textBlock.Text = textBlock.Text + " · 跳过 " + scanResult.Skipped + " 个无法读取的目录";
				}
				if (scanResult.Limited)
				{
					status.Text += " · 达到扫描上限，请分文件夹添加";
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			if (!closed)
			{
				Store.Log(ex2);
				status.Text = "扫描失败：" + ex2.Message;
			}
		}
		finally
		{
			busy = false;
			if (!closed)
			{
				sources.IsEnabled = true;
				recursive.IsEnabled = true;
				list.IsEnabled = true;
				add.IsEnabled = candidates.Any((AddCandidate x) => x.Selected);
			}
		}
	}
}
