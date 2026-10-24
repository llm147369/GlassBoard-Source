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
		return (uint)SHAppBarMessage(4u, ref data).ToUInt64();
	}

	private static void Apply(uint state)
	{
		APPBARDATA data = Data();
		data.Param = new IntPtr(state);
		SHAppBarMessage(10u, ref data);
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
