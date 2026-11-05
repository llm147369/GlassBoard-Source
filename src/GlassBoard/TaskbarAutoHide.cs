using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace GlassBoard;

public static class TaskbarAutoHide
{
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

	private static string Journal => Path.Combine(Store.Folder, "taskbar-autohide.json");

	public static bool Enabled => (State() & 1) != 0;

	[DllImport("shell32.dll")]
	private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindow(string cls, string name);

	private static APPBARDATA Data()
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

	private static uint State()
	{
		APPBARDATA data = Data();
		// SHAppBarMessage 与 explorer 通信：explorer 崩溃/挂起时该调用会阻塞。
		// 后台线程 + 400ms 超时兜底，返回 0 视为"无自动隐藏"，不让 UI 线程陪冻。
		try
		{
			System.Threading.Tasks.Task<uint> task = System.Threading.Tasks.Task.Run(() => (uint)SHAppBarMessage(4u, ref data).ToUInt64());
			return task.Wait(400) ? task.Result : 0u;
		}
		catch
		{
			return 0u;
		}
	}

	private static void Apply(uint state)
	{
		APPBARDATA data = Data();
		data.Param = new IntPtr(state);
		// 异步投递：调用方不依赖结果；explorer 挂起时后台任务自然排队，不冻结 UI。
		try
		{
			System.Threading.Tasks.Task.Run(() => SHAppBarMessage(10u, ref data));
		}
		catch
		{
		}
	}

	public static void Set(bool enabled)
	{
		uint num = State();
		if ((num & 1) != 0 != enabled)
		{
			Directory.CreateDirectory(Store.Folder);
			if (!File.Exists(Journal))
			{
				File.WriteAllText(Journal, num.ToString(CultureInfo.InvariantCulture));
			}
			Apply(FeatureRules.AutoHideState(num, enabled));
		}
	}

	public static void Restore()
	{
		if (File.Exists(Journal))
		{
			uint num = uint.Parse(File.ReadAllText(Journal), CultureInfo.InvariantCulture);
			Apply(FeatureRules.AutoHideState(State(), (num & 1) != 0));
			File.Delete(Journal);
		}
	}

	public static void ShowAndClear()
	{
		Apply(FeatureRules.AutoHideState(State(), enabled: false));
		if (File.Exists(Journal))
		{
			File.Delete(Journal);
		}
	}
}
