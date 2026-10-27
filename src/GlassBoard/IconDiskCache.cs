using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

// 图标永久磁盘缓存：首次提取后把 256px PNG 写入 %LocalAppData%\GlassBoard\icons\，
// 之后每次启动直接加载，不再重复调用 Shell——「一次加载、永久启用」。
// 缓存键含文件修改时间+大小指纹，源文件变化时自动换键、重新提取。
internal static class IconDiskCache
{
	private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBoard", "icons");

	internal static string Name(string key)
	{
		using (SHA1 sha = SHA1.Create())
		{
			// v2 前缀：升级提取尺寸/通道后旧缓存自动失效（重新提取，不读旧的低清 PNG）
			byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes("v2|" + key.ToLowerInvariant()));
			StringBuilder sb = new StringBuilder(hash.Length * 2);
			foreach (byte b in hash)
			{
				sb.Append(b.ToString("x2"));
			}
			return sb.ToString();
		}
	}

	internal static ImageSource Load(string cacheName)
	{
		try
		{
			string file = Path.Combine(Dir, cacheName + ".png");
			if (File.Exists(file))
			{
				BitmapImage bitmap = new BitmapImage();
				bitmap.BeginInit();
				bitmap.CacheOption = BitmapCacheOption.OnLoad;
				bitmap.UriSource = new Uri(file);
				bitmap.EndInit();
				bitmap.Freeze();
				return bitmap;
			}
		}
		catch
		{
		}
		return null;
	}

	internal static void Save(string cacheName, BitmapSource bitmap)
	{
		try
		{
			Directory.CreateDirectory(Dir);
			string file = Path.Combine(Dir, cacheName + ".png");
			PngBitmapEncoder encoder = new PngBitmapEncoder();
			encoder.Frames.Add(BitmapFrame.Create(bitmap));
			using (FileStream stream = File.Create(file))
			{
				encoder.Save(stream);
			}
		}
		catch
		{
		}
	}

	// 文件内容指纹：不存在（命名空间路径 ::{…}）时返回空串
	internal static string FileFingerprint(string path)
	{
		try
		{
			FileInfo info = new FileInfo(path);
			if (info.Exists)
			{
				return info.LastWriteTimeUtc.Ticks + "|" + info.Length;
			}
		}
		catch
		{
		}
		return "";
	}
}
