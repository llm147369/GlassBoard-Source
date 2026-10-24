using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace GlassBoard;

internal static class CreatorAssets
{
	private const string Key = "+w2G9GqN0Dp2dREGskd8qkOktn8hoPuUTF5BN3E70+o=";

	private static string[] Identity(string key)
	{
		if (key == "Wechat")
		{
			return new string[3] { "GlassBoard.Assets.c1.dat", "62c5159608df0606563bbb8b226f85665aacd2ec4780cff09ca94a4e598458e9", "98e4c3c299ff389b1aea9d59045bc674cfa1763772de01000147f0b3b5b84ecd" };
		}
		if (key == "Alipay")
		{
			return new string[3] { "GlassBoard.Assets.c2.dat", "6752976c421694cb6d5d3a67be3355da1da7f47a4859324efb06cd4295c26ed8", "ea068219af9de972ee287fcbaad8897f080b46a6b0aee052f847c88b61420625" };
		}
		throw new ArgumentException("未知创作者资源", "key");
	}

	private static string Digest(byte[] bytes)
	{
		using SHA256 sHA = SHA256.Create();
		return BitConverter.ToString(sHA.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
	}

	internal static byte[] Decode(string key, byte[] payload)
	{
		string[] array = Identity(key);
		if (payload == null || payload.Length < 36 || payload.Length > 4194304 || (payload.Length - 20) % 16 != 0 || payload[0] != 71 || payload[1] != 66 || payload[2] != 67 || payload[3] != 49 || Digest(payload) != array[1])
		{
			throw new InvalidDataException("创作者收款资源校验失败，请重新解压原版程序。");
		}
		byte[] array2 = new byte[16];
		Buffer.BlockCopy(payload, 4, array2, 0, 16);
		byte[] array3;
		using (Aes aes = Aes.Create())
		{
			aes.Key = Convert.FromBase64String("+w2G9GqN0Dp2dREGskd8qkOktn8hoPuUTF5BN3E70+o=");
			aes.IV = array2;
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.PKCS7;
			using ICryptoTransform cryptoTransform = aes.CreateDecryptor();
			array3 = cryptoTransform.TransformFinalBlock(payload, 20, payload.Length - 20);
		}
		if (Digest(array3) != array[2])
		{
			throw new InvalidDataException("创作者收款图片校验失败。");
		}
		return array3;
	}

	internal static byte[] Read(string key)
	{
		string[] array = Identity(key);
		using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(array[0]);
		if (stream == null || stream.Length > 4194304)
		{
			throw new InvalidDataException("创作者收款资源缺失或异常。");
		}
		using MemoryStream memoryStream = new MemoryStream();
		stream.CopyTo(memoryStream);
		return Decode(key, memoryStream.ToArray());
	}
}
