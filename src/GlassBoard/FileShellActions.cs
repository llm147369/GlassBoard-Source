using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GlassBoard;

public static class FileShellActions
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct SHELLEXECUTEINFO
	{
		public int Size;

		public uint Mask;

		public IntPtr Window;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string Verb;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string File;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string Parameters;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string Directory;

		public int Show;

		public IntPtr Instance;

		public IntPtr IdList;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string Class;

		public IntPtr ClassKey;

		public uint Hotkey;

		public IntPtr Icon;

		public IntPtr Process;
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

	public static void Properties(string path)
	{
		SHELLEXECUTEINFO info = new SHELLEXECUTEINFO
		{
			Size = Marshal.SizeOf(typeof(SHELLEXECUTEINFO)),
			Mask = 12u,
			Verb = "properties",
			File = path,
			Show = 1
		};
		if (!ShellExecuteEx(ref info))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
	}

	public static void Location(Entry entry)
	{
		LaunchInfoService.Enrich(entry);
		string text = ((!string.IsNullOrWhiteSpace(entry.LaunchTarget) && File.Exists(entry.LaunchTarget)) ? entry.LaunchTarget : entry.Path);
		if (Directory.Exists(text))
		{
			Process.Start(new ProcessStartInfo("explorer.exe", "\"" + text + "\"")
			{
				UseShellExecute = true
			});
			return;
		}
		if (File.Exists(text))
		{
			Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + text + "\"")
			{
				UseShellExecute = true
			});
			return;
		}
		throw new FileNotFoundException("文件已移动或删除", text);
	}
}
