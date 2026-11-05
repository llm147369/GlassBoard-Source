using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GlassBoard;

// 设置页里的灵动岛实时预览：不创建真实窗口，直接用 IslandChrome 渲染同一套
// 外观（材质/圆角/边框/阴影/字体/图标尺寸），支持收起态与展开态两种视图。
public sealed class IslandPreview : Border
{
	private readonly Grid host = new Grid();
	private readonly bool expanded;
	private readonly Controller C;

	public IslandPreview(Controller c, bool expandedView)
	{
		C = c;
		expanded = expandedView;
		Child = host;
		Margin = new Thickness(0, 4, 0, 12);
		HorizontalAlignment = HorizontalAlignment.Stretch;
		VerticalAlignment = VerticalAlignment.Top;
	}

	public void Render(IslandOptions o)
	{
		if (o == null)
		{
			o = new IslandOptions();
		}
		bool light = IslandChrome.EffectiveLight(o, C != null ? C.Prefs.Light : true);
		IslandChrome.Apply(this, o, light);
		// 顶部吸附模式：与真实窗口一致，顶角为 0、底角为设置圆角，且完全无边框线。
		if (o.TopGap <= 0.5)
		{
			CornerRadius = new CornerRadius(0, 0, o.CornerRadius, o.CornerRadius);
			BorderThickness = new Thickness(0);
		}
		else
		{
			Clip = null;
		}
		// 文字颜色与真实窗口一致：跟随系统明暗（深色→白字，浅色→黑字）。
		bool textLight = C != null ? C.Prefs.Light : true;
		Brush text = IslandChrome.TextBrush(textLight);
		Brush muted = IslandChrome.MutedTextBrush(textLight);
		Brush chip = new SolidColorBrush(textLight ? Color.FromArgb(26, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255));
		host.Children.Clear();
		host.Children.Add(expanded ? BuildExpanded(o, light, text, muted, chip) : BuildCollapsed(o, light, text, muted, chip));
	}

	private FrameworkElement BuildCollapsed(IslandOptions o, bool light, Brush text, Brush muted, Brush chip)
	{
		bool snap = o.TopGap <= 0.5;
		double viewH = snap ? Math.Max(24, o.CollapsedHeight - 14.0) : Math.Max(24, o.CollapsedHeight);
		DockPanel stack = new DockPanel
		{
			LastChildFill = true,
			VerticalAlignment = VerticalAlignment.Center,
			Height = viewH,
			MaxWidth = o.CollapsedWidth
		};
		double iconSize = o.IconSize;
		double gap = o.ItemGap;

		// 与真实窗口一致：收起布局 = 时间/日期（左）+ 天气（右）+ 实时刷新率（中填充）。
		StackPanel timeStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		timeStack.Children.Add(new TextBlock { Text = DateTime.Now.ToString("HH:mm"), FontSize = o.FontSize + 1.0, FontWeight = FontWeights.SemiBold, Foreground = text });
		timeStack.Children.Add(new TextBlock { Text = DateTime.Now.ToString("M月d日 ddd"), FontSize = Math.Max(10.0, o.FontSize - 3.0), Foreground = muted, Margin = new Thickness(0, 1, 0, 0) });
		DockPanel.SetDock(timeStack, Dock.Left);
		stack.Children.Add(timeStack);

		WeatherSnapshot ws = null;
		try
		{
			ws = C?.Weather?.Snapshot;
		}
		catch
		{
		}
		if (ws != null && ws.Current != null)
		{
			string desc = WeatherPresentation.Description(ws.Current.Code, ws.Current.Description);
			TextBlock wt = new TextBlock { Text = ws.Current.Temperature.ToString("0") + "° " + desc, FontSize = Math.Max(10.0, o.FontSize - 1.5), Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(gap, 0, 0, 0), MaxWidth = 132, TextTrimming = TextTrimming.CharacterEllipsis };
			DockPanel.SetDock(wt, Dock.Right);
			stack.Children.Add(wt);
		}

		// 中部填充：动态建议轮播（与真实窗口同源：天气 + 刷新率 + 今日随手记）
		NoteBook book = null;
		try
		{
			book = C.Notes?.Book;
		}
		catch
		{
		}
		string[] pool = IslandSuggestions.Build(ws, IslandWindow.CurrentRefreshRate(), book, 0);
		string first = (pool.Length == 0) ? "" : pool[0];
		TextBlock rt = new TextBlock { Text = first, FontSize = Math.Max(10.0, o.FontSize - 1.5), Foreground = muted, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
		stack.Children.Add(rt);
		stack.LastChildFill = true;
		return stack;
	}

	private static FrameworkElement Chip(FrameworkElement icon, string text, IslandOptions o, Brush fg, double gap)
	{
		StackPanel chip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(gap, 0, 0, 0) };
		chip.Children.Add(icon);
		chip.Children.Add(new TextBlock
		{
			Text = text,
			FontSize = Math.Max(10.5, o.FontSize - 1.5),
			Foreground = fg,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(3, 0, 0, 0)
		});
		return chip;
	}

	private FrameworkElement BuildExpanded(IslandOptions o, bool light, Brush text, Brush muted, Brush chip)
	{
		StackPanel root = new StackPanel { Width = Math.Min(320, o.ExpandedWidth) };
		// 页签条
		StackPanel tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 10, 8, 6) };
		if (o.ShowMusicPage)
		{
			tabs.Children.Add(Tab("正在播放", true, text, chip));
		}
		if (o.ShowNotificationsPage)
		{
			tabs.Children.Add(Tab("通知", false, muted, chip));
		}
		if (o.ClipboardEnabled)
		{
			tabs.Children.Add(Tab("剪贴板", false, muted, chip));
		}
		if (o.ShowControlPage)
		{
			tabs.Children.Add(Tab("控制中心", false, muted, chip));
		}
		root.Children.Add(tabs);

		Grid body = new Grid { Margin = new Thickness(14, 4, 14, 14) };
		body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		Border cover = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(Math.Min(o.CornerRadius, 36.0)), ClipToBounds = true, Margin = new Thickness(0, 0, 12, 0) };
		MusicEngine m = C?.Music;
		if (m != null && m.Artwork != null)
		{
			Image img = new Image { Source = m.Artwork, Stretch = Stretch.UniformToFill, Width = 72, Height = 72 };
			img.Clip = new RectangleGeometry(new Rect(0, 0, 72, 72), Math.Min(o.CornerRadius, 36.0), Math.Min(o.CornerRadius, 36.0));
			cover.Child = img;
		}
		else
		{
			cover.Child = IslandIcons.MusicPlaceholder(text, chip, 72, 14);
		}
		body.Children.Add(cover);
		StackPanel info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		info.Children.Add(new TextBlock
		{
			Text = m != null && !string.IsNullOrEmpty(m.Title) ? m.Title : "示例曲目",
			FontSize = o.FontSize + 2,
			FontWeight = FontWeights.SemiBold,
			Foreground = text,
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		info.Children.Add(new TextBlock
		{
			Text = m != null && !string.IsNullOrEmpty(m.Artist) ? m.Artist : "示例歌手",
			FontSize = o.FontSize - 2,
			Foreground = muted,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0, 4, 0, 0)
		});
		Grid.SetColumn(info, 1);
		body.Children.Add(info);
		root.Children.Add(body);
		return root;
	}

	private static Border Tab(string label, bool active, Brush fg, Brush chip)
	{
		Border b = new Border
		{
			CornerRadius = new CornerRadius(12),
			Background = active ? chip : Brushes.Transparent,
			Padding = new Thickness(10, 5, 10, 5),
			Margin = new Thickness(0, 0, 6, 0)
		};
		b.Child = new TextBlock
		{
			Text = label,
			FontSize = 12.5,
			Foreground = fg,
			FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal
		};
		return b;
	}
}
