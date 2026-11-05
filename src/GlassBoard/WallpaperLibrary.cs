using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace GlassBoard;

// 本地壁纸图库：%LocalAppData%\GlassBoard\Wallpapers\（用户导入）与 Wallpapers\Official\（官方图库下载）。
// 导入按内容 sha256 去重（文件名前缀），并预生成缩略图供设置页列表使用。
// 只负责文件管理：不写注册表、不碰系统壁纸（那部分在 WallpaperCore）。
internal static class WallpaperLibrary
{
	public static readonly string Folder = Path.Combine(Store.Folder, "Wallpapers");
	public static readonly string OfficialFolder = Path.Combine(Folder, "Official");
	private static readonly string ThumbFolder = Path.Combine(Folder, ".thumbs");

	public const long MaxBytes = 31457280L;

	private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };

	public static bool Supported(string path)
	{
		return !string.IsNullOrWhiteSpace(path) && Extensions.Contains(Path.GetExtension(path));
	}

	// 导入：校验格式与大小 → 内容去重 → 复制入库 → 生成缩略图。失败返回 null（原因写入 error.log）。
	public static string Import(string source, string displayName, bool official)
	{
		try
		{
			if (!Supported(source) || !File.Exists(source))
			{
				return null;
			}
			FileInfo info = new FileInfo(source);
			if (info.Length < 8 || info.Length > MaxBytes)
			{
				return null;
			}
			byte[] bytes = File.ReadAllBytes(source);
			if (!LooksLikeImage(bytes))
			{
				return null;
			}
			string hash = UpdateRules.Hash(bytes);
			string dir = official ? OfficialFolder : Folder;
			Directory.CreateDirectory(dir);
			// 同名内容去重：库内已有相同 sha256 的文件则直接复用
			string prefix = hash.Substring(0, 12);
			string existing = Directory.GetFiles(dir, prefix + "_*").FirstOrDefault();
			if (existing != null)
			{
				EnsureThumb(existing);
				return existing;
			}
			string name = Sanitize(string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(source) : displayName);
			string dest = Path.Combine(dir, prefix + "_" + name + Path.GetExtension(source).ToLowerInvariant());
			File.WriteAllBytes(dest, bytes);
			EnsureThumb(dest);
			return dest;
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			return null;
		}
	}

	// 库内全部壁纸文件（新到旧）。
	public static List<string> Entries()
	{
		List<string> list = new List<string>();
		foreach (string dir in new string[2] { Folder, OfficialFolder })
		{
			if (!Directory.Exists(dir))
			{
				continue;
			}
			list.AddRange(Directory.GetFiles(dir).Where(Supported));
		}
		return list.OrderByDescending((string f) => File.GetLastWriteTimeUtc(f)).ToList();
	}

	// 删除库内条目；若该图是当前壁纸，调用方需先还原系统壁纸再删除。
	public static bool Delete(string path)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path) || !IsManaged(path) || !File.Exists(path))
			{
				return false;
			}
			File.Delete(path);
			string thumb = ThumbPath(path);
			if (File.Exists(thumb))
			{
				File.Delete(thumb);
			}
			return true;
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			return false;
		}
	}

	// 路径是否位于应用壁纸库内（防误删用户其它文件）。
	public static bool IsManaged(string path)
	{
		try
		{
			string full = Path.GetFullPath(path);
			string root = Path.GetFullPath(Folder);
			return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	// 是否官方图库下载的壁纸（决定删除后的重下入口标记）。
	public static bool IsOfficial(string path)
	{
		try
		{
			string full = Path.GetFullPath(path);
			string root = Path.GetFullPath(OfficialFolder);
			return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	// 缩略图（最长边 240px JPEG）；生成失败返回 null，调用方降级为无图。
	public static string Thumb(string path)
	{
		if (!IsManaged(path) || !File.Exists(path))
		{
			return null;
		}
		return EnsureThumb(path);
	}

	private static string EnsureThumb(string path)
	{
		string thumb = ThumbPath(path);
		if (File.Exists(thumb))
		{
			return thumb;
		}
		try
		{
			Directory.CreateDirectory(ThumbFolder);
			using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			using (Image image = Image.FromStream(fs))
			{
				int w = image.Width;
				int h = image.Height;
				if (w < 1 || h < 1)
				{
					return null;
				}
				double scale = Math.Min(1.0, 240.0 / Math.Max(w, h));
				int tw = Math.Max(1, (int)Math.Round(w * scale));
				int th = Math.Max(1, (int)Math.Round(h * scale));
				using (Bitmap bmp = new Bitmap(tw, th))
				using (Graphics g = Graphics.FromImage(bmp))
				{
					g.InterpolationMode = InterpolationMode.HighQualityBicubic;
					g.PixelOffsetMode = PixelOffsetMode.HighQuality;
					g.DrawImage(image, 0, 0, tw, th);
					bmp.Save(thumb, ImageFormat.Jpeg);
				}
			}
			return thumb;
		}
		catch
		{
			try
			{
				if (File.Exists(thumb))
				{
					File.Delete(thumb);
				}
			}
			catch
			{
			}
			return null;
		}
	}

	private static string ThumbPath(string path)
	{
		return Path.Combine(ThumbFolder, Path.GetFileNameWithoutExtension(path) + ".jpg");
	}

	private static bool LooksLikeImage(byte[] b)
	{
		if (b == null || b.Length < 8)
		{
			return false;
		}
		if (b[0] == 0xFF && b[1] == 0xD8)
		{
			return true;
		}
		if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
		{
			return true;
		}
		return b[0] == 0x42 && b[1] == 0x4D;
	}

	private static string Sanitize(string name)
	{
		string text = name ?? "wallpaper";
		foreach (char c in Path.GetInvalidFileNameChars())
		{
			text = text.Replace(c, '_');
		}
		text = text.Trim();
		if (text.Length > 40)
		{
			text = text.Substring(0, 40);
		}
		return string.IsNullOrWhiteSpace(text) ? "wallpaper" : text;
	}
}
