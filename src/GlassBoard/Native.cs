using System;
using System.Diagnostics;
using System.IO;
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

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int ExtractIconEx(string file, int index, out IntPtr large, IntPtr small, int icons);

	// 系统命名空间图标（此电脑/回收站）：SHGetFileInfo 对 "::{GUID}" 命名空间路径经常拿不到图标，
	// 因此再回退到注册表 DefaultIcon + ExtractIconEx（imageres.dll 系统图标，稳定可靠）。
	private static ImageSource ShellNamespaceIcon(string clsid)
	{
		try
		{
			SHFILEINFO info;
			IntPtr hIcon = SHGetFileInfo(clsid, 0u, out info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 256u);
			if (hIcon != IntPtr.Zero)
			{
				BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
				bitmap.Freeze();
				DestroyIcon(hIcon);
				return bitmap;
			}
		}
		catch
		{
		}
		try
		{
			string guid = clsid.TrimStart(':');
			using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("CLSID\\" + guid + "\\DefaultIcon"))
			{
				string def = key?.GetValue(null) as string;
				if (!string.IsNullOrWhiteSpace(def))
				{
					BitmapSource fromDef = IconFromDefaultIconString(def);
					if (fromDef != null)
					{
						return fromDef;
					}
				}
			}
			// 最终兜底：Win10/11 桌面原生图标固定在 imageres.dll 里，
			// 此电脑 = -109（回收站满 = -55；注册表键缺失时走这里，保证永远是桌面原生图标）
			string known = (clsid == SystemPlaces.ComputerClsid) ? "imageres.dll,-109" : ((clsid == RecycleBin.Clsid) ? "imageres.dll,-55" : null);
			if (known != null)
			{
				BitmapSource fromKnown = IconFromDefaultIconString(known);
				if (fromKnown != null)
				{
					return fromKnown;
				}
			}
		}
		catch
		{
		}
		return null;
	}

	// "路径,索引" 形式的 DefaultIcon 字符串 → 图标位图（支持 -N 资源 ID 与逗号索引）
	private static BitmapSource IconFromDefaultIconString(string def)
	{
		try
		{
			int iconIndex = 0;
			string file = def;
			int idx = def.LastIndexOf(',');
			if (idx >= 0 && int.TryParse(def.Substring(idx + 1), out int parsed))
			{
				iconIndex = parsed;
				file = def.Substring(0, idx);
			}
			file = Environment.ExpandEnvironmentVariables(file.Trim('"'));
			if (File.Exists(file))
			{
				ExtractIconEx(file, iconIndex, out IntPtr large, IntPtr.Zero, 1);
				if (large != IntPtr.Zero)
				{
					BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
					bitmap.Freeze();
					DestroyIcon(large);
					return bitmap;
				}
			}
		}
		catch
		{
		}
		return null;
	}

	// 此电脑图标：系统官方图标（SHGetFileInfo → 注册表 DefaultIcon 双通道）
	public static ImageSource MyComputerIcon()
	{
		if (computerIcon != null)
		{
			return computerIcon;
		}
		computerIcon = ShellNamespaceIcon(SystemPlaces.ComputerClsid);
		return computerIcon;
	}

	// 回收站图标：系统官方图标（SHGetFileInfo → 注册表 DefaultIcon 双通道）
	public static ImageSource RecycleIcon()
	{
		if (recycleIcon != null)
		{
			return recycleIcon;
		}
		recycleIcon = ShellNamespaceIcon(RecycleBin.Clsid);
		return recycleIcon;
	}

	public static void Computer()
	{
		Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder")
		{
			UseShellExecute = true
		});
	}
}
