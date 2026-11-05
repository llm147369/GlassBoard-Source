using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GlassBoard;

// 灵动岛专用图标：全部在 24x24 视窗内自绘（Viewbox 缩放），不依赖 IconPacks。
// 现成 IconPacks 里没有电池/鼠标/键盘等图标，且这些图标需要在紧凑尺寸下
// 按电量百分比动态填充，自绘更可控。
public static class IslandIcons
{
	public static FrameworkElement PathIcon(string data, Brush brush, double size, bool fill = true, double strokeWidth = 1.6)
	{
		Path p = new Path
		{
			Data = Geometry.Parse(data),
			Stretch = Stretch.Uniform
		};
		if (fill)
		{
			p.Fill = brush;
		}
		else
		{
			p.Fill = null;
			p.Stroke = brush;
			p.StrokeThickness = strokeWidth;
			p.StrokeStartLineCap = PenLineCap.Round;
			p.StrokeEndLineCap = PenLineCap.Round;
			p.StrokeLineJoin = PenLineJoin.Round;
		}
		return new Viewbox { Width = size, Height = size, Child = p };
	}

	// level: null=不绘制（无数据）；0..1 填充比例；charging 画闪电
	public static FrameworkElement Battery(double? level, bool charging, Brush brush, Brush trackBrush, double size)
	{
		Canvas c = new Canvas { Width = 24, Height = 24 };
		// 外壳
		Path shell = new Path
		{
			Data = Geometry.Parse("M2.5 8.5 H16 A2.5 2.5 0 0 1 18.5 11 V13 A2.5 2.5 0 0 1 16 15.5 H2.5 A2.5 2.5 0 0 1 0 13 V11 A2.5 2.5 0 0 1 2.5 8.5 Z"),
			Fill = null,
			Stroke = brush,
			StrokeThickness = 1.4
		};
		c.Children.Add(shell);
		// 正极触点
		Path tip = new Path
		{
			Data = Geometry.Parse("M20.5 10.5 H22.5 V13.5 H20.5 Z"),
			Fill = brush
		};
		c.Children.Add(tip);
		if (level.HasValue)
		{
			double lv = Math.Max(0.04, Math.Min(1.0, level.Value));
			double w = 12.6 * lv; // 内部宽度 16-3.4 留边
			System.Windows.Shapes.Rectangle fill = new System.Windows.Shapes.Rectangle
			{
				Width = w,
				Height = 1.6,
				RadiusX = 0.6,
				RadiusY = 0.6,
				Fill = charging ? brush : (level.Value <= 0.2 ? trackBrush : brush)
			};
			Canvas.SetLeft(fill, 3.2);
			Canvas.SetTop(fill, 11.2);
			// 低电量用警示色（trackBrush 由调用方传红色系）
			if (!charging && level.Value <= 0.2 && trackBrush == null)
			{
				fill.Fill = brush;
			}
			c.Children.Add(fill);
			// 电量分格
			Path ticks = new Path
			{
				Data = Geometry.Parse("M8.7 10.2 V13.8 M12.9 10.2 V13.8"),
				Stroke = Brushes.Transparent
			};
			c.Children.Add(ticks);
		}
		else
		{
			// 无数据：只画一个问号点阵，调用方一般不会走这里
			Path q = new Path
			{
				Data = Geometry.Parse("M9 9 A3.2 3.2 0 1 1 12.4 13.8 V15.5"),
				Stroke = brush,
				StrokeThickness = 1.6,
				Fill = null
			};
			c.Children.Add(q);
			Ellipse dot = new Ellipse { Width = 1.6, Height = 1.6, Fill = brush };
			Canvas.SetLeft(dot, 11.2);
			Canvas.SetTop(dot, 18);
			c.Children.Add(dot);
		}
		if (charging)
		{
			Path bolt = new Path
			{
				Data = Geometry.Parse("M11.2 4.5 L6.8 12 H9.4 L8.6 19.5 L13.4 11.2 H10.8 Z"),
				Fill = brush,
				Stretch = Stretch.Uniform
			};
			c.Children.Add(bolt);
		}
		return new Viewbox { Width = size, Height = size, Child = c };
	}

	public static FrameworkElement Chevron(Brush brush, double size)
	{
		return PathIcon("M6.5 9 L12 14.5 L17.5 9", brush, size, fill: false, strokeWidth: 1.8);
	}

	public static FrameworkElement Bell(Brush brush, double size)
	{
		return PathIcon("M12 3 A5.5 5.5 0 0 0 6.5 8.5 V14 L4.5 16 V16.8 H19.5 V16 L17.5 14 V8.5 A5.5 5.5 0 0 0 12 3 Z M10 19.2 A2.1 2.1 0 0 0 14 19.2", brush, size, fill: false, strokeWidth: 1.5);
	}

	public static FrameworkElement Clipboard(Brush brush, double size)
	{
		return PathIcon("M8.5 4 H7 A2 2 0 0 0 5 6 V20 A2 2 0 0 0 7 22 H17 A2 2 0 0 0 19 20 V6 A2 2 0 0 0 17 4 H15.5 M9 4 H15 M9 4.5 V3.2 A1 1 0 0 1 10 2.4 H14 A1 1 0 0 1 15 3.2 V4.5 M9 12 H15 M9 16 H13", brush, size, fill: false, strokeWidth: 1.5);
	}

	public static FrameworkElement Play(Brush brush, double size)
	{
		return PathIcon("M8 5.2 L19 12 L8 18.8 Z", brush, size);
	}

	public static FrameworkElement Pause(Brush brush, double size)
	{
		return PathIcon("M8 5 H10.8 V19 H8 Z M13.2 5 H16 V19 H13.2 Z", brush, size);
	}

	public static FrameworkElement Previous(Brush brush, double size)
	{
		return PathIcon("M7 5 H9.6 V19 H7 Z M18.5 5 L10.5 12 L18.5 19 Z", brush, size);
	}

	public static FrameworkElement Next(Brush brush, double size)
	{
		return PathIcon("M14.4 5 H17 V19 H14.4 Z M5.5 5 L13.5 12 L5.5 19 Z", brush, size);
	}

	public static FrameworkElement Close(Brush brush, double size)
	{
		return PathIcon("M6.5 6.5 L17.5 17.5 M17.5 6.5 L6.5 17.5", brush, size, fill: false, strokeWidth: 1.6);
	}

	public static FrameworkElement Music(Brush brush, double size)
	{
		return PathIcon("M9.5 18.5 A2.5 2.5 0 1 1 7 16 V7.5 L18 5.5 V15.5 A2.5 2.5 0 1 1 15.5 13 V8.5 L9.5 10 V18.5 Z", brush, size, fill: false, strokeWidth: 1.5);
	}

	public static FrameworkElement Speaker(Brush brush, double size)
	{
		// 两条声波弧的终点曾写成相对偏移(0,6.4)/(0,10.4)而未换算成绝对坐标，
		// 导致圆弧横穿整个图标、显示成一团乱线（灵动岛上的“脏白点/音量图标不显示”根因）。
		return PathIcon("M4 9.5 V14.5 H7.5 L12.5 19 V5 L7.5 9.5 Z M15.5 8.8 A3.8 3.8 0 0 1 15.5 15.2 M17.5 6.8 A6.5 6.5 0 0 1 17.5 17.2", brush, size, fill: false, strokeWidth: 1.5);
	}

	public static FrameworkElement Gear(Brush brush, double size)
	{
		return PathIcon("M12 8.5 A3.5 3.5 0 1 0 12 15.5 A3.5 3.5 0 1 0 12 8.5 M12 3.2 L13.1 5.4 L15.7 5.6 L16.2 8 L18.2 9.3 L17.5 11.7 L19 13.6 L17.1 15.2 L17.4 17.6 L15 18.4 L13.6 20.5 L11.2 20.3 L9.8 22.2 H7.4 L6 20.3 L3.6 20.5 L2.2 18.4 L-0.2 17.6 L0.1 15.2 L-1.8 13.6 L-0.3 11.7 L-1 9.3 L1 8 L1.5 5.6 L4.1 5.4 L5.2 3.2 Z", brush, size, fill: false, strokeWidth: 1.4);
	}

	public static FrameworkElement Monitor(Brush brush, double size)
	{
		return PathIcon("M4 4.5 H20 V15 H4 Z M9 19.5 H15 M12 15 V19.5", brush, size, fill: false, strokeWidth: 1.5);
	}

	// —— 展开页签图标：Lucide 官方图标集（https://lucide.dev，ISC 协议，可自由商用）。
	// 24x24 视窗、2px 圆头描边，与灵动岛其余自绘线条图标同一风格。
	// music-2 / bell / clipboard / sliders-horizontal 的原始路径数据（已转 WPF 几何语法）。

	public static FrameworkElement TabMusic(Brush brush, double size)
	{
		return PathIcon("M8 14 A4 4 0 1 0 8 22 A4 4 0 1 0 8 14 M12 18 V2 L19 6", brush, size, fill: false, strokeWidth: 2.0);
	}

	public static FrameworkElement TabBell(Brush brush, double size)
	{
		return PathIcon("M10.268 21 A2 2 0 0 0 13.732 21 M3.262 15.326 A1 1 0 0 0 4 17 H16 A1 1 0 0 0 16.74 15.326 C19.41 13.956 18 12.499 18 8 A6 6 0 0 0 6 8 C6 12.499 4.589 13.956 3.262 15.326", brush, size, fill: false, strokeWidth: 2.0);
	}

	public static FrameworkElement TabClipboard(Brush brush, double size)
	{
		return PathIcon("M9 2 H15 A1 1 0 0 1 16 3 V5 A1 1 0 0 1 15 6 H9 A1 1 0 0 1 8 5 V3 A1 1 0 0 1 9 2 M16 4 H18 A2 2 0 0 1 20 6 V20 A2 2 0 0 1 18 22 H6 A2 2 0 0 1 4 20 V6 A2 2 0 0 1 6 4 H8", brush, size, fill: false, strokeWidth: 2.0);
	}

	public static FrameworkElement TabControl(Brush brush, double size)
	{
		return PathIcon("M10 5 H3 M12 19 H3 M14 3 V7 M16 17 V21 M21 12 H12 M21 19 H16 M21 5 H14 M8 10 V14 M8 12 H3", brush, size, fill: false, strokeWidth: 2.0);
	}

	public static FrameworkElement TabNote(Brush brush, double size)
	{
		return PathIcon("M15.5 3 H5 A2 2 0 0 0 3 5 V19 A2 2 0 0 0 5 21 H19 A2 2 0 0 0 21 19 V8.5 L15.5 3 Z M15 3 V9 H21", brush, size, fill: false, strokeWidth: 2.0);
	}

	// 无封面时的占位：圆角渐变块 + 音符
	public static FrameworkElement MusicPlaceholder(Brush foreground, Brush background, double size, double radius)
	{
		Grid g = new Grid { Width = size, Height = size };
		g.Children.Add(new Border
		{
			CornerRadius = new CornerRadius(radius),
			Background = background,
			Child = Music(foreground, size * 0.55)
		});
		return g;
	}
}
