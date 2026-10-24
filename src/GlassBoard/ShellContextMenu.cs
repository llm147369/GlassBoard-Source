using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GlassBoard;

/// <summary>
/// 弹出文件/文件夹的 Windows 原生右键菜单（资源管理器同款：管理员身份运行、属性、固定等）。
/// 通过 SHParseDisplayName + IShellFolder.GetUIObjectOf(IContextMenu) + TrackPopupMenuEx 实现。
/// </summary>
public static class ShellContextMenu
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct CMINVOKECOMMANDINFO
	{
		public int cbSize;
		public int fMask;
		public IntPtr hwnd;
		public IntPtr lpVerb;
		public string lpParameters;
		public string lpDirectory;
		public int nShow;
		public int dwHotKey;
		public IntPtr hIcon;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X;
		public int Y;
	}

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214e4-0000-0000-c000-000000000046")]
	private interface IContextMenu
	{
		[PreserveSig]
		int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

		[PreserveSig]
		int InvokeCommand(ref CMINVOKECOMMANDINFO pici);

		void GetCommandString(IntPtr idCmd, uint uType, IntPtr pvReserved, IntPtr pszName, uint cchMax);
	}

	[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214e6-0000-0000-c000-000000000046")]
	private interface IShellFolder
	{
		void ParseDisplayName(IntPtr hwnd, IntPtr pbc, string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

		[PreserveSig]
		int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

		[PreserveSig]
		int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

		[PreserveSig]
		int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

		[PreserveSig]
		int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

		[PreserveSig]
		int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

		[PreserveSig]
		int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);

		[PreserveSig]
		int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);

		[PreserveSig]
		int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

		[PreserveSig]
		int SetNameOf(IntPtr hwnd, IntPtr pidl, string pszName, uint uFlags, out IntPtr ppidlOut);
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

	[DllImport("shell32.dll")]
	private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IShellFolder ppv, out IntPtr ppidlLast);

	[DllImport("shell32.dll")]
	private static extern void ILFree(IntPtr pidl);

	[DllImport("user32.dll")]
	private static extern IntPtr CreatePopupMenu();

	[DllImport("user32.dll")]
	private static extern bool DestroyMenu(IntPtr hMenu);

	[DllImport("user32.dll")]
	private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT lpPoint);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	private static readonly Guid IidContextMenu = new Guid("000214e4-0000-0000-c000-000000000046");
	private static readonly Guid IidShellFolder = new Guid("000214e6-0000-0000-c000-000000000046");

	private const uint CMF_NORMAL = 0x0000;
	private const uint TPM_RETURNCMD = 0x0100;
	private const uint TPM_NONOTIFY = 0x0080;
	private const uint TPM_RIGHTBUTTON = 0x0002;

	/// <summary>原生菜单追踪期间为 true；大面板/控制中心据此抑制失焦自动隐藏。</summary>
	public static bool Active;

	/// <summary>在鼠标当前位置弹出 path 的原生右键菜单。owner 提供消息窗口。</summary>
	public static void ShowAtCursor(string path, Window owner)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}
		// 协议地址与虚拟 shell 项没有文件级右键菜单，跳过
		if (path.Contains("://") || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		try
		{
			IntPtr pidl;
			uint attrs;
			int hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs);
			if (hr != 0 || pidl == IntPtr.Zero)
			{
				Store.Trace("SHELLMENU parse failed hr=0x" + hr.ToString("X8") + " path=" + path);
				return;
			}
			IContextMenu menu = null;
			IntPtr hmenu = IntPtr.Zero;
			try
			{
				Guid iidFolder = IidShellFolder;
				IShellFolder folder;
				IntPtr childPidl;
				hr = SHBindToParent(pidl, ref iidFolder, out folder, out childPidl);
				if (hr != 0 || folder == null)
				{
					Store.Trace("SHELLMENU bind failed hr=0x" + hr.ToString("X8"));
					return;
				}
				object menuObject;
				Guid iidMenu = IidContextMenu;
				hr = folder.GetUIObjectOf(IntPtr.Zero, 1, new IntPtr[1] { childPidl }, ref iidMenu, IntPtr.Zero, out menuObject);
				if (hr != 0 || menuObject == null)
				{
					Store.Trace("SHELLMENU getuiobject failed hr=0x" + hr.ToString("X8"));
					return;
				}
				menu = menuObject as IContextMenu;
				if (menu == null)
				{
					Store.Trace("SHELLMENU cast failed");
					return;
				}
				hmenu = CreatePopupMenu();
				if (hmenu == IntPtr.Zero)
				{
					return;
				}
				int count = menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, CMF_NORMAL);
				if (count < 0)
				{
					Store.Trace("SHELLMENU query failed hr=0x" + count.ToString("X8"));
					return;
				}
				GetCursorPos(out POINT pt);
				IntPtr hwnd = ((owner == null) ? IntPtr.Zero : new WindowInteropHelper(owner).Handle);
				if (hwnd != IntPtr.Zero)
				{
					SetForegroundWindow(hwnd);
				}
				Active = true;
				int cmd;
				try
				{
					cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero);
				}
				finally
				{
					Active = false;
				}
				Store.Trace("SHELLMENU items=" + count + " track=" + cmd);
				if (cmd != 0)
				{
					CMINVOKECOMMANDINFO info = new CMINVOKECOMMANDINFO
					{
						cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFO)),
						hwnd = hwnd,
						lpVerb = new IntPtr(cmd - 1),
						nShow = 1
					};
					menu.InvokeCommand(ref info);
				}
			}
			finally
			{
				if (hmenu != IntPtr.Zero)
				{
					DestroyMenu(hmenu);
				}
				if (menu != null)
				{
					Marshal.ReleaseComObject(menu);
				}
				ILFree(pidl);
			}
		}
		catch (Exception ex)
		{
			Store.Trace("SHELLMENU error " + ex.Message);
		}
	}
}
