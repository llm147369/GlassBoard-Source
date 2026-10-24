using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GlassBoard;

// 主流音乐/媒体平台识别：把系统媒体会话的 Source（AUMID 或进程名片段）翻译成友好的平台名，
// 并提供已知播放器的可执行文件名，帮助快速取到应用图标。
public static class MusicPlatforms
{
	private static readonly KeyValuePair<string, string>[] Known = new KeyValuePair<string, string>[22]
	{
		new KeyValuePair<string, string>("cloudmusic", "网易云音乐"),
		new KeyValuePair<string, string>("netease", "网易云音乐"),
		new KeyValuePair<string, string>("qqmusic", "QQ音乐"),
		new KeyValuePair<string, string>("kwmusic", "酷狗音乐"),
		new KeyValuePair<string, string>("kugou", "酷狗音乐"),
		new KeyValuePair<string, string>("kwbox", "酷我音乐"),
		new KeyValuePair<string, string>("kuwo", "酷我音乐"),
		new KeyValuePair<string, string>("soda", "汽水音乐"),
		new KeyValuePair<string, string>("luna", "汽水音乐"),
		new KeyValuePair<string, string>("folia", "Folia"),
		new KeyValuePair<string, string>("izuna", "Folia"),
		new KeyValuePair<string, string>("spotify", "Spotify"),
		new KeyValuePair<string, string>("applemusic", "Apple Music"),
		new KeyValuePair<string, string>("foobar2000", "foobar2000"),
		new KeyValuePair<string, string>("wmplayer", "Windows Media Player"),
		new KeyValuePair<string, string>("zune", "媒体播放器"),
		new KeyValuePair<string, string>("groove", "媒体播放器"),
		new KeyValuePair<string, string>("potplayer", "PotPlayer"),
		new KeyValuePair<string, string>("vlc", "VLC"),
		new KeyValuePair<string, string>("bilibili", "哔哩哔哩"),
		new KeyValuePair<string, string>("douyin", "抖音"),
		new KeyValuePair<string, string>("microsoftedge", "浏览器播放")
	};

	private static readonly KeyValuePair<string, string>[] Executables = new KeyValuePair<string, string>[12]
	{
		new KeyValuePair<string, string>("cloudmusic", "cloudmusic.exe"),
		new KeyValuePair<string, string>("netease", "cloudmusic.exe"),
		new KeyValuePair<string, string>("qqmusic", "QQMusic.exe"),
		new KeyValuePair<string, string>("kwmusic", "KwMusic.exe"),
		new KeyValuePair<string, string>("kugou", "KGMusic.exe"),
		new KeyValuePair<string, string>("soda", "SodaMusic.exe"),
		new KeyValuePair<string, string>("folia", "Folia.exe"),
		new KeyValuePair<string, string>("spotify", "Spotify.exe"),
		new KeyValuePair<string, string>("foobar2000", "foobar2000.exe"),
		new KeyValuePair<string, string>("potplayer", "PotPlayerMini64.exe"),
		new KeyValuePair<string, string>("vlc", "vlc.exe"),
		new KeyValuePair<string, string>("wmplayer", "wmplayer.exe")
	};

	// AUMID/进程名片段 → 平台友好名；识别不到返回空串
	public static string FriendlyName(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return "";
		}
		string text = source.Trim();
		// NPSMLib 直连会话可能拿不到 AUMID，桥会补 app:进程名（如 app:cloudmusic）
		if (text.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(4).Trim();
		}
		else if (text.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(4).Trim();
		}
		KeyValuePair<string, string>[] known = Known;
		for (int i = 0; i < known.Length; i++)
		{
			if (text.IndexOf(known[i].Key, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return known[i].Value;
			}
		}
		return "";
	}

	// 已知播放器的可执行文件名；识别不到返回空串（调用方回退到进程名扫描）
	public static string KnownExecutable(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return "";
		}
		string text = source;
		KeyValuePair<string, string>[] executables = Executables;
		for (int i = 0; i < executables.Length; i++)
		{
			if (text.IndexOf(executables[i].Key, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return executables[i].Value;
			}
		}
		return "";
	}

	// 音乐来源判定：黑名单制——默认放行任何播放器（系统会话类型已限 Music），
	// 只拦截浏览器与视频站等已知非音乐源，避免漏掉小众播放器（Folia/汽水等）。
	private static readonly string[] NonMusicKeys = new string[26]
	{
		"msedge", "chrome", "firefox", "opera", "brave", "qqbrowser", "360se", "360chrome",
		"sogou", "iexplore", "maxthon", "thorium", "vivaldi", "lbbrowser", "2345explorer",
		"bilibili", "douyin", "iqiyi", "youku", "tenvideo", "qqlive", "mgtv", "tudou",
		"youtube", "netflix", "primevideo"
	};

	public static bool IsMusicSource(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return false;
		}
		string text = source.Trim();
		if (text.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(4).Trim();
		}
		else if (text.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(4).Trim();
		}
		if (text.Length == 0)
		{
			return false;
		}
		string[] keys = NonMusicKeys;
		for (int i = 0; i < keys.Length; i++)
		{
			if (text.IndexOf(keys[i], StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}
		}
		return true;
	}

	private static readonly string[] BrowserMarkers = new string[13]
	{
		"msedge", "chrome", "firefox", "opera", "brave", "qqbrowser", "360se", "360chrome", "sogou", "iexplore", "maxthon", "thorium", "vivaldi"
	};

	// 扫描当前运行中的已知音乐播放器进程，返回识别键（如 "soda"）；没有则返回空串。
	// 用于"打开音乐软件但还没播放任何曲目"时的占位显示（此时 GSMTC 会话可能尚未注册）。
	public static string RunningPlatformKey()
	{
		KeyValuePair<string, string>[] executables = Executables;
		for (int i = 0; i < executables.Length; i++)
		{
			string name = executables[i].Value;
			if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				name = name.Substring(0, name.Length - 4);
			}
			try
			{
				if (Process.GetProcessesByName(name).Length != 0)
				{
					return executables[i].Key;
				}
			}
			catch (Exception)
			{
			}
		}
		return "";
	}

	// 已知播放器的主窗口标题（如网易云播放时标题形如 "晴天 - 周杰伦 - 网易云音乐"）；
	// 播放器未开或取不到返回空串。
	public static string WindowTitleFor(string key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return "";
		}
		string exe = KnownExecutable(key);
		if (exe.Length == 0)
		{
			return "";
		}
		if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			exe = exe.Substring(0, exe.Length - 4);
		}
		try
		{
			Process[] list = Process.GetProcessesByName(exe);
			for (int i = 0; i < list.Length; i++)
			{
				try
				{
					string title = list[i].MainWindowTitle;
					if (!string.IsNullOrWhiteSpace(title))
					{
						return title.Trim();
					}
				}
				catch (Exception)
				{
				}
			}
		}
		catch (Exception)
		{
		}
		return "";
	}

	// 浏览器等视频类源判定：音乐组件只认音乐会话，浏览器会话一律忽略（防止误控浏览器视频）
	public static bool IsBrowserSource(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return false;
		}
		string text = source;
		string[] markers = BrowserMarkers;
		for (int i = 0; i < markers.Length; i++)
		{
			if (text.IndexOf(markers[i], StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
		}
		return false;
	}
}
