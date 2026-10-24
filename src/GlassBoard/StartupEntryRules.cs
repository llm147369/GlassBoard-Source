using System;
using System.IO;
using System.Linq;

namespace GlassBoard;

public static class StartupEntryRules
{
	public static string Leaf(string path)
	{
		path = (path ?? "").Replace('\\', '/');
		int num = path.LastIndexOf('/');
		if (num < 0)
		{
			return path;
		}
		return path.Substring(num + 1);
	}

	public static string Stem(string path)
	{
		string text = Leaf(path);
		int num = text.LastIndexOf('.');
		if (num <= 0)
		{
			return text;
		}
		return text.Substring(0, num);
	}

	public static bool Auxiliary(string path)
	{
		string name = AutoGroupRules.Canonical(Stem(path));
		if (name.StartsWith("unins") || name.StartsWith("uninstall") || name.Contains("卸载") || name.EndsWith("updater") || name.Contains("crashreport") || name.Contains("crashpad"))
		{
			return true;
		}
		return new string[28]
		{
			"update", "updater", "autoupdate", "setup", "install", "installer", "crashreporter", "crashreportclient", "crashpadhandler", "cefsubprocess",
			"qtwebengineprocess", "unitycrashhandler64", "unitycrashhandler32", "werfault", "vc_redist", "vcredist", "dxsetup", "dxwebsetup", "dxdiag", "readme",
			"license", "help", "说明", "帮助", "repair", "修复", "reportbug", "feedback"
		}.Any((string x) => name == AutoGroupRules.Canonical(x) || name.StartsWith(AutoGroupRules.Canonical(x) + "64") || name.StartsWith(AutoGroupRules.Canonical(x) + "32"));
	}

	public static bool GameUri(string value)
	{
		value = (value ?? "").Trim().ToLowerInvariant();
		if (!value.StartsWith("steam://rungameid/") && !value.StartsWith("steam://run/") && !value.StartsWith("com.epicgames.launcher://apps/") && !value.StartsWith("uplay://launch/") && !value.StartsWith("origin://launchgame/"))
		{
			return value.StartsWith("battlenet://");
		}
		return true;
	}

	public static bool Candidate(string path)
	{
		if (!EntryRules.Importable(path) || DocumentRules.Supported(path) || Auxiliary(path))
		{
			return false;
		}
		if (!(Path.GetExtension(path).ToLowerInvariant() != ".url"))
		{
			return GameUri(ReadUrl(path));
		}
		return true;
	}

	public static string ReadUrl(string path)
	{
		try
		{
			FileInfo fileInfo = new FileInfo(path);
			if (!fileInfo.Exists || fileInfo.Length > 32768)
			{
				return "";
			}
			foreach (string item in File.ReadLines(path))
			{
				string text = item.Trim();
				if (text.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
				{
					return text.Substring(4).Trim();
				}
			}
		}
		catch
		{
		}
		return "";
	}
}
