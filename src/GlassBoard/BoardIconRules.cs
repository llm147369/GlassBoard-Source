using System;

namespace GlassBoard;

public static class BoardIconRules
{
	public static int Columns(double width, double icon, double gap)
	{
		return Math.Max(1, Math.Min(30, (int)(Math.Max(1.0, width) / (Math.Max(70.0, icon + 18.0) + gap * 2.0))));
	}

	public static int Rows(double height, double icon, double gap)
	{
		return Math.Max(1, Math.Min(20, (int)(Math.Max(1.0, height) / (icon + 40.0 + gap * 2.0))));
	}
}
