using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace GlassBoard;

// 桌面图标自动识别：监视用户桌面 + 公共桌面，出现新的快捷方式/程序时自动加入启动台（大面板）。
public static class DesktopWatcher
{
	private static readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();

	private static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static DispatcherTimer flush;

	private static Controller controller;

	private static bool started;

	// 程序类扩展名走常规导入（manual:false，保留辅助进程过滤）；
	// 图片/文档/文件夹等非程序类强制加入（绕过 LaunchInfoService.Primary 过滤）。
	private static readonly string[] ProgramExtensions = new string[6] { ".lnk", ".url", ".exe", ".appref-ms", ".bat", ".cmd" };

	private static readonly string[] TempExtensions = new string[4] { ".tmp", ".crdownload", ".part", ".partial" };

	public static void Start(Controller c)
	{
		if (started)
		{
			return;
		}
		started = true;
		controller = c;
		flush = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(2.5)
		};
		flush.Tick += (object s, EventArgs e) =>
		{
			flush.Stop();
			Flush();
		};
		string[] dirs = new string[2]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
			Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
		};
		foreach (string dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
			{
				continue;
			}
			try
			{
				FileSystemWatcher watcher = new FileSystemWatcher(dir)
				{
					NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
					EnableRaisingEvents = false
				};
				watcher.Created += (object s, FileSystemEventArgs e) => Queue(e.FullPath);
				watcher.Renamed += (object s, RenamedEventArgs e) => Queue(e.FullPath);
				watcher.EnableRaisingEvents = true;
				watchers.Add(watcher);
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
		}
	}

	private static void Queue(string path)
	{
		try
		{
			// 临时下载文件不收录，其余新文件/新文件夹一律排队（2.5s 防抖）
			string ext = Path.GetExtension(path);
			if (TempExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
			{
				return;
			}
			if (!File.Exists(path) && !Directory.Exists(path))
			{
				return;
			}
			lock (pending)
			{
				pending.Add(path);
			}
			flush.Stop();
			flush.Start();
		}
		catch
		{
		}
	}

	private static void Flush()
	{
		string[] paths;
		lock (pending)
		{
			paths = pending.ToArray();
			pending.Clear();
		}
		if (paths.Length == 0 || controller == null || !controller.Prefs.AutoDesktopImport)
		{
			return;
		}
		System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
		{
			try
			{
				string[] existing = paths.Where((string p) => File.Exists(p) || Directory.Exists(p)).ToArray();
				string[] programs = existing.Where((string p) => ProgramExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToArray();
				string[] others = existing.Where((string p) => !ProgramExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToArray();
				if (programs.Length > 0)
				{
					controller.Add(programs, manual: false, targetGroup: null);
				}
				if (others.Length > 0)
				{
					controller.Add(others, manual: true, targetGroup: null);
				}
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
		}));
	}
}
