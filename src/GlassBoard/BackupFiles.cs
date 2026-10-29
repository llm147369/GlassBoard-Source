using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace GlassBoard;

public static class BackupFiles
{
	private const int MaxFile = 8388608;

	private const int MaxTotal = 33554432;

	private static bool Allowed(string name)
	{
		switch (name)
		{
		case "layout.json":
		case "notes.json":
		case "manifest.json":
			return true;
		default:
		{
			bool flag = name.StartsWith("schemes/", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
			bool flag2 = name.StartsWith("icons/", StringComparison.Ordinal) && new string[5] { ".png", ".jpg", ".jpeg", ".ico", ".bmp" }.Contains(Path.GetExtension(name).ToLowerInvariant());
			if (!flag && !flag2)
			{
				return false;
			}
			string text = name.Substring(flag ? 8 : 6);
			if (text.Length > 5 && !text.Contains(".."))
			{
				return text.IndexOfAny(new char[10] { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0' }) < 0;
			}
			return false;
		}
		}
	}

	private static T Json<T>(byte[] bytes)
	{
		using MemoryStream stream = new MemoryStream(bytes);
		return (T)new DataContractJsonSerializer(typeof(T)).ReadObject((Stream)stream);
	}

	private static byte[] Encode<T>(T value)
	{
		using MemoryStream memoryStream = new MemoryStream();
		new DataContractJsonSerializer(typeof(T)).WriteObject((Stream)memoryStream, (object)value);
		return memoryStream.ToArray();
	}

	public static Dictionary<string, byte[]> Read(string path)
	{
		Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		int num = 0;
		using (FileStream stream = File.OpenRead(path))
		{
			using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
			if (zipArchive.Entries.Count > 4096)
			{
				throw new InvalidDataException("备份项目过多");
			}
			foreach (ZipArchiveEntry entry in zipArchive.Entries)
			{
				if (!Allowed(entry.FullName) || dictionary.ContainsKey(entry.FullName) || entry.Length > 8388608)
				{
					throw new InvalidDataException("备份中含有无效项目");
				}
				using Stream stream2 = entry.Open();
				using MemoryStream memoryStream = new MemoryStream();
				byte[] array = new byte[8192];
				int num2;
				while ((num2 = stream2.Read(array, 0, array.Length)) > 0)
				{
					num += num2;
					if (memoryStream.Length + num2 > 8388608 || num > 33554432)
					{
						throw new InvalidDataException("备份数据过大");
					}
					memoryStream.Write(array, 0, num2);
				}
				dictionary.Add(entry.FullName, memoryStream.ToArray());
			}
		}
		Validate(dictionary);
		return dictionary;
	}

	private static void Validate(Dictionary<string, byte[]> data)
	{
		if (!data.ContainsKey("manifest.json") || !data.ContainsKey("layout.json") || !data.ContainsKey("notes.json"))
		{
			throw new InvalidDataException("不是完整的 GlassBoard-Source 备份");
		}
		BackupManifest backupManifest = Json<BackupManifest>(data["manifest.json"]);
		if (backupManifest == null || backupManifest.Version != 1)
		{
			throw new InvalidDataException("不支持的备份版本");
		}
		Preferences preferences = Json<Preferences>(data["layout.json"]);
		NoteBook noteBook = Json<NoteBook>(data["notes.json"]);
		if (preferences == null || noteBook == null)
		{
			throw new InvalidDataException("备份配置为空");
		}
		preferences.Normalize();
		noteBook.Normalize();
		if (backupManifest.Icons != null)
		{
			foreach (KeyValuePair<string, string> icon in backupManifest.Icons)
			{
				if (!icon.Value.StartsWith("icons/", StringComparison.Ordinal) || !Allowed(icon.Value) || !data.ContainsKey(icon.Value))
				{
					throw new InvalidDataException("图标备份引用无效");
				}
			}
		}
		foreach (KeyValuePair<string, byte[]> item in data.Where((KeyValuePair<string, byte[]> x) => x.Key.StartsWith("schemes/")))
		{
			AppearanceScheme appearanceScheme = Json<AppearanceScheme>(item.Value);
			if (appearanceScheme == null || appearanceScheme.Version != 1 || appearanceScheme.Dock == null)
			{
				throw new InvalidDataException("外观方案无效");
			}
		}
	}

	public static void Create(string folder, string destination)
	{
		Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		dictionary["layout.json"] = File.ReadAllBytes(Path.Combine(folder, "layout.json"));
		string path = Path.Combine(folder, "notes.json");
		if (File.Exists(path))
		{
			dictionary["notes.json"] = File.ReadAllBytes(path);
		}
		else
		{
			NoteBook noteBook = new NoteBook();
			noteBook.Normalize();
			dictionary["notes.json"] = Encode(noteBook);
		}
		string path2 = Path.Combine(folder, "schemes");
		if (Directory.Exists(path2))
		{
			string[] files = Directory.GetFiles(path2, "*.json");
			foreach (string path3 in files)
			{
				string text = "schemes/" + Path.GetFileName(path3);
				if (!Allowed(text))
				{
					throw new InvalidDataException("外观方案文件名无效");
				}
				dictionary[text] = File.ReadAllBytes(path3);
			}
		}
		Dictionary<string, string> dictionary2 = new Dictionary<string, string>();
		foreach (Entry item in from a in (Json<Preferences>(dictionary["layout.json"]).Apps ?? new List<Entry>()).Concat(dictionary.Where((KeyValuePair<string, byte[]> item) => item.Key.StartsWith("schemes/")).SelectMany((KeyValuePair<string, byte[]> item) => Json<AppearanceScheme>(item.Value).Apps ?? new List<Entry>()))
			where a != null && !string.IsNullOrWhiteSpace(a.IconPath) && File.Exists(a.IconPath)
			select a)
		{
			string text2 = Path.GetExtension(item.IconPath).ToLowerInvariant();
			if (new string[5] { ".png", ".jpg", ".jpeg", ".ico", ".bmp" }.Contains(text2))
			{
				byte[] array = File.ReadAllBytes(item.IconPath);
				string text3;
				using (SHA256 sHA = SHA256.Create())
				{
					text3 = BitConverter.ToString(sHA.ComputeHash(array)).Replace("-", "").ToLowerInvariant();
				}
				string text4 = "icons/" + text3 + text2;
				dictionary[text4] = array;
				dictionary2[item.IconPath] = text4;
			}
		}
		dictionary["manifest.json"] = Encode(new BackupManifest
		{
			Version = 1,
			AppVersion = "0.9.20.9",
			CreatedUtc = DateTime.UtcNow.ToString("o"),
			Icons = dictionary2
		});
		if (dictionary.Count > 4096 || dictionary.Values.Any((byte[] b) => b.Length > 8388608) || ((IEnumerable<byte[]>)dictionary.Values).Sum((Func<byte[], long>)((byte[] b) => b.Length)) > 33554432)
		{
			throw new InvalidDataException("备份数据过大");
		}
		Validate(dictionary);
		string text5 = destination + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			using (FileStream stream = File.Create(text5))
			{
				using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Create);
				foreach (KeyValuePair<string, byte[]> item2 in dictionary)
				{
					using Stream stream2 = zipArchive.CreateEntry(item2.Key, CompressionLevel.Optimal).Open();
					stream2.Write(item2.Value, 0, item2.Value.Length);
				}
			}
			if (File.Exists(destination))
			{
				File.Replace(text5, destination, null);
			}
			else
			{
				File.Move(text5, destination);
			}
		}
		finally
		{
			if (File.Exists(text5))
			{
				File.Delete(text5);
			}
		}
	}

	private static void Atomic(string path, byte[] bytes)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		string text = path + ".restore-" + Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllBytes(text, bytes);
			if (File.Exists(path))
			{
				File.Replace(text, path, null);
			}
			else
			{
				File.Move(text, path);
			}
		}
		finally
		{
			if (File.Exists(text))
			{
				File.Delete(text);
			}
		}
	}

	public static void Restore(string backup, string folder, Action<string> afterCommit = null)
	{
		Dictionary<string, byte[]> dictionary = Read(backup);
		BackupManifest backupManifest = Json<BackupManifest>(dictionary["manifest.json"]);
		if (backupManifest.Icons != null && backupManifest.Icons.Count > 0)
		{
			Preferences preferences = Json<Preferences>(dictionary["layout.json"]);
			foreach (Entry item in from a in (preferences.Apps ?? new List<Entry>()).Concat(dictionary.Where((KeyValuePair<string, byte[]> item) => item.Key.StartsWith("schemes/")).SelectMany((KeyValuePair<string, byte[]> item) => Json<AppearanceScheme>(item.Value).Apps ?? new List<Entry>()))
				where a != null && !string.IsNullOrEmpty(a.IconPath)
				select a)
			{
				if (backupManifest.Icons.TryGetValue(item.IconPath, out var value))
				{
					item.IconPath = Path.GetFullPath(Path.Combine(folder, value.Replace('/', Path.DirectorySeparatorChar)));
				}
			}
			dictionary["layout.json"] = Encode(preferences);
			string[] array = dictionary.Keys.Where((string k) => k.StartsWith("schemes/", StringComparison.Ordinal)).ToArray();
			foreach (string key in array)
			{
				AppearanceScheme appearanceScheme = Json<AppearanceScheme>(dictionary[key]);
				foreach (Entry item2 in (appearanceScheme.Apps ?? new List<Entry>()).Where((Entry a) => a != null && !string.IsNullOrEmpty(a.IconPath)))
				{
					if (backupManifest.Icons.TryGetValue(item2.IconPath, out var value2))
					{
						item2.IconPath = Path.GetFullPath(Path.Combine(folder, value2.Replace('/', Path.DirectorySeparatorChar)));
					}
				}
				dictionary[key] = Encode(appearanceScheme);
			}
		}
		Directory.CreateDirectory(folder);
		List<string> list = dictionary.Keys.Where((string k) => k != "manifest.json").ToList();
		string path = Path.Combine(folder, "schemes");
		if (Directory.Exists(path))
		{
			list = list.Concat(from f in Directory.GetFiles(path, "*.json")
				select "schemes/" + Path.GetFileName(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		}
		foreach (string item3 in list)
		{
			if (!Allowed(item3))
			{
				throw new InvalidDataException("配置目录包含无效文件名");
			}
			if (Directory.Exists(Path.Combine(folder, item3.Replace('/', Path.DirectorySeparatorChar))))
			{
				throw new IOException("恢复目标被文件夹占用：" + item3);
			}
		}
		string text = Path.Combine(folder, ".restore-" + Guid.NewGuid().ToString("N"));
		bool flag = true;
		List<string> list2 = new List<string>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			foreach (KeyValuePair<string, byte[]> item4 in dictionary.Where((KeyValuePair<string, byte[]> x) => x.Key != "manifest.json"))
			{
				string path2 = Path.Combine(text, "new", item4.Key.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(path2));
				File.WriteAllBytes(path2, item4.Value);
			}
			try
			{
				foreach (string item5 in list)
				{
					string text2 = item5.Replace('/', Path.DirectorySeparatorChar);
					string text3 = Path.Combine(folder, text2);
					string text4 = Path.Combine(text, "old", text2);
					Directory.CreateDirectory(Path.GetDirectoryName(text3));
					if (File.Exists(text3))
					{
						Directory.CreateDirectory(Path.GetDirectoryName(text4));
						File.Move(text3, text4);
						hashSet.Add(item5);
					}
					list2.Add(item5);
					if (dictionary.ContainsKey(item5))
					{
						File.Move(Path.Combine(text, "new", text2), text3);
					}
					afterCommit?.Invoke(item5);
				}
			}
			catch
			{
				Exception ex = null;
				foreach (string item6 in list2.AsEnumerable().Reverse())
				{
					try
					{
						string text5 = item6.Replace('/', Path.DirectorySeparatorChar);
						string text6 = Path.Combine(folder, text5);
						if (File.Exists(text6))
						{
							File.Delete(text6);
						}
						if (hashSet.Contains(item6))
						{
							File.Move(Path.Combine(text, "old", text5), text6);
						}
					}
					catch (Exception ex2)
					{
						if (ex == null)
						{
							ex = ex2;
						}
					}
				}
				if (ex != null)
				{
					flag = false;
					throw new IOException("恢复回滚未完成，原始数据仍保存在：" + text, ex);
				}
				throw;
			}
		}
		finally
		{
			if (flag && Directory.Exists(text))
			{
				try
				{
					Directory.Delete(text, recursive: true);
				}
				catch
				{
				}
			}
		}
	}
}
