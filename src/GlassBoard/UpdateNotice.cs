using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassBoard;

// 设置侧边栏「重要提示」页（置顶）：
// 说明开源仓库的更新方式（可用 AI 软件拉取覆盖）与后续在线更新功能预告。
public static class UpdateNotice
{
	public static void Build(TabControl tabs)
	{
		Brush accent = Polish.Color("#416AA9");
		Brush cardBrush = new SolidColorBrush(Color.FromRgb(38, 41, 52));
		Brush lineBrush = new SolidColorBrush(Color.FromRgb(58, 62, 76));

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
		content.Children.Add(AlertCard("遇到小 Bug 或异常卡顿？可以让 AI 修复", new string[]
		{
			"方法一（推荐）：把本程序交给 AI 软件（豆包、Codex、Cursor、Claude Code 等）排查——本程序完全开源，AI 可以直接阅读源码定位问题并修改修复。",
			"方法二：把报错信息直接反馈给作者（GitHub Issues 或邮件），附上日志段落即可。",
			"报错位置：右键任务栏托盘里的本程序图标 → 打开配置文件夹，其中 error.log 是错误详情、session.log 是运行日志、failures.log 是卡顿/异常检测记录。",
			"反馈时把 error.log 最新一段（或 failures.log 里的卡顿记录）连同问题现象一起发出，即可快速定位。"
		}, accent, cardBrush, lineBrush));
		content.Children.Add(Card("项目已开源，更新方式如下", new string[]
		{
			"源码仓库：https://github.com/llm147369/GlassBoard-Source",
			"仓库根目录就是编译好的便携版：AI 软件拉取后直接双击根目录的 GlassBoard.exe 即可运行，无需下载发布包，也无需安装编译环境。",
			"更新时用 AI 软件（如豆包、Codex、Cursor、Claude Code 等）拉取仓库最新代码覆盖本地，运行根目录的新程序即完成升级。",
			"覆盖只会替换程序文件，不会影响你的配置数据（配置保存在 %LocalAppData%\\GlassBoard，与项目目录无关）。",
			"覆盖前建议先备份整个项目文件夹，避免自己改过的内容被覆盖丢失。"
		}, accent, cardBrush, lineBrush));
		content.Children.Add(Card("在线更新已上线", new string[]
		{
			"程序每次启动会自动检查新版本（每天最多一次），发现新版本会在托盘气泡提醒。",
			"更新入口：设置 → 通用 → 检查更新，一键下载并自动重启升级；托盘菜单里也有「检查更新」。",
			"更新只替换程序文件，配置与自定义图标不受影响。"
		}, accent, cardBrush, lineBrush));
		TabItem item = new TabItem
		{
			Header = "重要提示",
			Content = scroll
		};
		tabs.Items.Insert(0, item);
	}

	// 醒目告警卡：琥珀色边框 + 高亮标题 + 白色正文，用于 bug 反馈指引。
	private static FrameworkElement AlertCard(string title, string[] lines, Brush accent, Brush cardBrush, Brush lineBrush)
	{
		Color alertColor = Color.FromRgb(byte.MaxValue, 168, 58);
		Brush alertBrush = new SolidColorBrush(alertColor);
		Border card = new Border
		{
			Background = cardBrush,
			BorderBrush = alertBrush,
			BorderThickness = new Thickness(1.2),
			CornerRadius = new CornerRadius(10.0),
			Padding = new Thickness(14.0, 12.0, 14.0, 13.0),
			Margin = new Thickness(4.0, 6.0, 4.0, 6.0)
		};
		StackPanel panel = new StackPanel();
		card.Child = panel;
		panel.Children.Add(new TextBlock
		{
			Text = title,
			FontSize = 16.0,
			FontWeight = FontWeights.Bold,
			Foreground = alertBrush,
			TextWrapping = TextWrapping.Wrap
		});
		TextBlock body = new TextBlock
		{
			FontSize = 12.5,
			Foreground = Brushes.White,
			LineHeight = 21.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 7.0, 0.0, 0.0)
		};
		for (int i = 0; i < lines.Length; i++)
		{
			if (i > 0)
			{
				body.Inlines.Add(new System.Windows.Documents.LineBreak());
			}
			body.Inlines.Add(new System.Windows.Documents.Run(lines[i]));
		}
		panel.Children.Add(body);
		Border divider = new Border
		{
			Height = 1.0,
			Background = lineBrush,
			Margin = new Thickness(0.0, 11.0, 0.0, 0.0)
		};
		panel.Children.Add(divider);
		return card;
	}

	private static FrameworkElement Card(string title, string[] lines, Brush accent, Brush cardBrush, Brush lineBrush)
	{
		Border card = new Border
		{
			Background = cardBrush,
			CornerRadius = new CornerRadius(10.0),
			Padding = new Thickness(14.0, 12.0, 14.0, 13.0),
			Margin = new Thickness(4.0, 6.0, 4.0, 6.0)
		};
		StackPanel panel = new StackPanel();
		card.Child = panel;
		panel.Children.Add(new TextBlock
		{
			Text = title,
			FontSize = 15.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = accent,
			TextWrapping = TextWrapping.Wrap
		});
		TextBlock body = new TextBlock
		{
			FontSize = 12.5,
			Foreground = Brushes.LightGray,
			LineHeight = 21.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 7.0, 0.0, 0.0)
		};
		for (int i = 0; i < lines.Length; i++)
		{
			if (i > 0)
			{
				body.Inlines.Add(new System.Windows.Documents.LineBreak());
			}
			body.Inlines.Add(new System.Windows.Documents.Run(lines[i]));
		}
		panel.Children.Add(body);
		Border divider = new Border
		{
			Height = 1.0,
			Background = lineBrush,
			Margin = new Thickness(0.0, 11.0, 0.0, 0.0)
		};
		panel.Children.Add(divider);
		return card;
	}
}
