using System;
using System.IO;
using System.Text;

namespace GlassBoard;

public class TrackMetadata
{
	public string Title;

	public string Artist;

	public string Album;

	public byte[] Cover;

	public static TrackMetadata Read(string path)
	{
		TrackMetadata trackMetadata = new TrackMetadata
		{
			Title = Path.GetFileNameWithoutExtension(path),
			Artist = "本地音乐"
		};
		try
		{
			using (FileStream fileStream = File.OpenRead(path))
			{
				byte[] array = new byte[10];
				if (fileStream.Read(array, 0, 10) == 10 && Encoding.ASCII.GetString(array, 0, 3) == "ID3" && (array[3] == 3 || array[3] == 4) && (array[5] & 0xC0) == 0)
				{
					int num = Sync(array, 6);
					if (num > 0 && num <= 8388608)
					{
						byte[] array2 = new byte[num];
						int i;
						int num2;
						for (i = 0; i < num; i += num2)
						{
							num2 = fileStream.Read(array2, i, num - i);
							if (num2 == 0)
							{
								break;
							}
						}
						int num5;
						int num4;
						for (int num3 = 0; num3 + 10 <= i; num3 = num5 + num4)
						{
							string text = Encoding.ASCII.GetString(array2, num3, 4);
							num4 = ((array[3] == 4) ? Sync(array2, num3 + 4) : Big(array2, num3 + 4));
							if (num4 <= 0 || num4 > i - num3 - 10)
							{
								break;
							}
							num5 = num3 + 10;
							bool flag = array2[num3 + 8] == 0 && array2[num3 + 9] == 0;
							if (flag)
							{
								switch (text)
								{
								case "TIT2":
								case "TPE1":
								case "TALB":
								{
									string text2 = Text(array2, num5, num4);
									if (!string.IsNullOrWhiteSpace(text2))
									{
										if (text == "TIT2")
										{
											trackMetadata.Title = text2;
										}
										else if (text == "TPE1")
										{
											trackMetadata.Artist = text2;
										}
										else
										{
											trackMetadata.Album = text2;
										}
									}
									continue;
								}
								}
							}
							if (!flag || !(text == "APIC") || trackMetadata.Cover != null)
							{
								continue;
							}
							int num6 = array2[num5];
							int j = num5 + 1;
							int num7;
							for (num7 = num5 + num4; j < num7 && array2[j] != 0; j++)
							{
							}
							j += 2;
							if (num6 == 1 || num6 == 2)
							{
								for (; j + 1 < num7 && (array2[j] != 0 || array2[j + 1] != 0); j += 2)
								{
								}
								j += 2;
							}
							else
							{
								for (; j < num7 && array2[j] != 0; j++)
								{
								}
								j++;
							}
							if (j < num7 && num7 - j <= 4194304)
							{
								trackMetadata.Cover = new byte[num7 - j];
								Buffer.BlockCopy(array2, j, trackMetadata.Cover, 0, num7 - j);
							}
						}
					}
				}
			}
			if (trackMetadata.Cover == null)
			{
				string directoryName = Path.GetDirectoryName(path);
				string[] array3 = new string[5]
				{
					Path.GetFileNameWithoutExtension(path) + ".jpg",
					Path.GetFileNameWithoutExtension(path) + ".png",
					"cover.jpg",
					"cover.png",
					"folder.jpg"
				};
				foreach (string path2 in array3)
				{
					string text3 = Path.Combine(directoryName, path2);
					if (File.Exists(text3) && new FileInfo(text3).Length <= 4194304)
					{
						trackMetadata.Cover = File.ReadAllBytes(text3);
						break;
					}
				}
			}
		}
		catch
		{
		}
		return trackMetadata;
	}

	private static int Sync(byte[] b, int p)
	{
		return ((b[p] & 0x7F) << 21) | ((b[p + 1] & 0x7F) << 14) | ((b[p + 2] & 0x7F) << 7) | (b[p + 3] & 0x7F);
	}

	private static int Big(byte[] b, int p)
	{
		return (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
	}

	private static string Text(byte[] b, int p, int count)
	{
		if (count < 2)
		{
			return "";
		}
		Encoding encoding;
		if (b[p] == 1)
		{
			encoding = Encoding.Unicode;
		}
		else if (b[p] == 2)
		{
			encoding = Encoding.BigEndianUnicode;
		}
		else
		{
			encoding = ((b[p] == 3) ? Encoding.UTF8 : Encoding.GetEncoding(28591));
		}
		return encoding.GetString(b, p + 1, count - 1).Trim('\0', '\ufeff', ' ', '\r', '\n');
	}
}
