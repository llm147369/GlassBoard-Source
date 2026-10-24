using System;
using System.Runtime.InteropServices;

namespace GlassBoard;

public static class SystemMedia
{
	private struct MOUSEINPUT
	{
		public int X;

		public int Y;

		public uint Data;

		public uint Flags;

		public uint Time;

		public IntPtr Extra;
	}

	private struct KEYBDINPUT
	{
		public ushort Key;

		public ushort Scan;

		public uint Flags;

		public uint Time;

		public IntPtr Extra;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct UNION
	{
		[FieldOffset(0)]
		public MOUSEINPUT Mouse;

		[FieldOffset(0)]
		public KEYBDINPUT Keyboard;
	}

	private struct INPUT
	{
		public uint Type;

		public UNION Value;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint count, INPUT[] input, int size);

	public static void Shortcut(ushort modifier, ushort key)
	{
		INPUT[] input = new INPUT[4]
		{
			new INPUT
			{
				Type = 1u,
				Value = new UNION
				{
					Keyboard = new KEYBDINPUT
					{
						Key = modifier
					}
				}
			},
			new INPUT
			{
				Type = 1u,
				Value = new UNION
				{
					Keyboard = new KEYBDINPUT
					{
						Key = key
					}
				}
			},
			new INPUT
			{
				Type = 1u,
				Value = new UNION
				{
					Keyboard = new KEYBDINPUT
					{
						Key = key,
						Flags = 2u
					}
				}
			},
			new INPUT
			{
				Type = 1u,
				Value = new UNION
				{
					Keyboard = new KEYBDINPUT
					{
						Key = modifier,
						Flags = 2u
					}
				}
			}
		};
		if (SendInput(4u, input, Marshal.SizeOf(typeof(INPUT))) != 4)
		{
			throw new InvalidOperationException("无法打开系统快捷面板");
		}
	}

	public static void Key(ushort key)
	{
		INPUT iNPUT = new INPUT
		{
			Type = 1u,
			Value = new UNION
			{
				Keyboard = new KEYBDINPUT
				{
					Key = key
				}
			}
		};
		INPUT iNPUT2 = new INPUT
		{
			Type = 1u,
			Value = new UNION
			{
				Keyboard = new KEYBDINPUT
				{
					Key = key,
					Flags = 2u
				}
			}
		};
		if (SendInput(2u, new INPUT[2] { iNPUT, iNPUT2 }, Marshal.SizeOf(typeof(INPUT))) != 2)
		{
			throw new InvalidOperationException("无法发送系统媒体键");
		}
	}
}
