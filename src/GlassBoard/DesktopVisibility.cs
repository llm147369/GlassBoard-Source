using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Threading;

namespace GlassBoard;

public static class DesktopVisibility
{
	private delegate bool EnumCallback(IntPtr h, IntPtr data);

	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct APPBARDATA
	{
		public uint Size;

		public IntPtr Window;

		public uint Message;

		public uint Edge;

		public RECT Rect;

		public IntPtr Param;
	}

	private static readonly string RecoveryFile;

	private static Dictionary<long, ShellState> original;

	private static bool? taskbarHidden;

	private static bool? iconsHidden;

	// 隐藏桌面图标时的原注册表 HideIcons 值：退出/崩溃恢复时还原。
	private static int? originalHideIcons;

	// 隐藏任务栏时记录的 AppBar 原始状态：恢复时还原工作区占用。
	private static uint? originalAppBarState;

	private const string ExplorerAdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

	// 每个窗口句柄最近一次 SW_HIDE 的时间：限制重试频率，避免与 explorer 反复互相拉扯。
	private static readonly Dictionary<long, DateTime> lastHide = new Dictionary<long, DateTime>();

	private static readonly DispatcherTimer maintenance;

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindow(string cls, string title);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr h);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(IntPtr h);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr h, int cmd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool ShowWindowAsync(IntPtr h, int cmd);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr h, out uint processId);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong(IntPtr h, int index);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr h, StringBuilder text, int count);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumCallback callback, IntPtr data);

	[DllImport("user32.dll")]
	private static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr data);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

	[DllImport("shell32.dll")]
	private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

	// 桌面图标走 Explorer 原生开关（HideIcons 注册表值 + 广播 WM_SETTINGCHANGE）：
	// Explorer 自己负责隐藏/显示，窗口层不再反复 SW_HIDE，彻底消除图标闪烁。
	private static int? ReadHideIcons()
	{
		using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ExplorerAdvancedKey))
		{
			object value = key?.GetValue("HideIcons");
			return (value is int num) ? new int?(num) : null;
		}
	}

	private static void WriteHideIcons(int value)
	{
		using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ExplorerAdvancedKey))
		{
			key.SetValue("HideIcons", value, Microsoft.Win32.RegistryValueKind.DWord);
		}
		SendMessageTimeout(new IntPtr(0xFFFF), 26u, IntPtr.Zero, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer", 2u, 2000u, out IntPtr _);
	}

	static DesktopVisibility()
	{
		RecoveryFile = Path.Combine(Store.Folder, "shell-recovery.json");
		original = new Dictionary<long, ShellState>();
		maintenance = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(2000.0)
		};
		maintenance.Tick += (object s, EventArgs e) =>
		{
			Maintain();
		};
	}

	private static string Class(IntPtr h)
	{
		StringBuilder stringBuilder = new StringBuilder(256);
		GetClassName(h, stringBuilder, 256);
		return stringBuilder.ToString();
	}

	// —— AppBar 状态：ABS_AUTOHIDE(0x1) 让 Explorer 释放任务栏占用的屏幕工作区 ——
	// 单纯 SW_HIDE 任务栏窗口并不会释放工作区：全屏/最大化应用仍按“任务栏高度”留空，
	// 底部出现一条透明任务栏位置。给 AppBar 加上 ABS_AUTOHIDE 后 Explorer 才会广播全屏工作区。
	private static APPBARDATA AppBarData()
	{
		APPBARDATA result = new APPBARDATA
		{
			Size = (uint)Marshal.SizeOf(typeof(APPBARDATA)),
			Window = FindWindow("Shell_TrayWnd", null)
		};
		if (result.Window == IntPtr.Zero)
		{
			throw new InvalidOperationException("未找到 Windows 任务栏");
		}
		return result;
	}

	private static uint AppBarState()
	{
		APPBARDATA data = AppBarData();
		return (uint)SHAppBarMessage(4u, ref data).ToUInt64();
	}

	private static void ApplyAppBarState(uint state)
	{
		APPBARDATA data = AppBarData();
		data.Param = new IntPtr(state);
		SHAppBarMessage(10u, ref data);
	}

	// 还原隐藏前记录的 AppBar 状态（含 ABS_AUTOHIDE 位），恢复任务栏工作区占用
	private static void RestoreAppBarState()
	{
		if (originalAppBarState.HasValue)
		{
			try
			{
				ApplyAppBarState(originalAppBarState.Value);
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
			originalAppBarState = null;
		}
	}

	private static List<IntPtr> Taskbars()
	{
		List<IntPtr> result = new List<IntPtr>();
		IntPtr intPtr = FindWindow("Shell_TrayWnd", null);
		if (intPtr != IntPtr.Zero)
		{
			result.Add(intPtr);
		}
		EnumWindows((IntPtr h, IntPtr d) =>
		{
			string text = Class(h);
			if ((text == "Shell_TrayWnd" || text == "Shell_SecondaryTrayWnd") && !result.Contains(h))
			{
				result.Add(h);
			}
			return true;
		}, IntPtr.Zero);
		return result;
	}

	private static List<IntPtr> DesktopLists()
	{
		HashSet<IntPtr> result = new HashSet<IntPtr>();
		Action<IntPtr> addView = (IntPtr view) =>
		{
			result.Add(view);
			EnumChildWindows(view, (IntPtr child, IntPtr data) =>
			{
				if (Class(child) == "SysListView32")
				{
					result.Add(child);
				}
				return true;
			}, IntPtr.Zero);
		};
		EnumWindows((IntPtr h, IntPtr d) =>
		{
			string text = Class(h);
			IntPtr intPtr = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
			if (intPtr != IntPtr.Zero)
			{
				addView(intPtr);
			}
			if (text == "Progman" || text == "WorkerW")
			{
				EnumChildWindows(h, (IntPtr child, IntPtr data) =>
				{
					if (Class(child) == "SHELLDLL_DefView")
					{
						addView(child);
					}
					return true;
				}, IntPtr.Zero);
			}
			return true;
		}, IntPtr.Zero);
		return result.OrderBy((IntPtr h) => (!(Class(h) == "SHELLDLL_DefView")) ? 1 : 0).ToList();
	}

	private static bool MatchesSaved(ShellState state)
	{
		IntPtr intPtr = new IntPtr(state.Handle);
		if (!IsWindow(intPtr) || Class(intPtr) != state.ClassName)
		{
			return false;
		}
		if (state.ClassName == "Shell_TrayWnd" || state.ClassName == "Shell_SecondaryTrayWnd")
		{
			return Taskbars().Contains(intPtr);
		}
		if (state.ClassName == "SysListView32" || state.ClassName == "SHELLDLL_DefView")
		{
			return DesktopLists().Contains(intPtr);
		}
		return false;
	}

	private static void Remember(IEnumerable<IntPtr> handles)
	{
		bool flag = false;
		foreach (IntPtr handle in handles)
		{
			if (!original.ContainsKey(handle.ToInt64()))
			{
				original[handle.ToInt64()] = new ShellState
				{
					Handle = handle.ToInt64(),
					ClassName = Class(handle),
					Visible = ((GetWindowLong(handle, -16) & 0x10000000) != 0),
					HideIcons = originalHideIcons,
					AppBarState = originalAppBarState
				};
				flag = true;
			}
		}
		if (flag)
		{
			Directory.CreateDirectory(Store.Folder);
			string text = RecoveryFile + ".tmp";
			using (FileStream stream = File.Create(text))
			{
				new DataContractJsonSerializer(typeof(List<ShellState>)).WriteObject((Stream)stream, (object)original.Values.ToList());
			}
			if (File.Exists(RecoveryFile))
			{
				File.Replace(text, RecoveryFile, null);
			}
			else
			{
				File.Move(text, RecoveryFile);
			}
		}
	}

	// 只返回桌面图标列表（SysListView32）。隐藏桌面图标时绝不能隐藏 SHELLDLL_DefView，
	// 否则桌面右键菜单会一起失效。
	private static List<IntPtr> IconLists()
	{
		return DesktopLists().Where((IntPtr h) => Class(h) == "SysListView32").ToList();
	}

	public static bool? IsHidden(bool taskbar)
	{
		List<IntPtr> list = (taskbar ? Taskbars() : DesktopLists());
		if (list.Count == 0)
		{
			return null;
		}
		List<IntPtr> list2 = list.Where((IntPtr h) => Class(h) == "SysListView32").ToList();
		if (!taskbar && list2.Count > 0)
		{
			list = list2;
		}
		return !list.Any(IsWindowVisible);
	}

	public static bool DesiredHidden(bool taskbar)
	{
		return (taskbar ? taskbarHidden : iconsHidden) ?? (IsHidden(taskbar) == true);
	}

	public static string SetHidden(bool taskbar, bool hide)
	{
		List<IntPtr> list = (taskbar ? Taskbars() : IconLists());
		string text = (taskbar ? "任务栏" : "桌面图标");
		if (list.Count == 0)
		{
			return "未找到" + text + "，请确认 Windows Explorer 已运行。";
		}
		int num = 0;
		Remember(list);
		if (taskbar && hide)
		{
			// 记录原始 AppBar 状态并加上 ABS_AUTOHIDE：Explorer 释放工作区，全屏不留透明条
			try
			{
				if (originalAppBarState == null)
				{
					originalAppBarState = AppBarState();
				}
				uint state = originalAppBarState.Value;
				if ((state & 1u) == 0)
				{
					ApplyAppBarState(state | 1u);
				}
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
		}
		else if (taskbar)
		{
			RestoreAppBarState();
		}
		if (!taskbar)
		{
			// 桌面图标：写 Explorer 原生 HideIcons 开关（首次记录原值，退出/崩溃恢复时还原）
			if (originalHideIcons == null)
			{
				originalHideIcons = ReadHideIcons() ?? 0;
			}
			WriteHideIcons(hide ? 1 : 0);
		}
		foreach (IntPtr item in list)
		{
			if (hide && ShellBooting(item))
			{
				// explorer 正在重启初始化：先跳过，由维护循环在窗口稳定后再隐藏，避免崩溃循环
				continue;
			}
			if (!ShowWindowAsync(item, (!hide) ? 8 : 0))
			{
				num++;
				Store.Log(new Win32Exception(Marshal.GetLastWin32Error(), "无法切换 " + Class(item)));
			}
		}
		if (taskbar)
		{
			taskbarHidden = hide;
		}
		else
		{
			iconsHidden = hide;
		}
		if (taskbarHidden == true || iconsHidden == true)
		{
			maintenance.Start();
		}
		else
		{
			maintenance.Stop();
		}
		if (num <= 0)
		{
			return "已请求" + (hide ? "隐藏" : "显示") + text;
		}
		return text + "部分窗口切换失败，详情已记录在 error.log。";
	}

	public static string ToggleTaskbar()
	{
		return SetHidden(taskbar: true, !DesiredHidden(taskbar: true));
	}

	public static string ToggleIcons()
	{
		return SetHidden(taskbar: false, !DesiredHidden(taskbar: false));
	}

	// explorer 刚启动的一小段时间里任务栏仍在初始化，此时 SW_HIDE 容易与
	// comctl32 的 SysListView32 初始化竞争，引发 explorer 崩溃循环（0xc0000005）。
	private static bool ShellBooting(IntPtr h)
	{
		try
		{
			GetWindowThreadProcessId(h, out uint pid);
			if (pid == 0)
			{
				return true;
			}
			using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById((int)pid);
			return DateTime.UtcNow - process.StartTime.ToUniversalTime() < TimeSpan.FromSeconds(8.0);
		}
		catch
		{
			return true;
		}
	}

	// 限频 + 启动宽限期：维护循环只对稳定存在的窗口执行隐藏，避免高频拉扯与崩溃循环。
	private static bool CanHide(IntPtr h)
	{
		long key = h.ToInt64();
		if (ShellBooting(h))
		{
			return false;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (lastHide.TryGetValue(key, out DateTime value) && utcNow - value < TimeSpan.FromSeconds(3.0))
		{
			return false;
		}
		lastHide[key] = utcNow;
		if (lastHide.Count > 64)
		{
			foreach (long item in (from kv in lastHide orderby kv.Value select kv.Key).Take(lastHide.Count - 64).ToList())
			{
				lastHide.Remove(item);
			}
		}
		return true;
	}

	private static void Maintain()
	{
		try
		{
			// 桌面图标已由 Explorer 原生 HideIcons 负责，这里只做状态对齐：
			// 若用户在桌面右键菜单手动改回显示，重新写回隐藏值即可，不触碰任何窗口，不会闪烁。
			if (iconsHidden == true && (ReadHideIcons() ?? 0) != 1)
			{
				WriteHideIcons(1);
			}
			if (taskbarHidden == true)
			{
				// explorer 重启/其他软件改动后可能清掉 ABS_AUTOHIDE：重新断言，保持工作区全屏
				try
				{
					if (originalAppBarState.HasValue && (AppBarState() & 1u) == 0)
					{
						ApplyAppBarState(originalAppBarState.Value | 1u);
					}
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
				List<IntPtr> list = Taskbars();
				if (list.Count > 0)
				{
					Remember(list);
					foreach (IntPtr item in list)
					{
						if (IsWindowVisible(item) && CanHide(item))
						{
							ShowWindowAsync(item, 0);
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			maintenance.Stop();
		}
	}

	public static void Recover()
	{
		if (!File.Exists(RecoveryFile))
		{
			return;
		}
		using (FileStream stream = File.OpenRead(RecoveryFile))
		{
			foreach (ShellState item in (List<ShellState>)new DataContractJsonSerializer(typeof(List<ShellState>)).ReadObject((Stream)stream))
			{
				if (item.AppBarState.HasValue)
				{
					try
					{
						ApplyAppBarState(item.AppBarState.Value);
					}
					catch (Exception ex)
					{
						Store.Log(ex);
					}
					break;
				}
			}
		}
		using (FileStream stream = File.OpenRead(RecoveryFile))
		{
			foreach (ShellState item in (List<ShellState>)new DataContractJsonSerializer(typeof(List<ShellState>)).ReadObject((Stream)stream))
			{
				if (item.HideIcons.HasValue)
				{
					WriteHideIcons(item.HideIcons.Value);
					break;
				}
			}
		}
		using (FileStream stream2 = File.OpenRead(RecoveryFile))
		{
			foreach (ShellState item in (List<ShellState>)new DataContractJsonSerializer(typeof(List<ShellState>)).ReadObject((Stream)stream2))
			{
				if (MatchesSaved(item))
				{
					ShowWindow(new IntPtr(item.Handle), item.Visible ? 8 : 0);
				}
			}
		}
		File.Delete(RecoveryFile);
	}

	public static void RestoreOriginal()
	{
		maintenance.Stop();
		taskbarHidden = null;
		iconsHidden = null;
		RestoreAppBarState();
		if (originalHideIcons.HasValue)
		{
			WriteHideIcons(originalHideIcons.Value);
			originalHideIcons = null;
		}
		foreach (ShellState value in original.Values)
		{
			if (MatchesSaved(value))
			{
				ShowWindow(new IntPtr(value.Handle), value.Visible ? 8 : 0);
			}
		}
		original.Clear();
		if (File.Exists(RecoveryFile))
		{
			File.Delete(RecoveryFile);
		}
	}

	public static void ShowAll()
	{
		maintenance.Stop();
		taskbarHidden = false;
		iconsHidden = false;
		RestoreAppBarState();
		if (originalHideIcons.HasValue)
		{
			WriteHideIcons(originalHideIcons.Value);
			originalHideIcons = null;
		}
		IntPtr[] array = Taskbars().Concat(DesktopLists()).ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			ShowWindow(array[i], 8);
		}
		original.Clear();
		if (File.Exists(RecoveryFile))
		{
			File.Delete(RecoveryFile);
		}
	}
}
