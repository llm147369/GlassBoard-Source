using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

public static class Native
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct SHFILEINFO
	{
		public IntPtr hIcon;

		public int iIcon;

		public uint attributes;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string displayName;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
		public string typeName;
	}

	public const int RestoreMessage = 32815;

	private static readonly BoundedCache<string, ImageSource> icons = new BoundedCache<string, ImageSource>(64, StringComparer.OrdinalIgnoreCase);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindow(string cls, string title);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostMessage(IntPtr h, int message, IntPtr w, IntPtr l);

	public static bool RequestRestore()
	{
		IntPtr intPtr = FindWindow(null, "GlassBoard Dock");
		if (intPtr != IntPtr.Zero)
		{
			return PostMessage(intPtr, 32815, IntPtr.Zero, IntPtr.Zero);
		}
		return false;
	}

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);

	[DllImport("user32.dll")]
	public static extern bool UnregisterHotKey(IntPtr h, int id);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool SetWindowDisplayAffinity(IntPtr h, uint value);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool GetWindowDisplayAffinity(IntPtr h, out uint value);

	[DllImport("user32.dll")]
	public static extern bool SetForegroundWindow(IntPtr h);

	[DllImport("kernel32.dll")]
	private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

	// 把不在用的内存页还给系统（任务管理器里的内存数字立刻下降；页面按需换回）
	public static void TrimWorkingSet()
	{
		try
		{
			SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SHGetFileInfo(string path, uint attr, out SHFILEINFO info, uint cb, uint flags);

	[DllImport("comctl32.dll")]
	private static extern IntPtr ImageList_GetIcon(IntPtr list, int index, uint flags);

	[DllImport("user32.dll")]
	private static extern bool DestroyIcon(IntPtr h);

	public static ImageSource Icon(string path, bool showShortcutOverlays = false, bool sharper = false)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		string key = (sharper ? "sharp/" : "normal/") + (showShortcutOverlays ? "overlay/" : "plain/") + path;
		if (icons.TryGet(key, out var value))
		{
			return value;
		}
		SHFILEINFO info;
		IntPtr intPtr;
		if (showShortcutOverlays)
		{
			SHGetFileInfo(path, 0u, out info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), sharper ? 16736u : 256u);
			intPtr = info.hIcon;
			if (sharper && intPtr != IntPtr.Zero)
			{
				BitmapSource bitmapSource = ShellIconImages.Read(info.iIcon & 0xFFFFFF, (uint)(info.iIcon >>> 24 << 8));
				if (bitmapSource != null)
				{
					if (intPtr != IntPtr.Zero)
					{
						DestroyIcon(intPtr);
					}
					icons.Put(key, bitmapSource);
					return bitmapSource;
				}
			}
		}
		else
		{
			IntPtr intPtr2 = SHGetFileInfo(path, 0u, out info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 16384u);
			if (sharper && intPtr2 != IntPtr.Zero)
			{
				BitmapSource bitmapSource2 = ShellIconImages.Read(info.iIcon);
				if (bitmapSource2 != null)
				{
					icons.Put(key, bitmapSource2);
					return bitmapSource2;
				}
			}
			intPtr = ((intPtr2 == IntPtr.Zero) ? IntPtr.Zero : ImageList_GetIcon(intPtr2, info.iIcon, 1u));
		}
		if (intPtr == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			BitmapSource bitmapSource3 = Imaging.CreateBitmapSourceFromHIcon(intPtr, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
			bitmapSource3.Freeze();
			icons.Put(key, bitmapSource3);
			return bitmapSource3;
		}
		catch
		{
			return null;
		}
		finally
		{
			DestroyIcon(intPtr);
		}
	}

	public static void ClearIconCache()
	{
		icons.Clear();
	}

	private static ImageSource recycleIcon;
	private static ImageSource computerIcon;

	// 此电脑图标：SHGetFileInfo 解析「此电脑」命名空间 GUID，拿到系统官方图标
	public static ImageSource MyComputerIcon()
	{
		if (computerIcon != null)
		{
			return computerIcon;
		}
		try
		{
			SHFILEINFO info;
			IntPtr hIcon = SHGetFileInfo("::{20D04FE0-3AEA-1069-A2D9-08002B30309D}", 0u, out info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 256u);
			if (hIcon != IntPtr.Zero)
			{
				BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
				bitmap.Freeze();
				DestroyIcon(hIcon);
				computerIcon = bitmap;
				return bitmap;
			}
		}
		catch
		{
		}
		return null;
	}

	// 回收站图标：SHGetFileInfo 直接解析 GUID 命名空间路径，拿到系统回收站图标
	public static ImageSource RecycleIcon()
	{
		if (recycleIcon != null)
		{
			return recycleIcon;
		}
		try
		{
			SHFILEINFO info;
			IntPtr hIcon = SHGetFileInfo(RecycleBin.Clsid, 0u, out info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 256u);
			if (hIcon != IntPtr.Zero)
			{
				BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
				bitmap.Freeze();
				DestroyIcon(hIcon);
				recycleIcon = bitmap;
				return bitmap;
			}
		}
		catch
		{
		}
		return null;
	}

	public static void Computer()
	{
		Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder")
		{
			UseShellExecute = true
		});
	}
}
