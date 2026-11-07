using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace GlassBoard;

/// <summary>
/// 封面救援三级管线：网易云本地 API → 播放器窗口 UIA 截图 → iTunes 检索。
/// 任一命中即落盘缓存（%LOCALAPPDATA%\GlassBoard\artwork），换歌回来秒开。
/// </summary>
public static class CoverRescue
{
	public const int MaxImageBytes = 8388608;

	public static string LastStatus = "待命";

	static CoverRescue()
	{
		try
		{
			ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
		}
		catch
		{
		}
	}

	// ---------------- 磁盘缓存 ----------------

	private static string CacheDir
	{
		get
		{
			string text = Path.Combine(Store.Folder, "artwork");
			try
			{
				Directory.CreateDirectory(text);
			}
			catch
			{
			}
			return text;
		}
	}

	private static string HashKey(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return null;
		}
		using MD5 md5 = MD5.Create();
		return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant();
	}

	public static byte[] DiskLoad(string key)
	{
		try
		{
			string path = Path.Combine(CacheDir, HashKey(key) + ".dat");
			if (File.Exists(path))
			{
				byte[] array = File.ReadAllBytes(path);
				if (array.Length > 0 && array.Length <= MaxImageBytes && LooksLikeImage(array))
				{
					return array;
				}
			}
		}
		catch
		{
		}
		return null;
	}

	public static void DiskSave(string key, byte[] bytes)
	{
		try
		{
			if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes)
			{
				return;
			}
			string text = Path.Combine(CacheDir, HashKey(key) + ".dat");
			if (File.Exists(text))
			{
				return;
			}
			string tmp = text + ".tmp";
			File.WriteAllBytes(tmp, bytes);
			File.Move(tmp, text);
			string[] files = Directory.GetFiles(CacheDir, "*.dat");
			if (files.Length > 400)
			{
				foreach (string item in files.OrderBy(File.GetLastWriteTimeUtc).Take(files.Length - 200))
				{
					try
					{
						File.Delete(item);
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
	}

	// ---------------- 主入口 ----------------

	public static Task<byte[]> FetchAsync(MediaSnapshot session, int startTier)
	{
		return Task.Run(delegate
		{
			if (session == null)
			{
				return null;
			}
			byte[] cached = DiskLoad(session.Key) ?? DiskLoad(SoftKey(session));
			if (cached != null)
			{
				LastStatus = "磁盘缓存命中";
				return cached;
			}
			for (int tier = Math.Max(0, startTier); tier <= 2; tier++)
			{
				try
				{
					byte[] result;
					if (tier == 0)
					{
						result = NeteaseLocal(session);
					}
					else if (tier == 1)
					{
						result = CapturePlayerWindow(session.Source);
					}
					else
					{
						result = ItunesSearch(session.Title, session.Artist);
					}
					if (result != null && LooksLikeImage(result))
					{
						DiskSave(session.Key, result);
						DiskSave(SoftKey(session), result);
						return result;
					}
				}
				catch (Exception ex)
				{
					LastStatus = "第" + tier + "层异常：" + ex.GetBaseException().Message;
				}
			}
			return null;
		});
	}

	private static string SoftKey(MediaSnapshot session)
	{
		string text = (session.Title ?? "").Trim() + "|" + (session.Artist ?? "").Trim();
		return ((text.Length > 1) ? ("soft:" + text) : "");
	}

	// ---------------- 第 0 层：网易云本地 API ----------------

	private static byte[] NeteaseLocal(MediaSnapshot session)
	{
		string source = session.Source ?? "";
		if (source.IndexOf("cloudmusic", StringComparison.OrdinalIgnoreCase) < 0 && source.IndexOf("netease", StringComparison.OrdinalIgnoreCase) < 0 && source.IndexOf("网易云", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return null;
		}
		string json = HttpGetText("http://127.0.0.1:27232/player/play", 2500);
		if (json == null)
		{
			LastStatus = "网易云本地 API 未响应（127.0.0.1:27232）";
			return null;
		}
		int num = json.IndexOf("\"playingTrack\"", StringComparison.Ordinal);
		if (num < 0)
		{
			LastStatus = "网易云本地 API 未找到 playingTrack";
			return null;
		}
		string text = json.Substring(num);
		string track = RegexField(text, "\"name\"");
		if (track.Length == 0)
		{
			LastStatus = "网易云本地 API 曲目名为空";
			return null;
		}
		if (Similarity(track, session.Title ?? "") < 0.75)
		{
			LastStatus = "网易云本地 API 曲目不匹配（" + track + "）";
			return null;
		}
		string url = RegexField(text, "\"picUrl\"");
		if (url.Length == 0)
		{
			LastStatus = "网易云本地 API 无封面地址";
			return null;
		}
		if (url.StartsWith("//"))
		{
			url = "http:" + url;
		}
		byte[] bytes = HttpGetBytes(url, 10000);
		if (bytes == null)
		{
			LastStatus = "网易云封面下载失败：" + url;
			return null;
		}
		LastStatus = "网易云本地 API 命中（" + track + "）";
		return bytes;
	}

	// ---------------- 第 1 层：播放器窗口 UIA 截图 ----------------

	private static byte[] CapturePlayerWindow(string source)
	{
		IntPtr hwnd = FindPlayerHwnd(source);
		if (hwnd == IntPtr.Zero)
		{
			LastStatus = "未找到播放器窗口";
			return null;
		}
		if (!GetWindowRect(hwnd, out RECT rect))
		{
			LastStatus = "播放器窗口矩形无效";
			return null;
		}
		int winW = rect.Right - rect.Left;
		int winH = rect.Bottom - rect.Top;
		if (winW < 260 || winH < 200)
		{
			LastStatus = "播放器窗口过小";
			return null;
		}
		Rect best = Rect.Empty;
		double bestScore = 0.0;
		try
		{
			AutomationElement window = AutomationElement.FromHandle(hwnd);
			if (window != null)
			{
				AutomationElementCollection images = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image));
				foreach (AutomationElement element in images)
				{
					try
					{
						Rect r = element.Current.BoundingRectangle;
						if (r.IsEmpty)
						{
							continue;
						}
						if (r.Width < 36.0 || r.Height < 36.0)
						{
							continue;
						}
						if (r.Width > 1024.0 || r.Height > 1024.0)
						{
							continue;
						}
						double ratio = r.Width / r.Height;
						if (ratio < 0.72 || ratio > 1.28)
						{
							continue;
						}
						double score = r.Width * r.Height;
						if (r.Bottom >= (double)rect.Bottom - 220.0)
						{
							score *= 3.0;
						}
						if (r.Left <= (double)rect.Left + 340.0)
						{
							score *= 2.0;
						}
						if (score > bestScore)
						{
							bestScore = score;
							best = r;
						}
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
		if (best.IsEmpty)
		{
			LastStatus = "播放器窗口内未找到封面控件（UIA）";
			return null;
		}
		Rectangle crop = new Rectangle(Math.Max(0, (int)Math.Round(best.X - rect.Left)), Math.Max(0, (int)Math.Round(best.Y - rect.Top)), Math.Min((int)Math.Round(best.Width), winW), Math.Min((int)Math.Round(best.Height), winH));
		crop.Intersect(new Rectangle(0, 0, winW, winH));
		if (crop.Width < 32 || crop.Height < 32)
		{
			LastStatus = "封面裁切区域过小";
			return null;
		}
		byte[] bytes = CaptureViaPrintWindow(hwnd, rect, crop);
		if (bytes != null)
		{
			LastStatus = "UIA 窗口截图命中（PrintWindow）";
			return bytes;
		}
		if (IsIconic(hwnd))
		{
			LastStatus = "播放器窗口最小化且 PrintWindow 无内容";
			return null;
		}
		bytes = CaptureViaScreen(rect, crop);
		if (bytes != null)
		{
			LastStatus = "UIA 窗口截图命中（屏幕抓取）";
			return bytes;
		}
		LastStatus = "播放器窗口截图空白（可能被遮挡）";
		return null;
	}

	private static bool WindowUsable(IntPtr hwnd)
	{
		if (!GetWindowRect(hwnd, out RECT rect))
		{
			return false;
		}
		return rect.Right - rect.Left >= 260 && rect.Bottom - rect.Top >= 200;
	}

	private static IntPtr FindPlayerHwnd(string source)
	{
		IntPtr best = IntPtr.Zero;
		string known = MusicPlatforms.KnownExecutable(source ?? "");
		if (known.Length > 0)
		{
			try
			{
				Process[] processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(known));
				foreach (Process process in processes)
				{
					try
					{
						if (process.MainWindowHandle != IntPtr.Zero && WindowUsable(process.MainWindowHandle))
						{
							best = process.MainWindowHandle;
						}
					}
					catch
					{
					}
					finally
					{
						process.Dispose();
					}
				}
			}
			catch
			{
			}
			if (best != IntPtr.Zero)
			{
				return best;
			}
		}
		string src = (source ?? "").Trim();
		if (src.Length >= 4 && (src.StartsWith("app:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)))
		{
			src = src.Substring(4);
		}
		int num = 0;
		foreach (Process process2 in Process.GetProcesses())
		{
			try
			{
				if (process2.Id == Process.GetCurrentProcess().Id)
				{
					continue;
				}
				string processName = process2.ProcessName;
				if (processName.Length < 3 || src.IndexOf(processName, StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}
				if (process2.MainWindowHandle == IntPtr.Zero || !WindowUsable(process2.MainWindowHandle))
				{
					continue;
				}
				if (processName.Length > num)
				{
					num = processName.Length;
					best = process2.MainWindowHandle;
				}
			}
			catch
			{
			}
			finally
			{
				process2.Dispose();
			}
		}
		return best;
	}

	private static byte[] CaptureViaPrintWindow(IntPtr hwnd, RECT rect, Rectangle crop)
	{
		int width = rect.Right - rect.Left;
		int height = rect.Bottom - rect.Top;
		using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
		{
			using (Graphics graphics = Graphics.FromImage(bitmap))
			{
				IntPtr hdc = graphics.GetHdc();
				try
				{
					PrintWindow(hwnd, hdc, 2u);
				}
				finally
				{
					graphics.ReleaseHdc(hdc);
				}
			}
			using (Bitmap clone = bitmap.Clone(crop, PixelFormat.Format32bppArgb))
			{
				if (IsBlank(clone))
				{
					return null;
				}
				return Encode(clone);
			}
		}
	}

	private static byte[] CaptureViaScreen(RECT rect, Rectangle crop)
	{
		int x = rect.Left + crop.X;
		int y = rect.Top + crop.Y;
		using (Bitmap bitmap = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb))
		{
			using (Graphics graphics = Graphics.FromImage(bitmap))
			{
				graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(crop.Width, crop.Height), CopyPixelOperation.SourceCopy);
			}
			if (IsBlank(bitmap))
			{
				return null;
			}
			return Encode(bitmap);
		}
	}

	private static bool IsBlank(Bitmap bitmap)
	{
		int width = bitmap.Width;
		int height = bitmap.Height;
		int min = 255;
		int max = 0;
		for (int i = 0; i < 7; i++)
		{
			for (int j = 0; j < 7; j++)
			{
				Color color = bitmap.GetPixel(Math.Min(width - 1, (width - 1) * i / 6), Math.Min(height - 1, (height - 1) * j / 6));
				int v = (color.R + color.G + color.B) / 3;
				if (v < min)
				{
					min = v;
				}
				if (v > max)
				{
					max = v;
				}
			}
		}
		return max - min < 10;
	}

	private static byte[] Encode(Bitmap bitmap)
	{
		ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault((ImageCodecInfo e) => e.FormatID == ImageFormat.Jpeg.Guid) ?? ImageCodecInfo.GetImageEncoders()[0];
		using (EncoderParameters parameters = new EncoderParameters(1))
		{
			parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 86L);
			using (MemoryStream stream = new MemoryStream())
			{
				bitmap.Save(stream, codec, parameters);
				return stream.ToArray();
			}
		}
	}

	// ---------------- 第 2 层：iTunes 检索 ----------------

	private static byte[] ItunesSearch(string title, string artist)
	{
		if (string.IsNullOrWhiteSpace(title))
		{
			LastStatus = "iTunes 检索无曲目名";
			return null;
		}
		string term = string.Join(" ", new string[2] { artist, title }.Where(delegate(string x)
		{
			return !string.IsNullOrWhiteSpace(x);
		}).Select(delegate(string x)
		{
			return x.Trim();
		}).Distinct());
		if (term.Length > 140)
		{
			term = term.Substring(0, 140);
		}
		string json = HttpGetText("https://itunes.apple.com/search?term=" + Uri.EscapeDataString(term) + "&media=music&entity=song&limit=10&country=cn", 9000);
		if (json == null)
		{
			LastStatus = "iTunes 检索网络不可用";
			return null;
		}
		MatchCollection matches = Regex.Matches(json, "\"artworkUrl100\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
		foreach (Match match in matches)
		{
			string prefix = json.Substring(0, match.Index);
			string trackName = LastField(prefix, "\"trackName\"");
			if (trackName.Length == 0)
			{
				continue;
			}
			double titleSim = Similarity(trackName, title);
			if (titleSim < 0.8)
			{
				continue;
			}
			if (!string.IsNullOrWhiteSpace(artist))
			{
				double artistSim = Similarity(LastField(prefix, "\"artistName\""), artist);
				if (artistSim < 0.55 && titleSim < 0.9)
				{
					continue;
				}
			}
			byte[] bytes = DownloadArtwork(match.Groups[1].Value);
			if (bytes != null)
			{
				LastStatus = "iTunes 检索命中（" + trackName + "）";
				return bytes;
			}
		}
		LastStatus = "iTunes 检索无匹配";
		return null;
	}

	private static byte[] DownloadArtwork(string url)
	{
		string text = JsonUnescape(url);
		if (text.Length == 0)
		{
			return null;
		}
		text = text.Replace("100x100bb", "600x600bb").Replace("100x100", "600x600");
		if (text.StartsWith("//"))
		{
			text = "http:" + text;
		}
		return HttpGetBytes(text, 10000);
	}

	// ---------------- HTTP ----------------

	private static string HttpGetText(string url, int timeoutMs)
	{
		try
		{
			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
			request.Timeout = timeoutMs;
			request.ReadWriteTimeout = timeoutMs;
			request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) GlassBoard/1.0";
			request.Accept = "*/*";
			using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
			using (Stream stream = response.GetResponseStream())
			using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
			{
				StringBuilder builder = new StringBuilder();
				char[] buffer = new char[8192];
				int read;
				while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
				{
					if (builder.Length + read > 524288)
					{
						break;
					}
					builder.Append(buffer, 0, read);
				}
				return builder.ToString();
			}
		}
		catch
		{
			return null;
		}
	}

	private static byte[] HttpGetBytes(string url, int timeoutMs)
	{
		try
		{
			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
			request.Timeout = timeoutMs;
			request.ReadWriteTimeout = timeoutMs;
			request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) GlassBoard/1.0";
			using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
			using (Stream stream = response.GetResponseStream())
			using (MemoryStream memory = new MemoryStream())
			{
				byte[] buffer = new byte[16384];
				int read;
				while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
				{
					if (memory.Length + read > MaxImageBytes)
					{
						return null;
					}
					memory.Write(buffer, 0, read);
				}
				byte[] array = memory.ToArray();
				return (LooksLikeImage(array) ? array : null);
			}
		}
		catch
		{
			return null;
		}
	}

	public static bool LooksLikeImage(byte[] bytes)
	{
		if (bytes == null || bytes.Length < 12)
		{
			return false;
		}
		if (bytes[0] == 255 && bytes[1] == 216)
		{
			return true;
		}
		if (bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71)
		{
			return true;
		}
		if (bytes[0] == 71 && bytes[1] == 73 && bytes[2] == 70)
		{
			return true;
		}
		if (bytes[0] == 66 && bytes[1] == 77)
		{
			return true;
		}
		return false;
	}

	// ---------------- 文本工具 ----------------

	private static string RegexField(string json, string field)
	{
		Match match = Regex.Match(json, field + "\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
		if (!match.Success)
		{
			return "";
		}
		return JsonUnescape(match.Groups[1].Value);
	}

	private static string LastField(string json, string field)
	{
		MatchCollection matches = Regex.Matches(json, field + "\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
		if (matches.Count == 0)
		{
			return "";
		}
		return JsonUnescape(matches[matches.Count - 1].Groups[1].Value);
	}

	private static string JsonUnescape(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		return Regex.Replace(s, "\\\\(u[0-9a-fA-F]{4}|.)", delegate(Match m)
		{
			string v = m.Groups[1].Value;
			if (v.Length == 5 && v[0] == 'u')
			{
				return ((char)Convert.ToInt32(v.Substring(1), 16)).ToString();
			}
			char c = v[0];
			switch (c)
			{
			case 'n':
				return "\n";
			case 'r':
				return "\r";
			case 't':
				return "\t";
			case 'b':
				return "\b";
			case 'f':
				return "\f";
			case '/':
				return "/";
			default:
				return v;
			}
		});
	}

	private static string Normalize(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}
		string s = text.ToLowerInvariant();
		StringBuilder builder = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			if (char.IsLetterOrDigit(c))
			{
				builder.Append(c);
			}
		}
		return builder.ToString();
	}

	private static int Levenshtein(string a, string b)
	{
		int[] prev = new int[b.Length + 1];
		int[] curr = new int[b.Length + 1];
		for (int j = 0; j <= b.Length; j++)
		{
			prev[j] = j;
		}
		for (int i = 1; i <= a.Length; i++)
		{
			curr[0] = i;
			for (int j = 1; j <= b.Length; j++)
			{
				int cost = ((a[i - 1] == b[j - 1]) ? 0 : 1);
				curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
			}
			int[] tmp = prev;
			prev = curr;
			curr = tmp;
		}
		return prev[b.Length];
	}

	private static double Similarity(string a, string b)
	{
		string x = Normalize(a);
		string y = Normalize(b);
		if (x.Length == 0 || y.Length == 0)
		{
			return 0.0;
		}
		if (x.Length >= 4 && (x.Contains(y) || y.Contains(x)))
		{
			return 1.0;
		}
		int max = Math.Max(x.Length, y.Length);
		return 1.0 - (double)Levenshtein(x, y) / max;
	}

	// ---------------- P/Invoke ----------------

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsIconic(IntPtr hWnd);
}
