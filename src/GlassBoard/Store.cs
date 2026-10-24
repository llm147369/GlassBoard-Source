using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;

namespace GlassBoard;

public static class Store
{
	public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBoard");

	public static readonly string FileName = Path.Combine(Folder, "layout.json");

	private static readonly object saveLock = new object();

	private static byte[] lastSaved;

	private static string lastInventory;

	private static DateTime lastSnapshot;

	public static string RecoveryMessage { get; private set; }

	public static void Trace(string message)
	{
		try
		{
			Directory.CreateDirectory(Folder);
			string text = Path.Combine(Folder, "session.log");
			if (File.Exists(text) && new FileInfo(text).Length > 1048576)
			{
				File.Move(text, text + "." + DateTime.Now.Ticks);
			}
			File.AppendAllText(text, DateTime.Now.ToString("s") + " [" + Assembly.GetExecutingAssembly().GetName().Version?.ToString() + "] " + message + Environment.NewLine);
		}
		catch
		{
		}
	}

	public static void Log(Exception ex)
	{
		try
		{
			Directory.CreateDirectory(Folder);
			File.AppendAllText(Path.Combine(Folder, "error.log"), DateTime.Now.ToString("s") + " [" + Assembly.GetExecutingAssembly().GetName().Version?.ToString() + "] " + ex?.ToString() + Environment.NewLine);
		}
		catch
		{
		}
	}

	private static byte[] Encode<T>(T value)
	{
		using MemoryStream memoryStream = new MemoryStream();
		new DataContractJsonSerializer(typeof(T)).WriteObject((Stream)memoryStream, (object)value);
		return memoryStream.ToArray();
	}

	public static Preferences ReadLayout(byte[] bytes)
	{
		if (bytes == null || bytes.Length > 8388608)
		{
			throw new InvalidDataException("配置文件过大");
		}
		using MemoryStream stream = new MemoryStream(bytes, writable: false);
		Preferences preferences = (Preferences)new DataContractJsonSerializer(typeof(Preferences)).ReadObject((Stream)stream);
		if (preferences == null || preferences.Apps == null)
		{
			throw new InvalidDataException("配置缺少入口清单");
		}
		return preferences;
	}

	private static void Atomic(string path, byte[] bytes, string backup = null)
	{
		string text = path + ".write-" + Guid.NewGuid().ToString("N");
		try
		{
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				fileStream.Write(bytes, 0, bytes.Length);
				fileStream.Flush(flushToDisk: true);
			}
			if (File.Exists(path))
			{
				File.Replace(text, path, backup);
			}
			else
			{
				File.Move(text, path);
			}
		}
		finally
		{
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch
			{
			}
		}
	}

	private static IEnumerable<string> RecoveryPaths()
	{
		List<string> list = new List<string>
		{
			FileName + ".last-good",
			FileName + ".previous",
			FileName + ".tmp"
		};
		string path = Path.Combine(Folder, "snapshots");
		if (Directory.Exists(path))
		{
			list.AddRange(Directory.GetFiles(path, "layout-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(30));
			list.AddRange(Directory.GetFiles(path, "upgrade-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(20));
		}
		list.AddRange(Directory.GetFiles(Folder, "layout.json.backup-*").Concat(Directory.GetFiles(Folder, "layout.json.before-*")).OrderByDescending(File.GetLastWriteTimeUtc)
			.Take(30));
		return list;
	}

	private static void ArchiveUpgrade(byte[] bytes)
	{
		try
		{
			string text = Path.Combine(Folder, "snapshots");
			Directory.CreateDirectory(text);
			string text2 = UpdateRules.Hash(bytes);
			string path = Path.Combine(text, "upgrade-" + text2 + ".json");
			if (!File.Exists(path))
			{
				Atomic(path, bytes);
			}
		}
		catch (Exception ex)
		{
			Log(ex);
		}
	}

	public static Preferences Load(bool mergeJournal = true)
	{
		lock (saveLock)
		{
			Directory.CreateDirectory(Folder);
			RecoveryMessage = null;
			lastSaved = null;
			bool flag = false;
			Preferences preferences = null;
			byte[] bytes = null;
			if (File.Exists(FileName))
			{
				try
				{
					bytes = File.ReadAllBytes(FileName);
					preferences = ReadLayout(bytes);
				}
				catch (Exception ex)
				{
					flag = true;
					Log(ex);
					try
					{
						File.Copy(FileName, FileName + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), overwrite: false);
					}
					catch
					{
					}
				}
			}
			if (preferences == null)
			{
				foreach (string item in RecoveryPaths())
				{
					if (File.Exists(item))
					{
						try
						{
							bytes = File.ReadAllBytes(item);
							preferences = ReadLayout(bytes);
							RecoveryMessage = "已从安全副本恢复布局。可在设置 → 通用 → 找回旧版入口中检查历史条目。";
						}
						catch (Exception ex2)
						{
							flag = true;
							Log(ex2);
							continue;
						}
						break;
					}
				}
			}
			if (preferences == null)
			{
				if (flag)
				{
					throw new InvalidDataException("配置和安全副本均无法读取，原文件已保留。请使用完整备份恢复，避免覆盖原配置。配置目录：" + Folder);
				}
				preferences = new Preferences();
			}
			else if (preferences.FormatVersion < 2)
			{
				ArchiveUpgrade(bytes);
			}
			if ((RecoveryMessage != null) & mergeJournal)
			{
				MergeRecoveryJournal(preferences);
			}
			preferences.Normalize();
			return preferences;
		}
	}

	private static void MergeRecoveryJournal(Preferences prefs)
	{
		try
		{
			string path = Path.Combine(Folder, "removed.json");
			if (!File.Exists(path))
			{
				return;
			}
			using FileStream stream = File.OpenRead(path);
			RemovalJournal journal = (RemovalJournal)new DataContractJsonSerializer(typeof(RemovalJournal)).ReadObject((Stream)stream);
			if (journal != null)
			{
				journal.Merge(prefs);
				prefs.Apps.RemoveAll((Entry a) => !(journal.ActivePaths ?? new List<string>()).Contains(a.Path, StringComparer.OrdinalIgnoreCase) && RemovalRules.Blocked(prefs, a));
			}
		}
		catch (Exception ex)
		{
			Log(ex);
		}
	}

	public static void Save(Preferences p)
	{
		lock (saveLock)
		{
			Directory.CreateDirectory(Folder);
			p.FormatVersion = 2;
			RemovalRules.ReconcileActive(p);
			byte[] array = Encode(p);
			ReadLayout(array);
			if (lastSaved != null && array.SequenceEqual(lastSaved))
			{
				return;
			}
			string backup = null;
			if (File.Exists(FileName))
			{
				try
				{
					ReadLayout(File.ReadAllBytes(FileName));
					backup = FileName + ".previous";
				}
				catch
				{
				}
			}
			Atomic(FileName, array, backup);
			lastSaved = array;
			try
			{
				Atomic(FileName + ".last-good", array);
			}
			catch (Exception ex)
			{
				Log(ex);
			}
			try
			{
				Atomic(Path.Combine(Folder, "removed.json"), Encode(new RemovalJournal
				{
					Paths = p.Removed.ToList(),
					Identities = p.RemovedIdentities.ToList(),
					ActivePaths = p.Apps.Select((Entry a) => a.Path).ToList()
				}));
			}
			catch (Exception ex2)
			{
				Log(ex2);
			}
			string text = BoardEditState.Capture(p).Signature();
			if (!(text != lastInventory) && !((DateTime.UtcNow - lastSnapshot).TotalSeconds >= 60.0))
			{
				return;
			}
			try
			{
				string text2 = Path.Combine(Folder, "snapshots");
				Directory.CreateDirectory(text2);
				Atomic(Path.Combine(text2, "layout-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json"), array);
				lastSnapshot = DateTime.UtcNow;
				lastInventory = text;
				foreach (string item in Directory.GetFiles(text2, "layout-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(30))
				{
					File.Delete(item);
				}
			}
			catch (Exception ex3)
			{
				Log(ex3);
			}
		}
	}

	private static Preferences ReadBackupLayout(string path)
	{
		using FileStream stream = File.OpenRead(path);
		using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
		List<ZipArchiveEntry> list = zipArchive.Entries.Where((ZipArchiveEntry e) => e.FullName == "layout.json").ToList();
		if (list.Count != 1 || list[0].Length > 8388608)
		{
			throw new InvalidDataException("备份布局不完整或过大");
		}
		using Stream stream2 = list[0].Open();
		using MemoryStream memoryStream = new MemoryStream();
		byte[] array = new byte[16384];
		int num;
		while ((num = stream2.Read(array, 0, array.Length)) > 0)
		{
			if (memoryStream.Length + num > 8388608)
			{
				throw new InvalidDataException("备份布局过大");
			}
			memoryStream.Write(array, 0, num);
		}
		return ReadLayout(memoryStream.ToArray());
	}

	public static List<Entry> HistoricalEntries()
	{
		List<Entry> list = new List<Entry>();
		Directory.CreateDirectory(Folder);
		foreach (string item in RecoveryPaths().ToList().Distinct(StringComparer.OrdinalIgnoreCase))
		{
			try
			{
				if (File.Exists(item))
				{
					Preferences preferences = ReadLayout(File.ReadAllBytes(item));
					list.AddRange(preferences.Apps.Where((Entry a) => a != null && !string.IsNullOrWhiteSpace(a.Path)).Select(FeatureRules.Copy));
				}
			}
			catch
			{
			}
		}
		string path = Path.Combine(Folder, "backups");
		if (Directory.Exists(path))
		{
			foreach (string item2 in Directory.GetFiles(path, "*.gbbackup").OrderByDescending(File.GetLastWriteTimeUtc).Take(20))
			{
				try
				{
					Preferences preferences2 = ReadBackupLayout(item2);
					list.AddRange(preferences2.Apps.Where((Entry a) => a != null && !string.IsNullOrWhiteSpace(a.Path)).Select(FeatureRules.Copy));
				}
				catch
				{
				}
			}
		}
		return EntryRules.NormalizeEntries(list);
	}
}
