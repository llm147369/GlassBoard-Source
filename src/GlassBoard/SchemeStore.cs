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
		// 灵动岛自定义背景图片：嵌入方案内，导出/导入不依赖可能失效的本地路径。
		if (data.Island != null && !string.IsNullOrWhiteSpace(data.Island.CustomImage) && File.Exists(data.Island.CustomImage))
		{
			byte[] array = File.ReadAllBytes(data.Island.CustomImage);
			if (array.Length <= 524288)
			{
				string text = "island-bg" + Path.GetExtension(data.Island.CustomImage).ToLowerInvariant();
				data.Icons[text] = Convert.ToBase64String(array);
				data.Island.CustomImage = "[asset]" + text;
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
		// 灵动岛背景图片物化到应用管理的本地目录。
		if (data.Island != null && data.Island.CustomImage != null && data.Island.CustomImage.StartsWith("[asset]", StringComparison.Ordinal))
		{
			string key = data.Island.CustomImage.Substring(7);
			if (data.Icons.TryGetValue(key, out var value) && value.Length <= 700000)
			{
				byte[] array = Convert.FromBase64String(value);
				if (array.Length > 524288)
				{
					throw new InvalidDataException("方案背景图片过大");
				}
				string dir = Path.Combine(Store.Folder, "island");
				Directory.CreateDirectory(dir);
				string ext = Path.GetExtension(key);
				if (string.IsNullOrEmpty(ext))
				{
					ext = ".png";
				}
				string text = Path.Combine(dir, "bg-" + Guid.NewGuid().ToString("N") + ext.ToLowerInvariant());
				File.WriteAllBytes(text, array);
				data.Island.CustomImage = text;
			}
			else
			{
				data.Island.CustomImage = "";
			}
		}
	}
}
