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

	private static readonly string RecoveryFile;

	private static Dictionary<long, ShellState> original;

	private static bool? taskbarHidden;

	private static bool? iconsHidden;

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

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong(IntPtr h, int index);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr h, StringBuilder text, int count);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumCallback callback, IntPtr data);

	[DllImport("user32.dll")]
	private static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr data);

	static DesktopVisibility()
	{
		RecoveryFile = Path.Combine(Store.Folder, "shell-recovery.json");
		original = new Dictionary<long, ShellState>();
		maintenance = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(750.0)
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
					Visible = ((GetWindowLong(handle, -16) & 0x10000000) != 0)
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
		foreach (IntPtr item in list)
		{
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

	private static void Maintain()
	{
		try
		{
			bool[] array = new bool[2] { true, false };
			foreach (bool flag in array)
			{
				if ((flag ? taskbarHidden : iconsHidden) != true)
				{
					continue;
				}
				List<IntPtr> list = (flag ? Taskbars() : IconLists());
				if (list.Count == 0)
				{
					continue;
				}
				Remember(list);
				foreach (IntPtr item in list)
				{
					if (IsWindowVisible(item))
					{
						ShowWindowAsync(item, 0);
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
