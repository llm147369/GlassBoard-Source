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

	private static readonly BoundedCache<string, ImageSource> icons = new BoundedCache<string, ImageSource>(300, StringComparer.OrdinalIgnoreCase);

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

	// SHIL_JUMBO = 4：256×256 系统图标列表（Win10/11 原生最高清晰度）
	[DllImport("shell32.dll", PreserveSig = false)]
	private static extern void SHGetImageList(int iImageList, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IImageList ppv);

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
	private interface IImageList
	{
		[PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
		[PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
		[PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
		[PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
		[PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
		[PreserveSig] int Draw(ref IntPtr pimldp);
		[PreserveSig] int Remove(int i);
		[PreserveSig] int GetIcon(int i, uint flags, out IntPtr picon);
	}

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
		// 永久磁盘缓存：每个图标只提取一次，之后所有启动直接从 256px PNG 加载
		string cacheName = IconDiskCache.Name(key + "|" + IconDiskCache.FileFingerprint(path));
		ImageSource cached = IconDiskCache.Load(cacheName);
		if (cached != null)
		{
			icons.Put(key, cached);
			return cached;
		}
		// 首选 Jumbo 通道：SYSICONINDEX + SHIL_JUMBO → 256×256 系统图标（最清晰）。
		// 注意：纯 SYSICONINDEX 请求拿不到 iIcon 高字节的角标（overlay）索引，
		// 因此带角标请求必须走下方 overlay 分支，否则快捷方式箭头会被静默丢弃。
		SHFILEINFO info;
		IntPtr intPtr;
		if (!showShortcutOverlays)
		{
			try
			{
				if (SHGetFileInfo(path, 0u, out info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 0x4000u) != IntPtr.Zero && info.iIcon != 0)
				{
					Guid guid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
					IImageList imageList = null;
					IntPtr jumboHandle = IntPtr.Zero;
					try
					{
						SHGetImageList(0x4, ref guid, out imageList);
						if (imageList != null && imageList.GetIcon(info.iIcon, 1u, out jumboHandle) == 0 && jumboHandle != IntPtr.Zero)
						{
							BitmapSource jumbo = Imaging.CreateBitmapSourceFromHIcon(jumboHandle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(256, 256));
							jumbo.Freeze();
							icons.Put(key, jumbo);
							IconDiskCache.Save(cacheName, jumbo);
							return jumbo;
						}
					}
					catch
					{
					}
					finally
					{
						if (jumboHandle != IntPtr.Zero)
						{
							DestroyIcon(jumboHandle);
						}
						if (imageList != null && Marshal.IsComObject(imageList))
						{
							Marshal.ReleaseComObject(imageList);
						}
					}
				}
			}
			catch
			{
			}
		}
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
					IconDiskCache.Save(cacheName, bitmapSource);
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
					IconDiskCache.Save(cacheName, bitmapSource2);
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
			IconDiskCache.Save(cacheName, bitmapSource3);
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

	// 只查内存缓存：图标后台管线的 UI 线程快速路径（未命中返回 null，不提取）
	public static ImageSource IconCached(string path, bool showShortcutOverlays = false, bool sharper = false)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		string key = (sharper ? "sharp/" : "normal/") + (showShortcutOverlays ? "overlay/" : "plain/") + path;
		icons.TryGet(key, out var value);
		return value;
	}

	public static void ClearIconCache()
	{
		icons.Clear();
	}

	private static ImageSource recycleIcon;
	private static ImageSource computerIcon;

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int ExtractIconEx(string file, int index, out IntPtr large, IntPtr small, int icons);

	// 按指定尺寸（256×256）精确提取图标资源（负索引 = 资源 ID，如 imageres.dll,-109）
	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int PrivateExtractIcons(string file, int index, int cx, int cy, out IntPtr hicon, IntPtr piconid, int nIcons, int flags);

	// 系统命名空间图标（此电脑/回收站）：注册表 DefaultIcon（缺失时用 imageres.dll 固定资源），
	// PrivateExtractIcons 按 256×256 精确提取（与 Explorer 桌面图标同源、同清晰度）。
	private static ImageSource ShellNamespaceIcon(string clsid)
	{
		try
		{
			string guid = clsid.TrimStart(':');
			string def = null;
			using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("CLSID\\" + guid + "\\DefaultIcon"))
			{
				def = key?.GetValue(null) as string;
			}
			// Win10/11 上「此电脑」的 DefaultIcon 键经常不存在 → 固定 imageres.dll 资源兜底
			if (string.IsNullOrWhiteSpace(def))
			{
				def = ((clsid == SystemPlaces.ComputerClsid) ? "imageres.dll,-109" : ((clsid == RecycleBin.Clsid) ? "imageres.dll,-55" : null));
			}
			if (!string.IsNullOrWhiteSpace(def))
			{
				BitmapSource fromDef = IconFromDefaultIconString(def);
				if (fromDef != null)
				{
					return fromDef;
				}
			}
		}
		catch
		{
		}
		try
		{
			SHFILEINFO info;
			IntPtr hIcon = SHGetFileInfo(clsid, 0u, out info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), 256u);
			if (hIcon != IntPtr.Zero)
			{
				BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(256, 256));
				bitmap.Freeze();
				DestroyIcon(hIcon);
				return bitmap;
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
				IntPtr large;
				// 首选 256×256 精确提取（负索引 = 资源 ID）；失败回退 ExtractIconEx
				if (PrivateExtractIcons(file, iconIndex, 256, 256, out large, IntPtr.Zero, 1, 0) > 0 && large != IntPtr.Zero)
				{
					BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(256, 256));
					bitmap.Freeze();
					DestroyIcon(large);
					return bitmap;
				}
				if (ExtractIconEx(file, iconIndex, out large, IntPtr.Zero, 1) > 0 && large != IntPtr.Zero)
				{
					BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(256, 256));
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

	// 系统特殊图标（此电脑/回收站）只在首次被请求时提取；UI 线程与后台图标管线可能并发触发
	private static readonly object shellIconLock = new object();

	// 此电脑图标：系统官方图标（SHGetFileInfo → 注册表 DefaultIcon 双通道）
	public static ImageSource MyComputerIcon()
	{
		ImageSource existing = computerIcon;
		if (existing != null)
		{
			return existing;
		}
		lock (shellIconLock)
		{
			if (computerIcon == null)
			{
				computerIcon = ShellNamespaceIcon(SystemPlaces.ComputerClsid);
			}
			return computerIcon;
		}
	}

	// 回收站图标：系统官方图标（SHGetFileInfo → 注册表 DefaultIcon 双通道）
	public static ImageSource RecycleIcon()
	{
		ImageSource existing = recycleIcon;
		if (existing != null)
		{
			return existing;
		}
		lock (shellIconLock)
		{
			if (recycleIcon == null)
			{
				recycleIcon = ShellNamespaceIcon(RecycleBin.Clsid);
			}
			return recycleIcon;
		}
	}

	public static void Computer()
	{
		Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder")
		{
			UseShellExecute = true
		});
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool LockWorkStation();

	// 锁屏：锁定当前工作站（等同 Win+L）
	public static void Lock()
	{
		try
		{
			LockWorkStation();
		}
		catch
		{
		}
	}

	// ---- 快捷方式 AppUserModelID（通知红点把通知按 AUMID 关联到 Dock 图标） ----

	[ComImport]
	[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPropertyStore
	{
		[PreserveSig]
		int GetCount(out uint count);
		[PreserveSig]
		int GetAt(uint index, out PropertyKey key);
		// PROPVARIANT 用原始缓冲区读写：struct 封送在部分环境返回 VT_EMPTY（封送器差异）
		[PreserveSig]
		int GetValue(ref PropertyKey key, IntPtr value);
		[PreserveSig]
		int SetValue(ref PropertyKey key, IntPtr value);
		[PreserveSig]
		int Commit();
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PropertyKey
	{
		public Guid fmtid;
		public uint pid;
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
	private static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr pbc, int flags, ref Guid riid, out IPropertyStore store);

	private static readonly Guid IID_IPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
	private static readonly PropertyKey PKEY_AppUserModel_ID = new PropertyKey
	{
		fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
		pid = 5u
	};

	// 读取 .lnk 快捷方式的 AppUserModelID（失败或非快捷方式返回空串）
	public static string ShortcutAumid(string path)
	{
		try
		{
			Guid iid = IID_IPropertyStore;
			SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out IPropertyStore store);
			try
			{
				PropertyKey key = PKEY_AppUserModel_ID;
				IntPtr pv = Marshal.AllocHGlobal(16);
				try
				{
					int hr = store.GetValue(ref key, pv);
					if (hr == 0 && Marshal.ReadInt16(pv) == 31)
					{
						IntPtr val = Marshal.ReadIntPtr(pv, 8);
						if (val != IntPtr.Zero)
						{
							string result = Marshal.PtrToStringUni(val) ?? "";
							Marshal.FreeCoTaskMem(val);
							return result;
						}
					}
				}
				finally
				{
					Marshal.FreeHGlobal(pv);
				}
				return "";
			}
			finally
			{
				Marshal.ReleaseComObject(store);
			}
		}
		catch
		{
			return "";
		}
	}

	// 读取 .lnk 指向的目标路径（WScript.Shell COM + 反射，避免引入 Microsoft.CSharp）
	public static string ShortcutTarget(string path)
	{
		try
		{
			Type shellType = Type.GetTypeFromProgID("WScript.Shell");
			if (shellType == null)
			{
				return "";
			}
			object shell = Activator.CreateInstance(shellType);
			if (shell == null)
			{
				return "";
			}
			try
			{
				object lnk = shellType.InvokeMember("CreateShortcut",
					System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
				if (lnk == null)
				{
					return "";
				}
				try
				{
					object target = lnk.GetType().InvokeMember("TargetPath",
						System.Reflection.BindingFlags.GetProperty, null, lnk, null);
					return target as string ?? "";
				}
				finally
				{
					Marshal.ReleaseComObject(lnk);
				}
			}
			finally
			{
				Marshal.ReleaseComObject(shell);
			}
		}
		catch
		{
			return "";
		}
	}

	// —— 渲染诊断支持：物理坐标与每显示器 DPI ——
	// 背景捕获（CopyFromScreen）必须用物理像素坐标。WPF 的 TransformToDevice 矩阵
	// 基于主屏 DPI，在混合 DPI 多显示器（主屏 150% + 副屏 100% 等）下算出的区域是错的，
	// 这正是部分设备（尤其配合动态壁纸）背景错位/黑块的根因。

	[StructLayout(LayoutKind.Sequential)]
	public struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

	[DllImport("user32.dll")]
	public static extern int GetDpiForWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	public static extern IntPtr GetDC(IntPtr hWnd);

	[DllImport("gdi32.dll")]
	public static extern int GetDeviceCaps(IntPtr hdc, int index);

	[DllImport("user32.dll")]
	public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

	/// <summary>窗口所在显示器的 DPI（Win10 1607+ 用 GetDpiForWindow，失败回退 GDI）。</summary>
	public static int DpiOf(IntPtr hwnd)
	{
		try
		{
			int dpi = GetDpiForWindow(hwnd);
			if (dpi > 0)
			{
				return dpi;
			}
		}
		catch
		{
		}
		try
		{
			IntPtr hdc = GetDC(hwnd);
			if (hdc == IntPtr.Zero)
			{
				return 96;
			}
			try
			{
				int caps = GetDeviceCaps(hdc, 88); // LOGPIXELSX
				return caps > 0 ? caps : 96;
			}
			finally
			{
				ReleaseDC(hwnd, hdc);
			}
		}
		catch
		{
			return 96;
		}
	}
}
