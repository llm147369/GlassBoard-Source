using System;
using System.Security.Cryptography;

namespace GlassBoard;

public static class CoverFingerprint
{
	public static string Of(string text)
	{
		using SHA256 sHA = SHA256.Create();
		byte[] array = new byte[4096];
		for (int i = 0; i < text.Length; i += 2048)
		{
			int num = Math.Min(2048, text.Length - i);
			for (int j = 0; j < num; j++)
			{
				char c = text[i + j];
				array[j * 2] = (byte)c;
				array[j * 2 + 1] = (byte)((int)c >> 8);
			}
			sHA.TransformBlock(array, 0, num * 2, array, 0);
		}
		sHA.TransformFinalBlock(new byte[0], 0, 0);
		return BitConverter.ToString(sHA.Hash);
	}
}
