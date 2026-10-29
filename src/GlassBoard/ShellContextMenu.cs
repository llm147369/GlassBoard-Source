using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlassBoard;

/// <summary>
/// 弹出文件/文件夹的 Windows 原生右键菜单（资源管理器同款：管理员身份运行、属性、固定等）。
/// 通过 SHParseDisplayName + IShellFolder.GetUIObjectOf(IContextMenu) + TrackPopupMenuEx 实现。
/// 所有 Shell 调用都跑在专用 STA 宿主线程（自带消息泵）上：第三方扩展即使卡死也只挂后台线程，
/// UI 线程永远即时返回；超时/挂死的宿主会被弃用重生，绝不无限等待。
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

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct MENUITEMINFO
	{
		public uint cbSize;
		public uint fMask;
		public uint fType;
		public uint fState;
		public uint wID;
		public IntPtr hSubMenu;
		public IntPtr hbmpChecked;
		public IntPtr hbmpUnchecked;
		public IntPtr dwItemData;
		public IntPtr dwTypeData;
		public uint cch;
		public IntPtr hbmpItem;
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

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, IntPtr hSubMenu);

	[DllImport("user32.dll")]
	private static extern bool DestroyMenu(IntPtr hMenu);

	[DllImport("user32.dll")]
	private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT lpPoint);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern int GetMenuItemCount(IntPtr hMenu);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, bool byPosition, ref MENUITEMINFO mii);

	private static readonly Guid IidContextMenu = new Guid("000214e4-0000-0000-c000-000000000046");
	private static readonly Guid IidShellFolder = new Guid("000214e6-0000-0000-c000-000000000046");

	private const uint CMF_NORMAL = 0x0000;
	private const uint TPM_RETURNCMD = 0x0100;
	private const uint TPM_NONOTIFY = 0x0080;
	private const uint TPM_RIGHTBUTTON = 0x0002;
	private const uint MIIM_STATE = 0x0001;
	private const uint MIIM_ID = 0x0002;
	private const uint MIIM_SUBMENU = 0x0004;
	private const uint MIIM_STRING = 0x0040;
	private const uint MIIM_FTYPE = 0x0100;
	private const uint MFT_SEPARATOR = 0x0800;
	private const uint MFS_DISABLED = 0x0003;
	private const uint MFS_CHECKED = 0x0008;
	private const uint MF_SEPARATOR = 0x0800;
	private const uint MF_GRAYED = 0x0001;
	private const uint MF_CHECKED = 0x0008;
	private const uint MF_POPUP = 0x0010;

	/// <summary>原生菜单追踪期间为 true；大面板/控制中心据此抑制失焦自动隐藏。</summary>
	public static volatile bool Active;

	// —— STA 宿主线程（自带 Dispatcher 消息泵）：Shell 扩展兼容性最好（ThreadingModel=Apartment 的扩展） ——
	private static readonly object hostGate = new object();
	private static Thread hostThread;
	private static Dispatcher hostDispatcher;
	private static int hostBusyTick;
	private static bool hostPoisoned;

	private static Dispatcher Host()
	{
		lock (hostGate)
		{
			bool stuck = hostDispatcher != null && !hostPoisoned && hostBusyTick != 0 && Environment.TickCount - hostBusyTick > 8000;
			if (stuck)
			{
				Store.Trace("SHELLMENU host stuck, respawning");
				hostPoisoned = true;
			}
			if (hostThread != null && hostThread.IsAlive && hostDispatcher != null && !hostPoisoned)
			{
				return hostDispatcher;
			}
			ManualResetEventSlim ready = new ManualResetEventSlim(false);
			Dispatcher dispatcher = null;
			Thread thread = new Thread(() =>
			{
				dispatcher = Dispatcher.CurrentDispatcher;
				ready.Set();
				Dispatcher.Run();
			});
			thread.IsBackground = true;
			thread.Name = "glassboard-shellhost";
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
			ready.Wait();
			hostThread = thread;
			hostDispatcher = dispatcher;
			hostBusyTick = 0;
			hostPoisoned = false;
			return dispatcher;
		}
	}

	// 把工作投递到宿主线程并限时等待；超时视为扩展挂死（宿主下次弃用），UI 侧走回退。
	private static T Run<T>(Func<T> work, int timeoutMs)
	{
		Dispatcher dispatcher = Host();
		TaskCompletionSource<T> tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (hostGate)
		{
			hostBusyTick = Environment.TickCount;
		}
		dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
		{
			try
			{
				tcs.TrySetResult(work());
			}
			catch (Exception ex)
			{
				tcs.TrySetException(ex);
			}
			finally
			{
				lock (hostGate)
				{
					hostBusyTick = 0;
				}
			}
		}));
		if (tcs.Task.Wait(timeoutMs))
		{
			return tcs.Task.Result;
		}
		Store.Trace("SHELLMENU timeout ms=" + timeoutMs);
		lock (hostGate)
		{
			hostPoisoned = true;
		}
		throw new TimeoutException("Shell 扩展响应超时");
	}

	// 与 BuildModel/Invoke 共享的菜单句柄准备（宿主线程内执行）。
	private static IntPtr Prepare(string path, out IntPtr pidl, out IContextMenu menu)
	{
		pidl = IntPtr.Zero;
		menu = null;
		uint attrs;
		int hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs);
		if (hr != 0 || pidl == IntPtr.Zero)
		{
			Store.Trace("SHELLMENU parse failed hr=0x" + hr.ToString("X8") + " path=" + path);
			return IntPtr.Zero;
		}
		Guid iidFolder = IidShellFolder;
		IShellFolder folder;
		IntPtr childPidl;
		hr = SHBindToParent(pidl, ref iidFolder, out folder, out childPidl);
		if (hr != 0 || folder == null)
		{
			Store.Trace("SHELLMENU bind failed hr=0x" + hr.ToString("X8"));
			ILFree(pidl);
			pidl = IntPtr.Zero;
			return IntPtr.Zero;
		}
		object menuObject;
		Guid iidMenu = IidContextMenu;
		hr = folder.GetUIObjectOf(IntPtr.Zero, 1, new IntPtr[1] { childPidl }, ref iidMenu, IntPtr.Zero, out menuObject);
		if (hr != 0 || menuObject == null)
		{
			Store.Trace("SHELLMENU getuiobject failed hr=0x" + hr.ToString("X8"));
			ILFree(pidl);
			pidl = IntPtr.Zero;
			return IntPtr.Zero;
		}
		menu = menuObject as IContextMenu;
		if (menu == null)
		{
			Store.Trace("SHELLMENU cast failed");
			ILFree(pidl);
			pidl = IntPtr.Zero;
			return IntPtr.Zero;
		}
		IntPtr hmenu = CreatePopupMenu();
		if (hmenu == IntPtr.Zero)
		{
			ILFree(pidl);
			pidl = IntPtr.Zero;
			Marshal.ReleaseComObject(menu);
			menu = null;
			return IntPtr.Zero;
		}
		int count = menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, CMF_NORMAL);
		if (count < 0)
		{
			Store.Trace("SHELLMENU query failed hr=0x" + count.ToString("X8"));
			DestroyMenu(hmenu);
			ILFree(pidl);
			pidl = IntPtr.Zero;
			Marshal.ReleaseComObject(menu);
			menu = null;
			return IntPtr.Zero;
		}
		return hmenu;
	}

	// 把 HMENU 树转换成 UI 无关的菜单模型（宿主线程内执行；不保存句柄/COM 引用）。
	private static void WalkMenu(IntPtr hmenu, List<ShellMenuEntry> into)
	{
		int count = GetMenuItemCount(hmenu);
		if (count <= 0)
		{
			return;
		}
		for (int i = 0; i < count; i++)
		{
			MENUITEMINFO info = new MENUITEMINFO
			{
				cbSize = (uint)Marshal.SizeOf(typeof(MENUITEMINFO)),
				fMask = MIIM_FTYPE | MIIM_ID | MIIM_STATE | MIIM_SUBMENU | MIIM_STRING
			};
			if (!GetMenuItemInfo(hmenu, (uint)i, true, ref info))
			{
				continue;
			}
			if ((info.fType & MFT_SEPARATOR) != 0)
			{
				into.Add(new ShellMenuEntry
				{
					Separator = true
				});
				continue;
			}
			ShellMenuEntry entry = new ShellMenuEntry
			{
				Id = info.wID,
				Disabled = (info.fState & MFS_DISABLED) != 0,
				Checked = (info.fState & MFS_CHECKED) != 0
			};
			if (info.cch > 0)
			{
				info.cch++;
				IntPtr buffer = Marshal.AllocHGlobal((int)info.cch * 2);
				try
				{
					info.dwTypeData = buffer;
					if (GetMenuItemInfo(hmenu, (uint)i, true, ref info))
					{
						entry.Text = Marshal.PtrToStringUni(buffer);
					}
				}
				finally
				{
					Marshal.FreeHGlobal(buffer);
				}
			}
			if (info.hSubMenu != IntPtr.Zero)
			{
				entry.Sub = new List<ShellMenuEntry>();
				WalkMenu(info.hSubMenu, entry.Sub);
			}
			into.Add(entry);
		}
	}

	private static bool CanShell(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		// 协议地址与虚拟 shell 项没有文件级右键菜单，跳过
		if (path.Contains("://") || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return true;
	}

	private static List<ShellMenuEntry> BuildModel(string path)
	{
		IntPtr pidl;
		IContextMenu menu;
		IntPtr hmenu = Prepare(path, out pidl, out menu);
		if (hmenu == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			List<ShellMenuEntry> model = new List<ShellMenuEntry>();
			WalkMenu(hmenu, model);
			return model;
		}
		finally
		{
			DestroyMenu(hmenu);
			if (menu != null)
			{
				Marshal.ReleaseComObject(menu);
			}
			ILFree(pidl);
		}
	}

	private static void InvokeCommand(string path, uint id, IntPtr owner)
	{
		IntPtr pidl;
		IContextMenu menu;
		IntPtr hmenu = Prepare(path, out pidl, out menu);
		if (hmenu == IntPtr.Zero || menu == null)
		{
			return;
		}
		try
		{
			CMINVOKECOMMANDINFO info = new CMINVOKECOMMANDINFO
			{
				cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFO)),
				hwnd = owner,
				lpVerb = new IntPtr((long)(id - 1)),
				nShow = 1
			};
			menu.InvokeCommand(ref info);
		}
		finally
		{
			DestroyMenu(hmenu);
			Marshal.ReleaseComObject(menu);
			ILFree(pidl);
		}
	}

	// 用宿主线程构建出的模型在 UI 线程重建纯 Win32 菜单（保留 id/禁用/勾选/子菜单，不依赖 Shell 扩展渲染）。
	private static IntPtr RebuildMenu(List<ShellMenuEntry> model)
	{
		IntPtr hmenu = CreatePopupMenu();
		if (hmenu == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		foreach (ShellMenuEntry entry in model)
		{
			if (entry.Separator)
			{
				AppendMenu(hmenu, MF_SEPARATOR, 0, (string)null);
				continue;
			}
			uint flags = 0;
			if (entry.Disabled)
			{
				flags |= MF_GRAYED;
			}
			if (entry.Checked)
			{
				flags |= MF_CHECKED;
			}
			if (entry.Sub != null && entry.Sub.Count > 0)
			{
				IntPtr sub = RebuildMenu(entry.Sub);
				if (sub != IntPtr.Zero)
				{
					AppendMenu(hmenu, flags | MF_POPUP, entry.Id, sub);
				}
			}
			else
			{
				AppendMenu(hmenu, flags, entry.Id, entry.Text ?? "");
			}
		}
		return hmenu;
	}

	// 必须在 owner 所属的 UI 线程调用：TrackPopupMenuEx 从后台线程（含 WPF Dispatcher 宿主线程）调用时
	// 菜单窗口根本不会创建、立即返回 0（已用 MenuTest 实测矩阵证实）。菜单模型构建仍留在宿主线程，
	// 因此第三方扩展卡死不会冻结 UI；只有最后的菜单追踪阶段占用 UI 线程（与资源管理器一致）。
	private static int TrackNative(string path, List<ShellMenuEntry> model, IntPtr hwnd)
	{
		IntPtr hmenu = RebuildMenu(model);
		if (hmenu == IntPtr.Zero)
		{
			return 0;
		}
		try
		{
			GetCursorPos(out POINT pt);
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
				// 延迟复位：菜单打开期间排队的 Deactivated 事件（Input 优先级）先于 Idle 处理，
				// 保证面板/控制中心的失焦自动隐藏在菜单存续与关闭瞬间都不误判。
				Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
				{
					Active = false;
				}));
			}
			Store.Trace("SHELLMENU track=" + cmd);
			if (cmd != 0)
			{
				InvokeAsync(path, (uint)cmd, hwnd);
			}
			return cmd;
		}
		finally
		{
			DestroyMenu(hmenu);
		}
	}

	/// <summary>
	/// 异步构建原生菜单模型：ready/fail 在后台线程池回调（调用方自行 marshal 回 UI 线程）。
	/// Shell 扩展挂死时最多等待 3s 后走 fail 回退。
	/// </summary>
	public static void BuildAsync(string path, Action<List<ShellMenuEntry>> ready, Action fail)
	{
		if (!CanShell(path))
		{
			fail?.Invoke();
			return;
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		ThreadPool.QueueUserWorkItem(delegate
		{
			try
			{
				List<ShellMenuEntry> model = Run(() => BuildModel(path), 3000);
				stopwatch.Stop();
				Store.Trace("SHELLMENU build ms=" + stopwatch.ElapsedMilliseconds + " items=" + (model == null ? 0 : model.Count) + " path=" + path);
				if (model == null || model.Count == 0)
				{
					fail?.Invoke();
					return;
				}
				ready?.Invoke(model);
			}
			catch (Exception ex)
			{
				stopwatch.Stop();
				Store.Trace("SHELLMENU build failed " + ex.Message + " ms=" + stopwatch.ElapsedMilliseconds);
				fail?.Invoke();
			}
		});
	}

	/// <summary>在宿主线程执行原生菜单命令（命令 id 为 1 基编号）。</summary>
	public static void InvokeAsync(string path, uint id, IntPtr owner)
	{
		ThreadPool.QueueUserWorkItem(delegate
		{
			try
			{
				Run(delegate
				{
					InvokeCommand(path, id, owner);
					return true;
				}, 4000);
			}
			catch (Exception ex)
			{
				Store.Trace("SHELLMENU invoke failed " + ex.Message);
			}
		});
	}

	/// <summary>
	/// 在鼠标当前位置弹出 path 的原生右键菜单：模型在宿主线程异步构建（挂死防护），
	/// 菜单追踪在 owner 的 UI 线程执行（原生菜单的硬性要求）；构建失败或菜单为空时调用 fallback。
	/// </summary>
	public static void ShowAtCursor(string path, Window owner, Action fallback = null)
	{
		if (!CanShell(path))
		{
			fallback?.Invoke();
			return;
		}
		if (owner == null)
		{
			fallback?.Invoke();
			return;
		}
		IntPtr hwnd = new WindowInteropHelper(owner).Handle;
		BuildAsync(path, delegate (List<ShellMenuEntry> model)
		{
			owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
			{
				try
				{
					int cmd = TrackNative(path, model, hwnd);
					if (cmd == 0 && model.Count == 0)
					{
						fallback?.Invoke();
					}
				}
				catch (Exception ex)
				{
					Store.Trace("SHELLMENU track error " + ex.Message);
				}
			}));
		}, delegate
		{
			owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
			{
				fallback?.Invoke();
			}));
		});
	}
}

/// <summary>原生菜单项模型（宿主线程构建 → UI 线程渲染，跨线程只传数据不传句柄）。</summary>
public sealed class ShellMenuEntry
{
	public string Text;
	public uint Id;
	public bool Separator;
	public bool Disabled;
	public bool Checked;
	public List<ShellMenuEntry> Sub;
}
