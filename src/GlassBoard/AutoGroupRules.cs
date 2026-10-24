using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GlassBoard;

public static class AutoGroupRules
{
	private static List<string[]> catalog = new List<string[]>();

	private static bool loaded;

	private static readonly object gate = new object();

	private static bool Letter(char c)
	{
		if (c >= 'a')
		{
			return c <= 'z';
		}
		return false;
	}

	private static bool Match(string text, string term)
	{
		int num = 0;
		while (num < text.Length)
		{
			int num2 = text.IndexOf(term, num, StringComparison.Ordinal);
			if (num2 < 0)
			{
				return false;
			}
			int num3 = num2 + term.Length;
			if ((!Letter(term[0]) || num2 == 0 || !Letter(text[num2 - 1])) && (!Letter(term[term.Length - 1]) || num3 == text.Length || !Letter(text[num3])))
			{
				return true;
			}
			num = num2 + 1;
		}
		return false;
	}

	public static string Canonical(string name)
	{
		return new string((name ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(char.IsLetterOrDigit)
			.ToArray());
	}

	private static string Words(string name)
	{
		string text = new string((from c in (name ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant()
			select (!char.IsLetterOrDigit(c)) ? ' ' : c).ToArray());
		return string.Join(" ", text.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
	}

	public static string Identity(Entry a)
	{
		string text = Canonical(StartupEntryRules.Stem(a.LaunchTarget));
		return Canonical((string.IsNullOrWhiteSpace(a.LaunchTarget) || !string.IsNullOrWhiteSpace(a.LaunchArguments) || StartupEntryRules.GameUri(a.LaunchTarget) || text == "launcher" || text == "game") ? a.Name : text);
	}

	public static int LoadCatalog(IEnumerable<string> lines)
	{
		List<string[]> list = new List<string[]>();
		foreach (string line in lines)
		{
			if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
			{
				continue;
			}
			string[] array = line.Split('\t');
			if (array.Length != 3)
			{
				continue;
			}
			string text = array[0].Trim();
			string text2 = array[1].Trim();
			if (text.Length == 0 || text == "全部" || text == "未分组" || (text2 != "word" && text2 != "exact"))
			{
				continue;
			}
			string[] array2 = array[2].Split('|');
			foreach (string name in array2)
			{
				string text3 = Canonical(name);
				if (text3.Length > 0)
				{
					list.Add(new string[4]
					{
						text,
						text2,
						text3,
						Words(name)
					});
				}
			}
		}
		lock (gate)
		{
			catalog = (from e in list
				orderby e[1] == "exact" descending, e[2].Length descending
				select e).ToList();
			loaded = true;
		}
		return list.Count;
	}

	public static void ReloadCatalog()
	{
		string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "AppGroups.tsv");
		try
		{
			LoadCatalog(File.ReadLines(path));
		}
		catch
		{
			lock (gate)
			{
				catalog = new List<string[]>();
				loaded = true;
			}
		}
	}

	public static string Guess(Entry entry, Preferences prefs)
	{
		if (entry != null && entry.IsFolder)
		{
			return null;
		}
		if (prefs != null && prefs.LearnedGroups != null && prefs.LearnedGroups.TryGetValue(Identity(entry), out var value))
		{
			if (!string.IsNullOrEmpty(value))
			{
				return value;
			}
			return null;
		}
		return Guess(entry);
	}

	public static string Guess(Entry entry)
	{
		if (entry == null || entry.IsFolder || StartupEntryRules.Auxiliary(entry.Path) || StartupEntryRules.Auxiliary(entry.LaunchTarget))
		{
			return null;
		}
		if (Path.GetExtension(entry.Path ?? "").ToLowerInvariant().Length > 0 && !EntryRules.Importable(entry.Path))
		{
			return null;
		}
		if (!loaded)
		{
			ReloadCatalog();
		}
		string[] source = new string[3]
		{
			Canonical(entry.Name),
			Canonical(StartupEntryRules.Stem(entry.Path)),
			Canonical(StartupEntryRules.Stem(entry.LaunchTarget))
		};
		string[] source2 = new string[4]
		{
			Words(entry.Name),
			Words(StartupEntryRules.Stem(entry.Path)),
			Words(StartupEntryRules.Stem(entry.LaunchTarget)),
			Words(entry.ProductInfo)
		};
		string text = Canonical(entry.ProductInfo);
		List<string[]> list;
		lock (gate)
		{
			list = catalog;
		}
		foreach (string[] rule in list)
		{
			bool num;
			if (!(rule[1] == "exact"))
			{
				if (source.Any((string t) => Match(t, rule[2])) || Match(text, rule[2]))
				{
					goto IL_01a4;
				}
				num = source2.Any((string t) => Match(t, rule[3]));
			}
			else
			{
				num = source.Any((string t) => t == rule[2]);
			}
			if (!num)
			{
				continue;
			}
			goto IL_01a4;
			IL_01a4:
			return rule[0];
		}
		string text2 = (entry.Name + " " + StartupEntryRules.Leaf(entry.Path) + " " + StartupEntryRules.Leaf(entry.LaunchTarget) + " " + entry.ProductInfo).ToLowerInvariant();
		string[][] array = new string[8][]
		{
			new string[12]
			{
				"系统工具", "uninstall", "卸载", "winrar", "7-zip", "7zip", "todesk", "clash", "powershell", "任务管理器",
				"注册表", "控制面板"
			},
			new string[17]
			{
				"音乐", "qqmusic", "qq音乐", "网易云音乐", "cloudmusic", "酷狗", "酷我", "spotify", "foobar", "pro tools",
				"protools", "ableton", "audition", "fl studio", "reaper", "cubase", "asio4all"
			},
			new string[12]
			{
				"开发", "visual studio", "vscode", "code", "pycharm", "intellij", "git", "python", "unity", "unreal",
				"codex", "trae"
			},
			new string[22]
			{
				"创作", "photoshop", "after effects", "premiere", "illustrator", "lightroom", "indesign", "blender", "maya", "3ds max",
				"3dmax", "cinema 4d", "c4d", "marmoset", "substance", "davinci", "resolve", "capcut", "剪映", "canva",
				"houdini", "绘画"
			},
			new string[11]
			{
				"游戏", "steam", "epic games", "wegame", "ubisoft", "battle.net", "rockstar", "5e", "游戏", "对战平台",
				"战网"
			},
			new string[13]
			{
				"办公", "word", "excel", "powerpoint", "onenote", "outlook", "wps", "acrobat", "foxit", "notion",
				"obsidian", "飞书", "钉钉"
			},
			new string[8] { "浏览器", "chrome", "msedge", "edge", "firefox", "opera", "brave", "浏览器" },
			new string[8] { "社交", "qq", "微信", "wechat", "kook", "discord", "telegram", "tt语音" }
		};
		foreach (string[] array2 in array)
		{
			for (int num3 = 1; num3 < array2.Length; num3++)
			{
				if (Match(text2, array2[num3]))
				{
					return array2[0];
				}
			}
		}
		if (StartupEntryRules.GameUri(entry.LaunchTarget) || StartupEntryRules.GameUri(entry.LaunchArguments))
		{
			return "游戏";
		}
		if ((entry.LaunchTarget ?? entry.Path ?? "").Replace('\\', '/').ToLowerInvariant().Contains("/steamapps/common/"))
		{
			return "游戏";
		}
		return null;
	}

	public static int ApplyUnassigned(Preferences prefs)
	{
		int num = 0;
		foreach (Entry app in prefs.Apps)
		{
			if (!(FeatureRules.GroupName(app) != "未分组"))
			{
				string text = Guess(app, prefs);
				if (text != null)
				{
					app.Group = text;
					num++;
				}
			}
		}
		prefs.Groups = FeatureRules.Groups(prefs.Groups, prefs.Apps);
		return num;
	}
}
