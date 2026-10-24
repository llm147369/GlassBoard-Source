using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class DockLayoutRules
{
	public const string Apps = "glassboard://dock/apps";

	public const string Computer = "glassboard://dock/computer";

	public const string ControlCenter = "glassboard://dock/control";

	public const string Power = "glassboard://dock/power";

	public const string Network = "glassboard://dock/network";

	public const string RunningList = "glassboard://dock/running";

	public const string Settings = "glassboard://dock/settings";

	public static readonly string[] SystemKeys = new string[7] { "glassboard://dock/apps", "glassboard://dock/computer", "glassboard://dock/control", "glassboard://dock/power", "glassboard://dock/network", "glassboard://dock/settings", "glassboard://dock/running" };

	public static bool SystemItem(string key)
	{
		return SystemKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
	}

	public static bool Visible(DockOptions o, string key)
	{
		switch (key)
		{
		default:
			if (!(key != "glassboard://dock/settings"))
			{
				return o.ShowSettings;
			}
			return true;
		case "glassboard://dock/network":
			return o.ShowNetwork;
		case "glassboard://dock/power":
			return o.ShowPower;
		case "glassboard://dock/control":
			return o.ShowControlCenter;
		case "glassboard://dock/computer":
			return o.ShowComputer;
		case "glassboard://dock/running":
			return o.ShowRunningList;
		}
	}

	private static List<string> Merge(IEnumerable<string> saved, List<string> apps)
	{
		HashSet<string> allowed = new HashSet<string>(SystemKeys.Concat(apps), StringComparer.OrdinalIgnoreCase);
		List<string> list = (saved ?? new string[0]).Where((string k) => k != null && allowed.Contains(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count == 0)
		{
			return new string[2] { "glassboard://dock/apps", "glassboard://dock/computer" }.Concat(apps).Concat(new string[5] { "glassboard://dock/control", "glassboard://dock/running", "glassboard://dock/power", "glassboard://dock/network", "glassboard://dock/settings" }).ToList();
		}
		string[] systemKeys = SystemKeys;
		foreach (string text in systemKeys)
		{
			if (!list.Contains(text, StringComparer.OrdinalIgnoreCase))
			{
				list.Add(text);
			}
		}
		foreach (string app in apps)
		{
			if (!list.Contains(app, StringComparer.OrdinalIgnoreCase))
			{
				int num2 = list.FindLastIndex((string k) => !SystemItem(k));
				if (num2 < 0)
				{
					num2 = list.IndexOf("glassboard://dock/computer");
				}
				list.Insert((num2 < 0) ? list.Count : (num2 + 1), app);
			}
		}
		return list;
	}

	public static List<string> Common(Preferences p, bool visible = false)
	{
		List<string> list = Merge(p.Dock.ItemOrder, (from a in DockOrderRules.Ordered(p)
			select a.Path).ToList());
		if (!visible)
		{
			return list;
		}
		return list.Where((string k) => Visible(p.Dock, k)).ToList();
	}

	public static List<string> Running(Preferences p, IEnumerable<string> tasks, bool visible = false)
	{
		List<string> list = tasks.ToList();
		IEnumerable<string> enumerable = p.Dock.RunningOrder;
		if (enumerable == null || !enumerable.Any())
		{
			List<string> list2 = new List<string>();
			int num = 0;
			foreach (string item in Common(p))
			{
				if (SystemItem(item))
				{
					list2.Add(item);
				}
				else if (num < list.Count)
				{
					list2.Add(list[num++]);
				}
			}
			if (num < list.Count)
			{
				int num2 = list2.FindLastIndex((string k) => !SystemItem(k));
				list2.InsertRange((num2 < 0) ? Math.Min(2, list2.Count) : (num2 + 1), list.Skip(num));
			}
			enumerable = list2;
		}
		List<string> list3 = Merge(enumerable, list);
		if (!visible)
		{
			return list3;
		}
		return list3.Where((string k) => Visible(p.Dock, k)).ToList();
	}

	public static void SyncApps(Preferences p)
	{
		if (p.Dock == null)
		{
			return;
		}
		List<string> list = (from a in DockOrderRules.Ordered(p)
			select a.Path).ToList();
		List<string> list2 = Common(p);
		int num = 0;
		for (int num2 = 0; num2 < list2.Count; num2++)
		{
			if (!SystemItem(list2[num2]))
			{
				list2[num2] = list[num++];
			}
		}
		p.Dock.ItemOrder = list2;
	}

	public static bool Move(Preferences p, string source, string target, bool before, IEnumerable<string> tasks = null)
	{
		List<string> list = ((tasks == null) ? Common(p) : Running(p, tasks));
		int num = list.FindIndex((string k) => string.Equals(k, source, StringComparison.OrdinalIgnoreCase));
		int num2 = ((target == null) ? list.Count : list.FindIndex((string k) => string.Equals(k, target, StringComparison.OrdinalIgnoreCase)));
		if (num < 0 || num2 < 0 || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string item = list[num];
		string[] second = list.ToArray();
		list.RemoveAt(num);
		if (num < num2)
		{
			num2--;
		}
		if (target != null && !before)
		{
			num2++;
		}
		list.Insert(Math.Max(0, Math.Min(list.Count, num2)), item);
		if (list.SequenceEqual(second, StringComparer.OrdinalIgnoreCase))
		{
			return false;
		}
		if (tasks == null)
		{
			p.Dock.ItemOrder = list;
			p.DockOrder = list.Where((string k) => !SystemItem(k)).ToList();
		}
		else
		{
			p.Dock.RunningOrder = list;
		}
		return true;
	}

	public static bool Step(Preferences p, string source, int step, IEnumerable<string> tasks = null)
	{
		List<string> list = ((tasks == null) ? Common(p, visible: true) : Running(p, tasks, visible: true));
		int num = list.FindIndex((string k) => string.Equals(k, source, StringComparison.OrdinalIgnoreCase));
		int num2 = num + step;
		if (num >= 0 && num2 >= 0 && num2 < list.Count && Math.Abs(step) == 1)
		{
			return Move(p, source, list[num2], step < 0, tasks);
		}
		return false;
	}

	public static DockDropTarget OnIcon(Preferences p, string source, string target, IEnumerable<string> tasks = null)
	{
		List<string> list = ((tasks == null) ? Common(p) : Running(p, tasks));
		int num = list.FindIndex((string k) => string.Equals(k, source, StringComparison.OrdinalIgnoreCase));
		int num2 = list.FindIndex((string k) => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
		if (num >= 0 && num2 >= 0 && num != num2)
		{
			return new DockDropTarget
			{
				Path = target,
				Before = (num > num2)
			};
		}
		return null;
	}
}
