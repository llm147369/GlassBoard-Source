using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class DockPinRules
{
	public static DockPinResult Apply(Preferences prefs, IEnumerable<Entry> candidates, DockDropTarget target)
	{
		DockPinResult dockPinResult = new DockPinResult();
		List<Entry> moved = new List<Entry>();
		foreach (Entry candidate in candidates)
		{
			if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path))
			{
				continue;
			}
			Entry entry = prefs.Apps.FirstOrDefault((Entry a) => a.Pinned && (string.Equals(a.Path, candidate.Path, StringComparison.OrdinalIgnoreCase) || RemovalRules.Same(a, candidate))) ?? prefs.Apps.FirstOrDefault((Entry a) => string.Equals(a.Path, candidate.Path, StringComparison.OrdinalIgnoreCase)) ?? candidate;
			if (moved.Contains(entry))
			{
				continue;
			}
			if (!entry.Pinned && prefs.Apps.Count((Entry a) => a.Pinned) >= 20)
			{
				dockPinResult.Skipped++;
				continue;
			}
			if (!prefs.Apps.Contains(entry))
			{
				prefs.Apps.Add(entry);
			}
			if (!entry.Pinned)
			{
				entry.Pinned = true;
				dockPinResult.Added++;
			}
			RemovalRules.Forget(prefs, entry);
			moved.Add(entry);
			dockPinResult.Paths.Add(entry.Path);
		}
		if (moved.Count == 0)
		{
			return dockPinResult;
		}
		List<Entry> list = DockOrderRules.Ordered(prefs);
		Entry entry2 = ((target == null) ? null : list.FirstOrDefault((Entry a) => string.Equals(a.Path, target.Path, StringComparison.OrdinalIgnoreCase)));
		if (moved.Count == 1 && entry2 == moved[0])
		{
			return dockPinResult;
		}
		int num = ((entry2 == null) ? list.Count : (list.IndexOf(entry2) + ((!target.Before) ? 1 : 0)));
		int num2 = list.Take(num).Count((Entry a) => moved.Contains(a));
		list.RemoveAll((Entry a) => moved.Contains(a));
		num = Math.Max(0, Math.Min(list.Count, num - num2));
		list.InsertRange(num, moved);
		prefs.DockOrder = list.Select((Entry a) => a.Path).ToList();
		return dockPinResult;
	}
}
