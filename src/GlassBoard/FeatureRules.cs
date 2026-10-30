using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class FeatureRules
{
	public static Entry Copy(Entry a)
	{
		return new Entry
		{
			Name = a.Name,
			Path = a.Path,
			Pinned = a.Pinned,
			IsFolder = a.IsFolder,
			IconPath = a.IconPath,
			LaunchTarget = a.LaunchTarget,
			LaunchArguments = a.LaunchArguments,
			ProductInfo = a.ProductInfo,
			WindowTarget = a.WindowTarget,
			Folder = a.Folder
		};
	}

	public static AppearanceScheme Capture(Preferences p, string name)
	{
		return new AppearanceScheme
		{
			Name = name,
			Light = p.Light,
			BoardHideSearch = p.BoardHideSearch,
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
			Apps = p.Apps.Select(Copy).ToList()
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
