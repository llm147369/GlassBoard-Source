using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GlassBoard;

public static class LaunchInfoService
{
	public static Entry Read(string path)
	{
		Entry entry = new Entry
		{
			Name = EntryPathRules.Name(path),
			Path = EntryPathRules.Normalize(path),
			IsFolder = Directory.Exists(path)
		};
		Enrich(entry);
		return entry;
	}

	public static void Enrich(Entry entry)
	{
		string text = entry.Path ?? "";
		string text2 = "";
		string text3 = Path.GetExtension(text).ToLowerInvariant();
		if (Directory.Exists(text))
		{
			entry.IsFolder = true;
			entry.LaunchTarget = text;
			entry.LaunchArguments = "";
			return;
		}
		if (text3 == ".lnk")
		{
			object obj = null;
			object obj2 = null;
			try
			{
				Type typeFromProgID = Type.GetTypeFromProgID("WScript.Shell");
				obj = Activator.CreateInstance(typeFromProgID);
				obj2 = typeFromProgID.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, obj, new object[1] { text });
				text = (string)obj2.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, obj2, null);
				text2 = (string)obj2.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, obj2, null);
			}
			catch
			{
			}
			finally
			{
				if (obj2 != null && Marshal.IsComObject(obj2))
				{
					Marshal.ReleaseComObject(obj2);
				}
				if (obj != null && Marshal.IsComObject(obj))
				{
					Marshal.ReleaseComObject(obj);
				}
			}
		}
		else if (text3 == ".url")
		{
			text = StartupEntryRules.ReadUrl(text);
		}
		if (StartupEntryRules.Auxiliary(text) && text2.IndexOf("--processStart", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			Match match = Regex.Match(text2, "--processStart\\s+(?:\"([^\"]+)\"|([^\\s]+))", RegexOptions.IgnoreCase);
			if (match.Success)
			{
				string text4 = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
				if (!StartupEntryRules.Auxiliary(text4))
				{
					text = Path.Combine(Path.GetDirectoryName(text) ?? "", text4);
				}
			}
		}
		entry.LaunchTarget = text;
		entry.LaunchArguments = text2;
		entry.IsFolder = entry.IsFolder || Directory.Exists(text);
		try
		{
			if (Path.GetExtension(text ?? "").Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(text))
			{
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(text);
				entry.ProductInfo = versionInfo.ProductName + " " + versionInfo.FileDescription;
			}
		}
		catch
		{
		}
	}

	public static bool Primary(Entry entry)
	{
		if (entry.IsFolder || !StartupEntryRules.Candidate(entry.Path) || StartupEntryRules.Auxiliary(entry.LaunchTarget))
		{
			return false;
		}
		string launchTarget = entry.LaunchTarget;
		if (string.IsNullOrWhiteSpace(launchTarget))
		{
			return true;
		}
		if (Directory.Exists(launchTarget))
		{
			return false;
		}
		if (Path.GetExtension(launchTarget).ToLowerInvariant().Length != 0 && !StartupEntryRules.GameUri(launchTarget))
		{
			if (EntryRules.Importable(launchTarget))
			{
				return !DocumentRules.Supported(launchTarget);
			}
			return false;
		}
		return true;
	}
}
