using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class FeatureRules
{
	public static string ImportGroup(Preferences prefs, Entry entry, string target)
	{
		if (target == "未分组")
		{
			return null;
		}
		if (!string.IsNullOrWhiteSpace(target) && target != "全部" && prefs.Groups.Contains(target))
		{
			return target;
		}
		if (!prefs.AutoGrouping)
		{
			return null;
		}
		return AutoGroupRules.Guess(entry, prefs);
	}

	public static void NormalizeMemberships(Entry a)
	{
		a.Group = (string.IsNullOrWhiteSpace(a.Group) ? null : a.Group.Trim());
		if (a.Group == "全部" || a.Group == "未分组")
		{
			a.Group = null;
		}
		if (a.ExtraGroups == null)
		{
			a.ExtraGroups = new List<string>();
		}
		a.ExtraGroups = (from g in a.ExtraGroups
			where !string.IsNullOrWhiteSpace(g)
			select g.Trim() into g
			where g != "全部" && g != "未分组" && !string.Equals(g, a.Group, StringComparison.OrdinalIgnoreCase)
			select g).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (string.IsNullOrWhiteSpace(a.Group) && a.ExtraGroups.Count > 0)
		{
			a.Group = a.ExtraGroups[0];
			a.ExtraGroups.RemoveAt(0);
		}
	}

	public static bool InGroup(Entry a, string group)
	{
		if (group == "全部")
		{
			return true;
		}
		if (group == "未分组")
		{
			if (string.IsNullOrWhiteSpace(a.Group))
			{
				if (a.ExtraGroups != null)
				{
					return a.ExtraGroups.Count == 0;
				}
				return true;
			}
			return false;
		}
		if (!string.Equals(a.Group, group, StringComparison.OrdinalIgnoreCase))
		{
			if (a.ExtraGroups != null)
			{
				return a.ExtraGroups.Any((string g) => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
			}
			return false;
		}
		return true;
	}

	public static IEnumerable<string> Memberships(Entry a)
	{
		return from g in new string[1] { a.Group }.Concat(a.ExtraGroups ?? new List<string>())
			where !string.IsNullOrWhiteSpace(g)
			select g;
	}

	public static void AddMembership(Entry a, string group)
	{
		NormalizeMemberships(a);
		if (group == "未分组")
		{
			a.Group = null;
			a.ExtraGroups.Clear();
		}
		else if (!InGroup(a, group))
		{
			if (string.IsNullOrWhiteSpace(a.Group))
			{
				a.Group = group;
			}
			else
			{
				a.ExtraGroups.Add(group);
			}
		}
	}

	public static void RemoveMembership(Entry a, string group)
	{
		if (string.Equals(a.Group, group, StringComparison.OrdinalIgnoreCase))
		{
			a.Group = null;
		}
		if (a.ExtraGroups != null)
		{
			a.ExtraGroups.RemoveAll((string g) => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
		}
		NormalizeMemberships(a);
	}

	public static int AddShared(Preferences p, IEnumerable<string> paths, string group)
	{
		if (group == "全部" || string.IsNullOrWhiteSpace(group))
		{
			throw new ArgumentException("请先选择目标分组");
		}
		HashSet<string> set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
		int num = 0;
		foreach (Entry item in p.Apps.Where((Entry a) => set.Contains(a.Path)))
		{
			if (!InGroup(item, group))
			{
				AddMembership(item, group);
				num++;
			}
		}
		p.Groups = Groups(p.Groups, p.Apps);
		return num;
	}

	public static int Transfer(Preferences p, IEnumerable<string> paths, string from, string to)
	{
		if (from == to)
		{
			return 0;
		}
		if (to == "全部")
		{
			throw new ArgumentException("不能移动到全部分类");
		}
		if (from == "全部")
		{
			return AddShared(p, paths, to);
		}
		HashSet<string> set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
		int num = 0;
		foreach (Entry item in p.Apps.Where((Entry a) => set.Contains(a.Path) && InGroup(a, from)))
		{
			RemoveMembership(item, from);
			AddMembership(item, to);
			num++;
		}
		p.Groups = Groups(p.Groups, p.Apps);
		return num;
	}

	public static string GroupName(Entry entry)
	{
		if (!string.IsNullOrWhiteSpace(entry.Group))
		{
			return entry.Group.Trim();
		}
		return "未分组";
	}

	public static List<string> Groups(IEnumerable<string> groups, IEnumerable<Entry> apps)
	{
		return (from s in (groups ?? new string[3] { "创作", "办公", "游戏" }).Concat(apps.SelectMany(Memberships))
			select (s ?? "").Trim() into s
			where s.Length > 0 && s != "全部" && s != "未分组"
			select s).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
	}

	public static int Move(Preferences p, IEnumerable<string> paths, string group)
	{
		string text = (group ?? "").Trim();
		if (text == "全部")
		{
			throw new ArgumentException("不能移动到全部分类");
		}
		HashSet<string> set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
		List<Entry> list = p.Apps.Where((Entry a) => set.Contains(a.Path)).ToList();
		foreach (Entry item in list)
		{
			item.Group = ((text == "未分组") ? null : text);
			item.ExtraGroups = new List<string>();
			string text2 = AutoGroupRules.Identity(item);
			if (text2.Length > 0)
			{
				p.LearnedGroups[text2] = item.Group ?? "";
			}
		}
		p.Groups = Groups(p.Groups, p.Apps);
		return list.Count;
	}

	public static void RenameGroup(Preferences p, string before, string after)
	{
		after = (after ?? "").Trim();
		if (after.Length == 0 || after == "全部" || after == "未分组")
		{
			throw new ArgumentException("请输入有效分组名称");
		}
		if (p.Groups.Any((string g) => g.Equals(after, StringComparison.CurrentCultureIgnoreCase) && !g.Equals(before, StringComparison.CurrentCultureIgnoreCase)))
		{
			throw new ArgumentException("分组名称重复");
		}
		foreach (string item in p.LearnedGroups.Keys.ToList())
		{
			if (p.LearnedGroups[item] == before)
			{
				p.LearnedGroups[item] = after;
			}
		}
		foreach (Entry app in p.Apps)
		{
			if (GroupName(app) == before)
			{
				app.Group = after;
			}
			if (app.ExtraGroups != null)
			{
				app.ExtraGroups = app.ExtraGroups.Select((string g) => (!(g == before)) ? g : after).ToList();
			}
			NormalizeMemberships(app);
		}
		p.Groups = p.Groups.Select((string g) => (!(g == before)) ? g : after).ToList();
		p.Groups = Groups(p.Groups, p.Apps);
	}

	public static void DeleteGroup(Preferences p, string group)
	{
		foreach (string item in p.LearnedGroups.Keys.Where((string k) => p.LearnedGroups[k] == group).ToList())
		{
			p.LearnedGroups.Remove(item);
		}
		foreach (Entry app in p.Apps)
		{
			RemoveMembership(app, group);
		}
		p.Groups.RemoveAll((string g) => g == group);
	}

	public static Entry Copy(Entry a)
	{
		return new Entry
		{
			Name = a.Name,
			Path = a.Path,
			Pinned = a.Pinned,
			IsFolder = a.IsFolder,
			Group = a.Group,
			ExtraGroups = ((a.ExtraGroups == null) ? null : a.ExtraGroups.ToList()),
			IconPath = a.IconPath,
			LaunchTarget = a.LaunchTarget,
			LaunchArguments = a.LaunchArguments,
			ProductInfo = a.ProductInfo,
			WindowTarget = a.WindowTarget
		};
	}

	public static AppearanceScheme Capture(Preferences p, string name)
	{
		return new AppearanceScheme
		{
			Name = name,
			Light = p.Light,
			BoardHideSearch = p.BoardHideSearch,
			BoardHideGroups = p.BoardHideGroups,
			BoardHideFolders = p.BoardHideFolders,
			PanelSize = p.PanelSize,
			BoardIconSize = p.BoardIconSize,
			BoardIconGap = p.BoardIconGap,
			OpacityPercent = (p.OpacityPercent ?? 72.0),
			DockGap = p.DockGap,
			Dock = p.Dock.Copy(),
			BoardMaterial = p.BoardMaterial.Copy(),
			DockOrder = (p.DockOrder ?? new List<string>()).ToList(),
			Placement = p.Placement.Copy(),
			Widgets = p.Widgets.Copy(),
			Apps = p.Apps.Select(Copy).ToList(),
			Groups = p.Groups.ToList()
		};
	}

	public static void Apply(Preferences p, AppearanceScheme scheme)
	{
		if (scheme == null || scheme.Version != 1 || scheme.Dock == null)
		{
			throw new ArgumentException("方案格式或版本不支持");
		}
		List<Entry> list = EntryRules.NormalizeEntries((scheme.Apps ?? new List<Entry>()).Where((Entry a) => a != null && !string.IsNullOrWhiteSpace(a.Path)).Select(Copy));
		HashSet<string> known = new HashSet<string>(list.Select((Entry a) => a.Path), StringComparer.OrdinalIgnoreCase);
		list.AddRange(p.Apps.Where((Entry a) => !known.Contains(a.Path)));
		p.Apps = list;
		p.Removed.RemoveAll(known.Contains);
		p.Light = scheme.Light;
		p.BoardHideSearch = scheme.BoardHideSearch;
		p.BoardHideGroups = scheme.BoardHideGroups;
		p.BoardHideFolders = scheme.BoardHideFolders;
		p.PanelSize = scheme.PanelSize;
		p.BoardIconSize = scheme.BoardIconSize;
		p.BoardIconGap = scheme.BoardIconGap;
		p.OpacityPercent = scheme.OpacityPercent;
		p.DockGap = scheme.DockGap;
		p.Dock = scheme.Dock.Copy();
		p.BoardMaterial = (scheme.BoardMaterial ?? new MaterialOptions()).Copy();
		p.DockOrder = (scheme.DockOrder ?? new List<string>()).ToList();
		p.Placement = (scheme.Placement ?? new PlacementOptions()).Copy();
		p.Widgets = (scheme.Widgets ?? new WidgetOptions()).Copy();
		p.Groups = (scheme.Groups ?? new List<string>()).ToList();
		p.Normalize();
	}

	public static uint AutoHideState(uint original, bool enabled)
	{
		if (!enabled)
		{
			return original & 0xFFFFFFFEu;
		}
		return original | 1;
	}

	public static double Position(double desired, double origin, double extent, double length)
	{
		return Math.Max(origin, Math.Min(origin + Math.Max(0.0, extent - length), desired));
	}

	public static string WeatherLabel(int code)
	{
		switch (code)
		{
		case 0:
			return "晴";
		case 1:
		case 2:
		case 3:
			return "多云";
		default:
			switch (code)
			{
			case 45:
			case 48:
				return "雾";
			case 51:
			case 52:
			case 53:
			case 54:
			case 55:
			case 56:
			case 57:
			case 58:
			case 59:
			case 60:
			case 61:
			case 62:
			case 63:
			case 64:
			case 65:
			case 66:
			case 67:
				return "雨";
			default:
				if (code >= 71 && code <= 77)
				{
					return "雪";
				}
				if (code >= 80 && code <= 82)
				{
					return "阵雨";
				}
				switch (code)
				{
				case 85:
				case 86:
					return "阵雪";
				case 95:
				case 96:
				case 99:
					return "雷雨";
				default:
					return "天气代码 " + code;
				}
			}
		}
	}
}
