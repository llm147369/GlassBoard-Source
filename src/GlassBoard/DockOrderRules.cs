using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class DockOrderRules
{
	public static List<Entry> Ordered(Preferences prefs)
	{
		List<Entry> list = prefs.Apps.Where((Entry a) => a != null && a.Pinned && !string.IsNullOrWhiteSpace(a.Path)).ToList();
		Dictionary<string, Entry> dictionary = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
		foreach (Entry item in list)
		{
			if (!dictionary.ContainsKey(item.Path))
			{
				dictionary[item.Path] = item;
			}
		}
		List<Entry> list2 = new List<Entry>();
		foreach (string item2 in prefs.DockOrder ?? new List<string>())
		{
			if (item2 != null && dictionary.TryGetValue(item2, out var value))
			{
				list2.Add(value);
				dictionary.Remove(item2);
			}
		}
		foreach (Entry item3 in list)
		{
			if (dictionary.Remove(item3.Path))
			{
				list2.Add(item3);
			}
		}
		return list2;
	}

	public static void Normalize(Preferences prefs)
	{
		prefs.DockOrder = (from a in Ordered(prefs)
			select a.Path).ToList();
		DockLayoutRules.SyncApps(prefs);
	}

	public static bool Move(Preferences prefs, string source, string target, bool before)
	{
		List<Entry> list = Ordered(prefs);
		Entry entry = list.FirstOrDefault((Entry a) => string.Equals(a.Path, source, StringComparison.OrdinalIgnoreCase));
		if (entry == null || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		Entry entry2 = (string.IsNullOrEmpty(target) ? null : list.FirstOrDefault((Entry a) => string.Equals(a.Path, target, StringComparison.OrdinalIgnoreCase)));
		if (!string.IsNullOrEmpty(target) && entry2 == null)
		{
			return false;
		}
		string[] second = list.Select((Entry a) => a.Path).ToArray();
		list.Remove(entry);
		int index = ((entry2 == null) ? list.Count : (list.IndexOf(entry2) + ((!before) ? 1 : 0)));
		list.Insert(index, entry);
		List<string> list2 = list.Select((Entry a) => a.Path).ToList();
		if (list2.SequenceEqual(second, StringComparer.OrdinalIgnoreCase))
		{
			return false;
		}
		prefs.DockOrder = list2;
		DockLayoutRules.SyncApps(prefs);
		return true;
	}

	public static bool Step(Preferences prefs, string source, int step)
	{
		List<Entry> list = Ordered(prefs);
		int num = list.FindIndex((Entry a) => string.Equals(a.Path, source, StringComparison.OrdinalIgnoreCase));
		int num2 = num + step;
		if (num < 0 || num2 < 0 || num2 >= list.Count || Math.Abs(step) != 1)
		{
			return false;
		}
		return Move(prefs, source, list[num2].Path, step < 0);
	}
}
