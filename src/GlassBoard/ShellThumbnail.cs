using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

// Explorer 同款缩略图（图片/视频/文档等），复用系统缩略图缓存，速度快且不锁文件
public static class ShellThumbnail
{
	private static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>();

	[StructLayout(LayoutKind.Sequential)]
	private struct SIZE
	{
		public int cx;
		public int cy;
	}

	[Flags]
	private enum SIIGBF : uint
	{
		ResizeToFit = 0x0,
		BiggerSizeOk = 0x1,
		MemoryOnly = 0x2,
		IconOnly = 0x4,
		ThumbnailOnly = 0x8,
		InCacheOnly = 0x10
	}

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
	private interface IShellItemImageFactory
	{
		[PreserveSig]
		int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
	private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(IntPtr hObject);

	private static ImageSource Fetch(string path, int size)
	{
		if (!File.Exists(path))
		{
			return null;
		}
		Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
		SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);
		if (factory == null)
		{
			return null;
		}
		SIZE sz = new SIZE { cx = size, cy = size };
		int hr = factory.GetImage(sz, SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk, out IntPtr hbitmap);
		if (hr < 0 || hbitmap == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			src.Freeze();
			return src;
		}
		finally
		{
			DeleteObject(hbitmap);
		}
	}

	public static ImageSource Get(string path, int size)
	{
		string key = path.ToLowerInvariant() + "|" + size;
		if (cache.TryGetValue(key, out ImageSource hit))
		{
			return hit;
		}
		ImageSource src = Fetch(path, size);
		if (cache.Count > 160)
		{
			cache.Clear();
		}
		cache[key] = src;
		return src;
	}
}

// 媒体文件入口：面板磁贴缩略图 + 悬浮预览判定
public static class MediaThumb
{
	private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".svg" };
	private static readonly string[] VideoExt = { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".flv", ".ts" };
	private static readonly Dictionary<string, string> svgPng = new Dictionary<string, string>();

	public static bool IsImage(string path)
	{
		string ext = Path.GetExtension(path);
		foreach (string item in ImageExt)
		{
			if (string.Equals(ext, item, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	public static bool IsVideo(string path)
	{
		string ext = Path.GetExtension(path);
		foreach (string item in VideoExt)
		{
			if (string.Equals(ext, item, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	public static bool IsPreviewable(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
		{
			return false;
		}
		if (!IsImage(path))
		{
			return IsVideo(path);
		}
		return true;
	}

	public static ImageSource LoadFile(string path, int width)
	{
		try
		{
			BitmapImage img = new BitmapImage();
			img.BeginInit();
			img.CacheOption = BitmapCacheOption.OnLoad;
			img.UriSource = new Uri(path);
			img.DecodePixelWidth = width;
			img.EndInit();
			img.Freeze();
			return img;
		}
		catch
		{
			return null;
		}
	}

	private static string SvgRaster(string path)
	{
		if (svgPng.TryGetValue(path, out string cached) && File.Exists(cached))
		{
			return cached;
		}
		string png = SvgIconConverter.TryConvert(path, 256);
		if (png != null)
		{
			svgPng[path] = png;
		}
		return png;
	}

	// SVG 预览：先光栅化为 PNG 再解码
	public static ImageSource SvgPreview(string path, int width)
	{
		string png = SvgRaster(path);
		return ((png == null) ? null : LoadFile(png, width));
	}

	// 磁贴图标：图片/视频直接显示内容缩略图，其余文件沿用原有图标逻辑
	public static ImageSource TileIcon(Entry entry, bool showShortcutOverlays)
	{
		string ext = Path.GetExtension(entry.Path);
		if (string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase))
		{
			string png = SvgRaster(entry.Path);
			ImageSource svg = ((png == null) ? null : LoadFile(png, 192));
			if (svg != null)
			{
				return svg;
			}
		}
		else if (IsImage(entry.Path) || IsVideo(entry.Path))
		{
			ImageSource thumb = ShellThumbnail.Get(entry.Path, 192);
			if (thumb != null)
			{
				return thumb;
			}
		}
		return FeatureUi.Icon(entry, showShortcutOverlays, true);
	}
}
