using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class RemovalRules
{
	public static string Identity(Entry entry)
	{
		if (entry == null)
		{
			return null;
		}
		string text = WindowActivationRules.NormalizePath(entry.LaunchTarget);
		if (string.IsNullOrWhiteSpace(text))
		{
			return "path|" + WindowActivationRules.NormalizePath(entry.Path);
		}
		return "target|" + text + "|" + (entry.LaunchArguments ?? "").Trim();
	}

	public static bool Blocked(Preferences prefs, Entry entry)
	{
		if (!(prefs.Removed ?? new List<string>()).Any((string p) => string.Equals(WindowActivationRules.NormalizePath(p), WindowActivationRules.NormalizePath(entry.Path), StringComparison.OrdinalIgnoreCase)))
		{
			return (prefs.RemovedIdentities ?? new List<string>()).Contains(Identity(entry), StringComparer.OrdinalIgnoreCase);
		}
		return true;
	}

	public static void Remember(Preferences prefs, Entry entry)
	{
		if (prefs.RemovedIdentities == null)
		{
			prefs.RemovedIdentities = new List<string>();
		}
		string text = Identity(entry);
		if (text != null && !prefs.RemovedIdentities.Contains(text, StringComparer.OrdinalIgnoreCase))
		{
			prefs.RemovedIdentities.Add(text);
		}
	}

	public static void Forget(Preferences prefs, Entry entry)
	{
		if (prefs.RemovedIdentities != null)
		{
			prefs.RemovedIdentities.RemoveAll((string key) => string.Equals(key, Identity(entry), StringComparison.OrdinalIgnoreCase));
		}
		prefs.Removed.RemoveAll((string p) => string.Equals(WindowActivationRules.NormalizePath(p), WindowActivationRules.NormalizePath(entry.Path), StringComparison.OrdinalIgnoreCase));
	}

	public static void ReconcileActive(Preferences prefs)
	{
		if (prefs.Removed == null)
		{
			prefs.Removed = new List<string>();
		}
		if (prefs.RemovedIdentities == null)
		{
			prefs.RemovedIdentities = new List<string>();
		}
		foreach (Entry item in prefs.Apps ?? new List<Entry>())
		{
			Forget(prefs, item);
		}
	}

	public static bool Same(Entry one, Entry two)
	{
		if (!string.IsNullOrWhiteSpace(one.LaunchTarget) && !string.IsNullOrWhiteSpace(two.LaunchTarget))
		{
			return string.Equals(Identity(one), Identity(two), StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}
}
