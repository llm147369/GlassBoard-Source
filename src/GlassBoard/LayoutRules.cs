using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class LayoutRules
{
	public static int Pages(int count, int capacity)
	{
		if (count < 0 || capacity < 1)
		{
			throw new ArgumentOutOfRangeException();
		}
		return Math.Max(1, (count + capacity - 1) / capacity);
	}

	public static bool Matches(string name, string query)
	{
		return (query ?? "").Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries).All((string word) => (name ?? "").IndexOf(word, StringComparison.CurrentCultureIgnoreCase) >= 0);
	}

	public static double GlassOpacity(double value)
	{
		if (!double.IsNaN(value) && !double.IsInfinity(value))
		{
			return Math.Max(0.0, Math.Min(100.0, value));
		}
		return 72.0;
	}

	public static bool AllowImport(string path, IEnumerable<string> removed, bool manual)
	{
		if (!manual)
		{
			return !removed.Any((string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
		}
		return true;
	}

	public static double Clamp(double value, double min, double max, double fallback)
	{
		if (!double.IsNaN(value) && !double.IsInfinity(value))
		{
			return Math.Max(min, Math.Min(max, value));
		}
		return fallback;
	}
}
