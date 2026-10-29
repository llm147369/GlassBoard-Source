using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GlassBoard;

public static class EntryRules
{
	public static RemovalBatch Remove(Preferences prefs, IEnumerable<string> paths)
	{
		HashSet<string> wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
		RemovalBatch removalBatch = new RemovalBatch();
		for (int i = 0; i < prefs.Apps.Count; i++)
		{
			Entry a = prefs.Apps[i];
			if (!wanted.Contains(a.Path))
			{
				continue;
			}
			removalBatch.Items.Add(new RemovedItem
			{
				Entry = a,
				Index = i,
				WasRemoved = prefs.Removed.Any((string p) => string.Equals(p, a.Path, StringComparison.OrdinalIgnoreCase))
			});
		}
		prefs.Apps.RemoveAll((Entry entry) => wanted.Contains(entry.Path));
		foreach (RemovedItem item in removalBatch.Items)
		{
			if (!item.WasRemoved)
			{
				prefs.Removed.Add(item.Entry.Path);
			}
			RemovalRules.Remember(prefs, item.Entry);
		}
		return removalBatch;
	}

	public static int Undo(Preferences prefs, RemovalBatch batch)
	{
		int num = 0;
		if (batch == null)
		{
			return 0;
		}
		foreach (RemovedItem item in batch.Items.OrderBy((RemovedItem x) => x.Index))
		{
			if (!prefs.Apps.Any((Entry a) => string.Equals(a.Path, item.Entry.Path, StringComparison.OrdinalIgnoreCase)))
			{
				prefs.Apps.Insert(Math.Min(item.Index, prefs.Apps.Count), item.Entry);
				num++;
			}
			if (!item.WasRemoved)
			{
				RemovalRules.Forget(prefs, item.Entry);
			}
		}
		return num;
	}

	public static List<Entry> NormalizeEntries(IEnumerable<Entry> apps)
	{
		List<Entry> list = new List<Entry>();
		Dictionary<string, Entry> dictionary = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
		foreach (Entry item in apps ?? new Entry[0])
		{
			if (item == null || string.IsNullOrWhiteSpace(item.Path))
			{
				continue;
			}
			if (Directory.Exists(item.Path))
			{
				item.Path = EntryPathRules.Normalize(item.Path);
				item.IsFolder = true;
			}
			if (!dictionary.TryGetValue(item.Path, out var value))
			{
				dictionary.Add(item.Path, item);
				list.Add(item);
				continue;
			}
			value.Pinned |= item.Pinned;
			value.IsFolder |= item.IsFolder;
			if (string.IsNullOrEmpty(value.IconPath))
			{
				value.IconPath = item.IconPath;
			}
			if (string.IsNullOrEmpty(value.LaunchTarget))
			{
				value.LaunchTarget = item.LaunchTarget;
				value.LaunchArguments = item.LaunchArguments;
			}
			if (string.IsNullOrEmpty(value.ProductInfo))
			{
				value.ProductInfo = item.ProductInfo;
			}
			if (string.IsNullOrEmpty(value.WindowTarget))
			{
				value.WindowTarget = item.WindowTarget;
			}
		}
		return list;
	}

	public static bool Importable(string path)
	{
		switch (Path.GetExtension(path).ToLowerInvariant())
		{
		default:
			return DocumentRules.Supported(path);
		case ".lnk":
		case ".appref-ms":
		case ".exe":
		case ".url":
		case ".cmd":
		case ".bat":
			return true;
		}
	}

	public static List<string> Candidates(IEnumerable<string> paths, IEnumerable<Entry> existing)
	{
		HashSet<string> known = new HashSet<string>(existing.Select((Entry a) => a.Path), StringComparer.OrdinalIgnoreCase);
		known = new HashSet<string>(known.Select(EntryPathRules.Normalize), StringComparer.OrdinalIgnoreCase);
		return (from p in paths.Select(EntryPathRules.Normalize)
			where !string.IsNullOrWhiteSpace(p) && !known.Contains(p)
			select p).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(EntryPathRules.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
	}
}
