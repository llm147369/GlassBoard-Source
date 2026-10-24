using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace GlassBoard;

public static class SchemeStore
{
	public static string Folder => Path.Combine(Store.Folder, "schemes");

	public static string[] Files()
	{
		Directory.CreateDirectory(Folder);
		return Directory.GetFiles(Folder, "*.json");
	}

	public static AppearanceScheme Load(string path)
	{
		if (new FileInfo(path).Length > 5242880)
		{
			throw new InvalidDataException("方案文件超过 5 MB");
		}
		using FileStream stream = File.OpenRead(path);
		AppearanceScheme appearanceScheme = (AppearanceScheme)new DataContractJsonSerializer(typeof(AppearanceScheme)).ReadObject((Stream)stream);
		if (appearanceScheme == null || appearanceScheme.Version != 1 || appearanceScheme.Dock == null)
		{
			throw new InvalidDataException("方案格式不支持");
		}
		return appearanceScheme;
	}

	public static void Save(AppearanceScheme data, string path)
	{
		using FileStream stream = File.Create(path);
		new DataContractJsonSerializer(typeof(AppearanceScheme)).WriteObject((Stream)stream, (object)data);
	}

	public static void Embed(AppearanceScheme data)
	{
		data.Icons = new Dictionary<string, string>();
		int num = 0;
		foreach (Entry item in data.Apps.Where((Entry a) => !string.IsNullOrWhiteSpace(a.IconPath) && File.Exists(a.IconPath)))
		{
			byte[] array = File.ReadAllBytes(item.IconPath);
			if (array.Length <= 524288)
			{
				string text = "icon-" + num++ + ".png";
				data.Icons[text] = Convert.ToBase64String(array);
				item.IconPath = "[asset]" + text;
			}
		}
	}

	public static void Materialize(AppearanceScheme data)
	{
		if (data.Icons == null)
		{
			data.Icons = new Dictionary<string, string>();
		}
		foreach (Entry item in (data.Apps ?? new List<Entry>()).Where((Entry a) => a != null && a.IconPath != null && a.IconPath.StartsWith("[asset]", StringComparison.Ordinal)))
		{
			if (!data.Icons.TryGetValue(item.IconPath.Substring(7), out var value) || value.Length > 700000)
			{
				item.IconPath = null;
				continue;
			}
			byte[] array = Convert.FromBase64String(value);
			if (array.Length > 524288)
			{
				throw new InvalidDataException("方案图标过大");
			}
			Directory.CreateDirectory(Path.Combine(Store.Folder, "icons"));
			string text = Path.Combine(Store.Folder, "icons", Guid.NewGuid().ToString("N") + ".png");
			File.WriteAllBytes(text, array);
			item.IconPath = text;
		}
	}
}
