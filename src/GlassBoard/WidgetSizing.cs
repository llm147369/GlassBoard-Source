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

	// 统一外观开启时：全局透明度优先，忽略面板独立值（组件右键菜单单独调过透明度时，
	// 面板值会盖掉统一值，导致「统一透明度」只有 Dock 变化）。非破坏性：面板独立值保留，
	// 关闭统一后各面板恢复自己的透明度。
	public static double Background(Preferences prefs, WidgetOptions options, WidgetPanelPosition panel)
	{
		if (prefs.GlobalStyle.Unify)
		{
			return LayoutRules.Clamp(options.OpacityPercent, 0.0, 100.0, 72.0);
		}
		return Background(options, panel);
	}
}
