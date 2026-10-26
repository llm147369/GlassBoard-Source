using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GlassBoard;

// 设置窗口侧边栏「使用说明」页：
// 按功能模块编写详细用法，顶部搜索框输入关键词即时过滤相关小节并高亮命中文字。
public static class UsageGuide
{
	private sealed class Section
	{
		public string Title;
		public string[] Lines;
		public Border Card;
		public TextBlock TitleBlock;
		public TextBlock BodyBlock;
	}

	public static void Build(TabControl tabs)
	{
		Brush accent = Polish.Color("#416AA9");
		Brush cardBrush = new SolidColorBrush(Color.FromRgb(38, 41, 52));
		Brush lineBrush = new SolidColorBrush(Color.FromRgb(58, 62, 76));

		List<Section> sections = new List<Section>();
		ScrollViewer scroll = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};
		StackPanel content = new StackPanel
		{
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0)
		};
		scroll.Content = content;
		TextBlock noResult = new TextBlock
		{
			Text = "没有找到相关内容",
			FontSize = 14.0,
			Foreground = Brushes.LightGray,
			Visibility = Visibility.Collapsed,
			Margin = new Thickness(4.0, 18.0, 0.0, 0.0)
		};
		content.Children.Add(noResult);

		// 各模块的详细使用说明（标题 + 逐条用法）。
		(string, string[])[] data = new (string, string[])[]
		{
			("快速开始", new string[]
			{
				"双击 GlassBoard.exe 启动；推荐在「通用」页开启开机自启，登录 Windows 后自动运行。",
				"软件常驻系统托盘：右键托盘图标可打开面板、添加软件、进入设置或退出。",
				"首次启动若发布包内置推荐配置（DefaultConfig.json），会询问是否导入：选择「使用推荐配置」仅导入外观与布局，不覆盖你已固定的图标；导入后该文件自动改名，不会重复询问。",
				"三块核心界面：底部 Dock 栏、应用大面板、桌面小组件。下面按模块逐一说明用法。"
			}),
			("Dock 栏", new string[]
			{
				"单击图标启动软件；软件已运行时，再点同一图标会在其窗口之间轮换或最小化，方便多窗口软件切换。",
				"悬停图标可预览软件窗口列表，并显示纯文字提示。",
				"固定应用：从应用面板把图标拖到 Dock 松手即可固定；也可在面板右键入口选「固定到 Dock」，或在 Dock 右键 → 添加程序 / 文件 / 文件夹。",
				"右键图标菜单：打开、打开文件所在位置、文件属性、系统右键菜单（管理员运行等）、修改名称与图标、从 Dock 移除。",
				"在 Dock 上滚动鼠标滚轮：上滚回到常用软件，下滚查看正在运行的程序列表。",
				"系统入口：此电脑、设置、电源快捷菜单（睡眠/重启/关机）、Wi-Fi / 蓝牙快捷菜单、控制中心——可在「Dock DIY」页按需开关。",
				"运行列表入口显示正在运行的程序，点击即可切换窗口。",
				"最多固定 20 个入口；超出或屏幕放不下时，其余入口收进「更多」。",
				"自动隐藏：在「Dock DIY」开启后，鼠标靠近所在屏幕底边即可唤出；可调整隐藏延迟与底边触发范围；「弹簧弹出效果」也可直接右键 Dock 切换。"
			}),
			("应用面板", new string[]
			{
				"打开方式：默认快捷键 Ctrl+Alt+Space、托盘菜单「打开应用面板」、Dock 上的应用入口。",
				"顶部搜索框可直接搜索系统应用、文件、文件夹，边输入边出结果，回车启动。",
				"分组：新软件按类别自动分组（可在「面板与主题」关闭「新增软件自动识别分组」）；用「管理分组」新建/改名/删除分组，右键图标「移动到分组」手动归类；「自动分组未分组软件」一键整理。",
				"添加内容：点「添加软件」按钮或快捷键 Ctrl+Alt+A；把文件/文件夹直接拖进面板；开启「自动识别桌面新增图标」后，桌面新建的快捷方式会自动出现在面板（不会自动固定到 Dock）。",
				"创作工作区：把应用、素材文件夹和项目文件组成一组，一次点击同时打开一整套工具；托盘菜单也能直接打开已保存的工作区。",
				"外观方案：保存/切换整套外观配置，适合在不同场景间快速换肤。",
				"面板位置与尺寸可拖拽调整；「锁定大面板位置和尺寸」防止误拖；「点击外部自动收起」方便从桌面拖文件进来。"
			}),
			("控制中心", new string[]
			{
				"入口：Dock 上的控制中心图标，点击后在 Dock 上方展开，主题与强调色跟随当前设置。",
				"网络模块：列出可用 Wi-Fi，点击连接；连接过的网络免密直连。",
				"蓝牙模块：管理已配对设备。音量模块：拖动调节系统音量。亮度模块：部分显示器不支持。",
				"输出设备：切换默认播放设备；若系统不允许程序直接切换，会提示并引导到系统声音设置。",
				"各模块的显示开关在设置「控制中心」页调整。"
			}),
			("桌面组件", new string[]
			{
				"四种组件：时钟（时:分:秒，带闹钟）、天气（联网查询）、音乐、备忘录（自动保存、支持文本导出）。",
				"开关：设置「桌面组件」页的开关即时生效并自动保存；托盘菜单也能逐个开启。",
				"组件面板：组件可分配到面板 A/B/C/D；同一面板的组件融合成一块，不同面板则分离；点组件右上角 ••• 也可调整位置与外观。",
				"拖动组件面板改变位置；「锁定组件位置」防误拖；「组件始终置顶」让组件不被其他窗口遮住。",
				"闹钟：在时钟组件里设置，到点弹窗并播放系统提示音，可稍后 5 分钟再提醒；GlassBoard 需保持运行，时钟隐藏时闹钟仍然有效。",
				"组件外观：点「组件外观 DIY」调整颜色、无框模式、时钟样式、音乐按钮；透明度和玻璃材质也在这里设置。",
				"位置乱了可点「重置全部组件位置」；托盘菜单「恢复并开启全部组件」可找回所有组件。"
			}),
			("音乐组件", new string[]
			{
				"自动识别：支持网易云音乐、QQ 音乐、酷狗、酷我、汽水音乐等——前提是音乐软件开启了「信息同步 / 系统媒体信息」，否则组件拿不到曲目、封面和播放状态。",
				"操作：播放/暂停、上一首/下一首、封面显示；进度条拖动依赖系统媒体信息，部分播放器不公开时进度不可用。",
				"本地音乐模式：组件 ••• 菜单 → 本地音乐模式 → 选择音乐文件（可多选批量添加），组件直接播放；音量在「桌面组件」页调节。",
				"播放列表：••• 菜单 → 播放列表，列表持久保存，支持随机与循环播放。",
				"识别不到时：先开启音乐软件的信息同步；软件未开始播放时组件会提示「开始播放后即可控制」；浏览器、视频网站等非音乐来源会自动忽略。"
			}),
			("快捷键", new string[]
			{
				"默认组合：Ctrl+Alt+Space 展开/收起面板；Ctrl+Alt+T 隐藏/显示任务栏；Ctrl+Alt+E 隐藏/显示桌面图标；Ctrl+Alt+A 添加软件；Ctrl+Alt+S 外观设置；Ctrl+Alt+R 恢复任务栏与图标；Ctrl+Alt+H Windows 任务栏自动隐藏。被占用时程序会自动换用备选按键并在托盘提示。",
				"修改：设置「快捷键与开关」页，点对应行的「录制」，直接按下想要的组合键即可。",
				"每行下方会显示注册状态：已注册 / 未注册（被其他程序占用时请换组合）/ 已关闭。",
				"改动后点窗口右下角「保存快捷键并完成」才生效；「恢复默认快捷键」一键还原。",
				"任务栏与桌面图标的隐藏状态只在本次运行期间维持，正常退出后恢复启动前状态。"
			}),
			("个性化外观", new string[]
			{
				"「面板与主题」统一 Dock、大面板、小组件三处外观：材质风格 4 种（标准毛玻璃/轻盈渐变/纯净实色/明亮渐变）、玻璃色调 7 种（可选自定义 #RRGGBB）、透明度、玻璃模糊、边缘高光、圆角。",
				"玻璃拟态：勾选「开启玻璃拟态」一键应用磨砂模糊+高光描边；下方三个开关可单独控制面板 / Dock / 组件。Win10、Win11 通用；动态壁纸（如小红车）下背景为最近快照。",
				"面板：尺寸三档（中号/大号/全屏）、透明度、锁定位置、点击外部收起、隐藏搜索栏、隐藏分组栏、快捷方式角标、系统入口图标风格。",
				"「Dock DIY」：一键风格预设（紧凑/均衡/宽松）、图标尺寸 32–64px、间距、圆角、透明度、悬停放大、显示图标名称、此电脑/设置/电源/Wi-Fi 蓝牙/运行列表/控制中心等系统入口开关、强调色 6 种、分隔线、置顶、运行状态标记。",
				"Dock 位置：靠左/居中/靠右、左右微调、距工作区底边距离；「重置 Dock 位置」一键归位；「锁定 Dock 位置」防误拖。",
				"所有调整即时预览，关闭设置窗口即保留；改错了可手动改回或使用「恢复默认」类按钮。"
			}),
			("通用与维护", new string[]
			{
				"开机自启：两种优先级——登录后立即启动（最快）或桌面就绪后启动（稍晚更稳）；移动程序位置后，请关闭再开启该开关以更新路径。",
				"自动识别桌面新增图标：开启后桌面新建的快捷方式自动出现在面板。",
				"游戏模式：隐藏 Dock、大面板和小组件，避免游戏误触；可开启「检测全屏应用时自动进入」，退出全屏自动恢复；右键托盘可退出或暂停自动模式。",
				"找回旧版入口与分组：升级后丢失的入口可从这里恢复。",
				"完整备份与恢复：导出布局、分组、快捷键、组件、便签、外观方案与自定义图标；恢复时用备份覆盖当前设置（软件本体与音乐文件不在备份内）。",
				"配置数据保存在 %LocalAppData%\\GlassBoard（layout.json 等）；删除该目录等于恢复出厂设置。",
				"设置窗口入口：Dock 的设置图标、托盘菜单「外观与快捷键」或快捷键 Ctrl+Alt+S。"
			}),
			("常见问题", new string[]
			{
				"音乐识别不到：先在音乐软件里开启「信息同步 / 系统媒体信息」；确认正在播放；浏览器/视频站来源会被忽略；源码构建版需保证 lib\\NPSMLib.dll 与主程序在同一目录。",
				"快捷键没反应：看设置页注册状态，被占用时换组合，或点「重新注册当前快捷键」。",
				"Dock 不见了：检查是否开启了游戏模式、自动隐藏触发范围是否太小，或用托盘菜单「恢复并开启全部组件」。",
				"任务栏两种隐藏：「隐藏任务栏」= 彻底隐藏（悬停不弹出）；「自动隐藏」= Windows 原生自动隐藏（悬停底部弹出）。彻底隐藏与 Wallpaper Engine 等动态壁纸可能冲突白屏，如遇白屏改用自动隐藏。",
				"面板图标错乱：右键图标「移动到分组」整理；面板位置尺寸异常时用「重置面板位置和尺寸」。",
				"开机自启失效：程序被移动过，设置里关闭再开启开机自启即可。",
				"想彻底重置：删除 %LocalAppData%\\GlassBoard 目录（建议先导出完整备份）。"
			})
		};

		// 小节卡片：标题 + 逐条正文；搜索时整卡过滤、命中文字高亮。
		foreach ((string title, string[] lines) in data)
		{
			Border card = new Border
			{
				Background = cardBrush,
				BorderBrush = lineBrush,
				BorderThickness = new Thickness(1.0),
				CornerRadius = new CornerRadius(10.0),
				Padding = new Thickness(14.0, 12.0, 14.0, 12.0),
				Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
			};
			StackPanel inner = new StackPanel();
			card.Child = inner;
			TextBlock titleBlock = new TextBlock
			{
				FontSize = 15.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = Brushes.White,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 0.0, 0.0, 7.0)
			};
			inner.Children.Add(titleBlock);
			TextBlock body = new TextBlock
			{
				FontSize = 12.5,
				Foreground = Brushes.LightGray,
				TextWrapping = TextWrapping.Wrap,
				LineHeight = 21.0
			};
			inner.Children.Add(body);
			FillLines(titleBlock, new string[1] { title }, "");
			FillLines(body, lines, "");
			content.Children.Add(card);
			sections.Add(new Section
			{
				Title = title,
				Lines = lines,
				Card = card,
				TitleBlock = titleBlock,
				BodyBlock = body
			});
		}

		// 顶部搜索区：输入框 + 占位提示 + 结果统计。
		Grid root = new Grid
		{
			Margin = new Thickness(10.0, 8.0, 0.0, 8.0)
		};
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12.0) });
		root.RowDefinitions.Add(new RowDefinition());
		Border searchShell = new Border
		{
			Background = cardBrush,
			BorderBrush = lineBrush,
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(8.0)
		};
		Grid searchGrid = new Grid();
		searchShell.Child = searchGrid;
		TextBox searchBox = new TextBox
		{
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Foreground = Brushes.White,
			CaretBrush = Brushes.White,
			FontSize = 14.0,
			Padding = new Thickness(12.0, 9.0, 12.0, 9.0),
			MinHeight = 40.0,
			VerticalContentAlignment = VerticalAlignment.Center
		};
		searchGrid.Children.Add(searchBox);
		TextBlock hint = new TextBlock
		{
			Text = "搜索功能、设置项、常见问题…",
			Foreground = Brushes.LightGray,
			FontSize = 14.0,
			Margin = new Thickness(14.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			IsHitTestVisible = false
		};
		searchGrid.Children.Add(hint);
		root.Children.Add(searchShell);
		TextBlock status = new TextBlock
		{
			FontSize = 11.5,
			Foreground = Brushes.LightGray,
			Margin = new Thickness(4.0, 7.0, 0.0, 0.0)
		};
		Grid.SetRow(status, 1);
		root.Children.Add(status);
		Grid.SetRow(scroll, 3);
		root.Children.Add(scroll);

		void ApplyFilter()
		{
			string q = searchBox.Text.Trim();
			hint.Visibility = (searchBox.Text.Length == 0) ? Visibility.Visible : Visibility.Collapsed;
			foreach (Section s in sections)
			{
				FillLines(s.TitleBlock, new string[1] { s.Title }, q);
				FillLines(s.BodyBlock, s.Lines, q);
			}
			if (q.Length == 0)
			{
				foreach (Section s in sections)
				{
					s.Card.Visibility = Visibility.Visible;
				}
				noResult.Visibility = Visibility.Collapsed;
				status.Text = "共 " + sections.Count + " 节内容 · 输入关键词快速查找，命中处会高亮";
				return;
			}
			int visible = 0;
			foreach (Section s in sections)
			{
				bool hit = s.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || s.Lines.Any((string l) => l.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
				s.Card.Visibility = (hit ? Visibility.Visible : Visibility.Collapsed);
				if (hit)
				{
					visible++;
				}
			}
			noResult.Visibility = ((visible == 0) ? Visibility.Visible : Visibility.Collapsed);
			status.Text = ((visible == 0) ? ("没有找到与「" + q + "」相关的内容") : ("找到 " + visible + " 节相关内容"));
		}

		searchBox.TextChanged += (object s, TextChangedEventArgs e) =>
		{
			ApplyFilter();
		};
		ApplyFilter();
		TabItem item = new TabItem
		{
			Header = "使用说明",
			Content = root
		};
		tabs.Items.Add(item);
	}

	// 用换行元素逐行填充正文；命中关键词的片段以强调色背景高亮。
	private static void FillLines(TextBlock block, string[] lines, string query)
	{
		block.Inlines.Clear();
		for (int i = 0; i < lines.Length; i++)
		{
			if (i > 0)
			{
				block.Inlines.Add(new LineBreak());
			}
			string line = lines[i];
			if (string.IsNullOrEmpty(line))
			{
				continue;
			}
			if (string.IsNullOrEmpty(query))
			{
				block.Inlines.Add(new Run(line));
				continue;
			}
			int idx = 0;
			while (true)
			{
				int hit = line.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase);
				if (hit < 0)
				{
					block.Inlines.Add(new Run(line.Substring(idx)));
					break;
				}
				if (hit > idx)
				{
					block.Inlines.Add(new Run(line.Substring(idx, hit - idx)));
				}
				Run mark = new Run(line.Substring(hit, query.Length))
				{
					Background = Polish.Color("#416AA9"),
					Foreground = Brushes.White
				};
				block.Inlines.Add(mark);
				idx = hit + query.Length;
				if (idx >= line.Length)
				{
					break;
				}
			}
		}
	}
}
