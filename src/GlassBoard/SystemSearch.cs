using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace GlassBoard;

public class SystemSearchResult
{
	public string Name;
	public string Path;
	public bool IsFolder;
}

/// <summary>
/// 系统级搜索：开始菜单快捷方式 + 注册表已安装应用 + App Paths（即时），
/// 用户目录/程序目录/其他磁盘的深度文件扫描（后台流式，可取消）。
/// </summary>
public static class SystemSearch
{
	private static readonly string[] PriorityExtensions = new string[15]
	{
		".exe", ".lnk", ".url", ".appref-ms", ".msi", ".bat", ".cmd", ".zip", ".rar", ".7z", ".pdf", ".docx", ".xlsx", ".pptx", ".txt"
	};

	private static IEnumerable<string> StartMenuDirs()
	{
		yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
		yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
	}

	private static bool Matches(string name, string path, string[] words)
	{
		foreach (string word in words)
		{
			if (name.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)
			{
				return false;
			}
		}
		return true;
	}

	private static void TryAdd(List<SystemSearchResult> list, Dictionary<string, bool> seen, string name, string path, bool isFolder, string[] words)
	{
		if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
		{
			return;
		}
		if (!Matches(name, path, words))
		{
			return;
		}
		string key = path.ToLowerInvariant();
		if (seen.ContainsKey(key))
		{
			return;
		}
		seen[key] = true;
		list.Add(new SystemSearchResult
		{
			Name = name,
			Path = path,
			IsFolder = isFolder
		});
	}

	private static void CollectShortcuts(string dir, string[] words, List<SystemSearchResult> list, Dictionary<string, bool> seen, int depth)
	{
		if (depth > 3 || list.Count >= 150 || !Directory.Exists(dir))
		{
			return;
		}
		try
		{
			foreach (string file in Directory.EnumerateFiles(dir))
			{
				if (list.Count >= 150)
				{
					return;
				}
				string ext = Path.GetExtension(file).ToLowerInvariant();
				if (ext == ".lnk" || ext == ".url" || ext == ".appref-ms")
				{
					TryAdd(list, seen, Path.GetFileNameWithoutExtension(file), file, false, words);
				}
			}
			foreach (string sub in Directory.EnumerateDirectories(dir))
			{
				CollectShortcuts(sub, words, list, seen, depth + 1);
			}
		}
		catch
		{
		}
	}

	private static void CollectAppPaths(RegistryKey root, string[] words, List<SystemSearchResult> list, Dictionary<string, bool> seen)
	{
		using (RegistryKey key = root.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths"))
		{
			if (key == null)
			{
				return;
			}
			foreach (string name in key.GetSubKeyNames())
			{
				if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				using (RegistryKey sub = key.OpenSubKey(name))
				{
					string path = ((sub == null) ? null : (sub.GetValue(null) as string));
					if (!string.IsNullOrWhiteSpace(path))
					{
						TryAdd(list, seen, Path.GetFileNameWithoutExtension(name), path, false, words);
					}
				}
			}
		}
	}

	private static void CollectUninstall(RegistryKey root, string[] words, List<SystemSearchResult> list, Dictionary<string, bool> seen)
	{
		foreach (string subPath in new string[3] { "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall" })
		{
			using (RegistryKey key = root.OpenSubKey(subPath))
			{
				if (key == null)
				{
					continue;
				}
				foreach (string name in key.GetSubKeyNames())
				{
					using (RegistryKey sub = key.OpenSubKey(name))
					{
						if (sub == null)
						{
							continue;
						}
						string displayName = sub.GetValue("DisplayName") as string;
						object systemComponent = sub.GetValue("SystemComponent");
						if (string.IsNullOrWhiteSpace(displayName) || sub.GetValue("ParentKeyName") != null)
						{
							continue;
						}
						if (systemComponent is int num && num == 1)
						{
							continue;
						}
						string displayIcon = sub.GetValue("DisplayIcon") as string;
						string target = null;
						if (!string.IsNullOrWhiteSpace(displayIcon))
						{
							string iconPath = displayIcon.Split(',')[0].Trim('"');
							if (File.Exists(iconPath))
							{
								target = iconPath;
							}
						}
						if (target == null)
						{
							string location = sub.GetValue("InstallLocation") as string;
							if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
							{
								target = location;
							}
						}
						if (target != null)
						{
							TryAdd(list, seen, displayName, target, Directory.Exists(target), words);
						}
					}
				}
			}
		}
	}

	/// <summary>即时结果（开始菜单 + 注册表应用），不访问文件系统深层。</summary>
	public static List<SystemSearchResult> Instant(string query)
	{
		List<SystemSearchResult> list = new List<SystemSearchResult>();
		Dictionary<string, bool> seen = new Dictionary<string, bool>();
		if (string.IsNullOrWhiteSpace(query))
		{
			return list;
		}
		string[] words = query.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string dir in StartMenuDirs())
		{
			CollectShortcuts(dir, words, list, seen, 0);
		}
		try
		{
			CollectAppPaths(Registry.LocalMachine, words, list, seen);
		}
		catch
		{
		}
		try
		{
			CollectAppPaths(Registry.CurrentUser, words, list, seen);
		}
		catch
		{
		}
		try
		{
			CollectUninstall(Registry.LocalMachine, words, list, seen);
		}
		catch
		{
		}
		try
		{
			CollectUninstall(Registry.CurrentUser, words, list, seen);
		}
		catch
		{
		}
		list.Sort((SystemSearchResult a, SystemSearchResult b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		return list;
	}

	private static IEnumerable<string> DeepRoots()
	{
		foreach (string folder in new string[6]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
			Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
			Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
			Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
			Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
			GetDownloads()
		})
		{
			yield return folder;
		}
		yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		foreach (string extra in ExtraDriveRoots())
		{
			yield return extra;
		}
	}

	private static List<string> ExtraDriveRoots()
	{
		List<string> roots = new List<string>();
		try
		{
			string systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
			foreach (DriveInfo drive in DriveInfo.GetDrives())
			{
				if (drive.DriveType == DriveType.Fixed && drive.IsReady)
				{
					string root = drive.RootDirectory.FullName;
					if (!string.Equals(root, systemDrive, StringComparison.OrdinalIgnoreCase))
					{
						roots.Add(root);
					}
				}
			}
		}
		catch
		{
		}
		return roots;
	}

	private static string GetDownloads()
	{
		try
		{
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
		}
		catch
		{
			return "";
		}
	}

	/// <summary>深度扫描（流式分批回调，最多约 120 条；ct 取消即停）。</summary>
	public static void Deep(string query, Action<List<SystemSearchResult>> batch, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(query))
		{
			return;
		}
		string[] words = query.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
		Dictionary<string, bool> seen = new Dictionary<string, bool>();
		List<SystemSearchResult> found = new List<SystemSearchResult>();
		List<SystemSearchResult> pending = new List<SystemSearchResult>();
		int lastFlush = Environment.TickCount;
		int total = 0;
		int maxPerRoot = 80;
		foreach (string root in DeepRoots())
		{
			if (ct.IsCancellationRequested || total >= 120)
			{
				break;
			}
			if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
			{
				continue;
			}
			int rootCount = 0;
			Stack<Tuple<string, int>> stack = new Stack<Tuple<string, int>>();
			stack.Push(new Tuple<string, int>(root, 0));
			while (stack.Count > 0)
			{
				if (ct.IsCancellationRequested || total >= 120 || rootCount >= maxPerRoot)
				{
					break;
				}
				Tuple<string, int> current = stack.Pop();
				string dir = current.Item1;
				int depth = current.Item2;
				if (depth > 5)
				{
					continue;
				}
				try
				{
					foreach (string sub in Directory.EnumerateDirectories(dir))
					{
						if (ct.IsCancellationRequested || total >= 120 || rootCount >= maxPerRoot)
						{
							break;
						}
						try
						{
							string name = Path.GetFileName(sub);
							if (Matches(name, sub, words))
							{
								if (!seen.ContainsKey(sub.ToLowerInvariant()))
								{
									seen[sub.ToLowerInvariant()] = true;
									pending.Add(new SystemSearchResult
									{
										Name = name,
										Path = sub,
										IsFolder = true
									});
								}
							}
							if (depth < 4)
							{
								stack.Push(new Tuple<string, int>(sub, depth + 1));
							}
						}
						catch
						{
						}
					}
					foreach (string file in Directory.EnumerateFiles(dir))
					{
						if (ct.IsCancellationRequested || total >= 120 || rootCount >= maxPerRoot)
						{
							break;
						}
						try
						{
							string name = Path.GetFileName(file);
							if (!Matches(name, file, words))
							{
								continue;
							}
							string lower = file.ToLowerInvariant();
							if (seen.ContainsKey(lower))
							{
								continue;
							}
							seen[lower] = true;
							pending.Add(new SystemSearchResult
							{
								Name = name,
								Path = file,
								IsFolder = false
							});
						}
						catch
						{
						}
					}
				}
				catch
				{
					continue;
				}
				rootCount += pending.Count;
				if ((Environment.TickCount - lastFlush > 150 || pending.Count >= 25) && pending.Count > 0)
				{
					total += pending.Count;
					List<SystemSearchResult> sorted = pending.OrderByDescending((SystemSearchResult r) => Rank(r)).ThenBy((SystemSearchResult r) => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
					found.AddRange(sorted);
					pending.Clear();
					batch(sorted);
					lastFlush = Environment.TickCount;
				}
			}
		}
		if (!ct.IsCancellationRequested && pending.Count > 0)
		{
			List<SystemSearchResult> sorted = pending.OrderByDescending((SystemSearchResult r) => Rank(r)).ThenBy((SystemSearchResult r) => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
			batch(sorted);
		}
	}

	private static int Rank(SystemSearchResult r)
	{
		if (r.IsFolder)
		{
			return 2;
		}
		string ext = Path.GetExtension(r.Path).ToLowerInvariant();
		return PriorityExtensions.Contains(ext) ? 1 : 0;
	}

	/// <summary>打开结果：文件夹用资源管理器，文件按默认程序运行。</summary>
	public static void Open(SystemSearchResult r)
	{
		if (r == null)
		{
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo(r.Path)
			{
				UseShellExecute = true
			});
		}
		catch
		{
		}
	}
}
