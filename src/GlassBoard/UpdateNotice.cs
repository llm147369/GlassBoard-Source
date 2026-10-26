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
		content.Children.Add(Card("项目已开源，更新方式如下", new string[]
		{
			"源码仓库：https://github.com/llm147369/GlassBoard-Source",
			"仓库根目录就是编译好的便携版：AI 软件拉取后直接双击根目录的 GlassBoard.exe 即可运行，无需下载发布包，也无需安装编译环境。",
			"更新时打开豆包（或其他 AI 软件）的单独对话页面，输入作者最新视频里的更新指令并附上仓库地址，让 AI 拉取仓库最新代码覆盖本地，运行根目录的新程序即完成升级。",
			"覆盖只会替换程序文件，不会影响你的配置数据（配置保存在 %LocalAppData%\\GlassBoard，与项目目录无关）。",
			"覆盖前建议先备份整个项目文件夹，避免自己改过的内容被覆盖丢失。"
		}, accent, cardBrush, lineBrush));
		content.Children.Add(Card("在线更新功能（即将上线）", new string[]
		{
			"后续版本会加入「在线更新」功能：自动检查新版本、一键下载更新，无需再手动拉取覆盖。",
			"在线更新上线后，会在这里和 GitHub 仓库说明中同步告知。"
		}, accent, cardBrush, lineBrush));
		TabItem item = new TabItem
		{
			Header = "重要提示",
			Content = scroll
		};
		tabs.Items.Insert(0, item);
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
