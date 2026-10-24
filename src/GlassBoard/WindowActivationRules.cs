using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GlassBoard;

public static class WindowActivationRules
{
	public static string NormalizePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		string text = path.Trim().Trim('"').Replace('/', '\\');
		if (!text.StartsWith("\\\\?\\", StringComparison.Ordinal))
		{
			return text;
		}
		return text.Substring(4);
	}

	public static string[] Executables(Entry entry)
	{
		if (entry == null || entry.IsFolder)
		{
			return new string[0];
		}
		string text = NormalizePath(entry.WindowTarget);
		if (text != null && Path.GetExtension(text).Equals(".exe", StringComparison.OrdinalIgnoreCase))
		{
			return new string[1] { text };
		}
		return (from p in new string[2] { entry.LaunchTarget, entry.Path }.Select(NormalizePath)
			where p != null && Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase)
			select p).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	public static long Select(IEnumerable<long> windows, long foreground, long previous, bool ownForeground)
	{
		List<long> list = (from h in windows.Where((long h) => h != 0).Distinct()
			orderby h
			select h).ToList();
		if (list.Count == 0)
		{
			return 0L;
		}
		int num = list.IndexOf(foreground);
		if (num >= 0)
		{
			return list[(num + 1) % list.Count];
		}
		int num2 = list.IndexOf(previous);
		if (num2 >= 0)
		{
			return list[ownForeground ? ((num2 + 1) % list.Count) : num2];
		}
		return list[0];
	}
}
