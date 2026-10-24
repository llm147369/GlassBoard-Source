using System;

namespace GlassBoard;

public static class WidgetSizing
{
	public static double? Dimension(double? value, double min, double max)
	{
		if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
		{
			return null;
		}
		return Math.Max(min, Math.Min(max, value.Value));
	}

	public static double MinimumHeight(double natural, double available)
	{
		return Math.Min(available, Math.Max(140.0, natural * 0.5 + 28.0));
	}

	public static double Background(WidgetOptions options, WidgetPanelPosition panel)
	{
		return LayoutRules.Clamp(panel.OpacityPercent ?? options.OpacityPercent, 0.0, 100.0, 72.0);
	}
}
