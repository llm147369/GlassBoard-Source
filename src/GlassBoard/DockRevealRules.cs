using System;

namespace GlassBoard;

public static class DockRevealRules
{
	public static double[] Stops(double current, double target, double intensity, double top)
	{
		double num = Math.Min(12.0, Math.Abs(current - target) * 0.07) * Math.Max(0.5, Math.Min(1.5, intensity));
		return new double[4]
		{
			current,
			Math.Max(top, target - num),
			target + num * 0.22,
			target
		};
	}
}
