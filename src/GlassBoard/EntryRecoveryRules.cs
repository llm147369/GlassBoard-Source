using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class EntryRecoveryRules
{
	public static List<Entry> Candidates(Preferences prefs, IEnumerable<Entry> historical)
	{
		return EntryRules.NormalizeEntries(historical.Select(FeatureRules.Copy)).Where((Entry a) =>
		{
			Entry current = prefs.Apps.FirstOrDefault((Entry x) => string.Equals(x.Path, a.Path, StringComparison.OrdinalIgnoreCase));
			return current == null || FeatureRules.Memberships(a).Any((string g) => !FeatureRules.InGroup(current, g));
		}).ToList();
	}

	public static int Restore(Preferences prefs, IEnumerable<Entry> chosen)
	{
		int num = 0;
		foreach (Entry item in chosen)
		{
			Entry entry = prefs.Apps.FirstOrDefault((Entry a) => string.Equals(a.Path, item.Path, StringComparison.OrdinalIgnoreCase));
			if (entry == null)
			{
				entry = FeatureRules.Copy(item);
				prefs.Apps.Add(entry);
			}
			else
			{
				foreach (string item2 in FeatureRules.Memberships(item))
				{
					FeatureRules.AddMembership(entry, item2);
				}
			}
			RemovalRules.Forget(prefs, entry);
			num++;
		}
		prefs.Normalize();
		return num;
	}
}
