using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class DockDropRules
{
	public static DockDropTarget OnIcon(Preferences prefs, string source, string target)
	{
		List<string> list = (from a in DockOrderRules.Ordered(prefs)
			select a.Path).ToList();
		int num = list.FindIndex((string p) => string.Equals(p, source, StringComparison.OrdinalIgnoreCase));
		int num2 = list.FindIndex((string p) => string.Equals(p, target, StringComparison.OrdinalIgnoreCase));
		if (num < 0 || num2 < 0 || num == num2)
		{
			return null;
		}
		return new DockDropTarget
		{
			Path = target,
			Before = (num > num2)
		};
	}

	public static DockDropTarget OnGap(string source, string[] visible, double[] centers, double x)
	{
		if (visible == null || centers == null || visible.Length != centers.Length || double.IsNaN(x) || double.IsInfinity(x))
		{
			return null;
		}
		for (int i = 0; i < visible.Length; i++)
		{
			if (!string.Equals(visible[i], source, StringComparison.OrdinalIgnoreCase) && x < centers[i])
			{
				return new DockDropTarget
				{
					Path = visible[i],
					Before = true
				};
			}
		}
		return new DockDropTarget
		{
			Path = null,
			Before = false
		};
	}
}
