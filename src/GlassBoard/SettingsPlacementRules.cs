using System;

namespace GlassBoard;

public static class SettingsPlacementRules
{
	public static double[] Vertical(double workTop, double workHeight, double dockTop, double dockHeight, double preferredHeight)
	{
		double num = workTop + Math.Max(1.0, workHeight);
		double num2 = Math.Min(12.0, workHeight / 4.0);
		double num3 = Math.Max(workTop, Math.Min(num, dockTop));
		double num4 = Math.Max(workTop, Math.Min(num, dockTop + Math.Max(0.0, dockHeight)));
		double num5 = Math.Max(0.0, num3 - workTop - 2.0 * num2);
		double num6 = Math.Max(0.0, num - num4 - 2.0 * num2);
		bool flag = num6 > num5;
		double num7 = (flag ? num6 : num5);
		if (!(num7 < 1.0))
		{
			double num8 = Math.Max(1.0, Math.Min(preferredHeight, num7));
			return new double[2]
			{
				(flag ? num4 : workTop) + num2 + (num7 - num8) / 2.0,
				num8
			};
		}
		return new double[2]
		{
			workTop + num2,
			Math.Max(1.0, workHeight - 2.0 * num2)
		};
	}
}
