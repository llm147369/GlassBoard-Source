using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace GlassBoard;

// 收起态中部歌词行（一行、实时滚动）。歌词获取按“电脑上正在播放的音乐软件”的官方
// 曲库优先，命中率远高于单一第三方库：
//   1) 网易云音乐官方公开 API（music.163.com 搜索 + /api/song/lyric 歌词，无需登录）——
//      与网易云客户端显示的歌词同源；
//   2) lrclib.net 开源歌词库兜底（免费无密钥）。
//   3) lrclib.net 开源歌词库兜底（免费无密钥）。
// 拿到同步 LRC（[mm:ss.xx]）后按播放位置推进当前句；播放器（如网易云桌面版）不向系统
// 发布播放进度时，用“本曲开始播放至今的时长”估算行号（可能随时间轻微漂移）。
// 检索失败或没有同步歌词时收起态只显示曲名+歌手。歌词文本只保存在内存，绝不写入日志。
public static class IslandLyrics
{
	public sealed class LyricLine
	{
		public double Time;
		public string Text;
	}

	private static readonly object gate = new object();
	private static List<LyricLine> lines;
	private static int activeIndex = -1;
	private static string trackKey = "";
	private static DateTime lastFetch = DateTime.MinValue;

	// 当前应显示的一句（无歌词返回空串）。必须在 UI 线程读取。
	public static string ActiveText
	{
		get
		{
			lock (gate)
			{
				return lines != null && activeIndex >= 0 && activeIndex < lines.Count ? lines[activeIndex].Text : "";
			}
		}
	}

	public static bool HasLyrics
	{
		get { lock (gate) { return lines != null && lines.Count > 0; } }
	}

	// 曲目变化时调用：清空旧歌词并异步检索新曲目。
	public static void Reset(string artist, string title)
	{
		artist = (artist ?? "").Trim();
		title = (title ?? "").Trim();
		// 无会话时的占位文案不是真实曲目：忽略且不消耗节流窗口
		if (title.Length == 0 || title == "等待音乐" || title == "选择本地音乐")
		{
			lock (gate)
			{
				lines = null;
				activeIndex = -1;
			}
			return;
		}
		string key = artist.ToLowerInvariant() + "|" + title.ToLowerInvariant();
		bool changed;
		lock (gate)
		{
			changed = key != trackKey;
			trackKey = key;
			lines = null;
			activeIndex = -1;
		}
		if (changed && title.Length > 0)
		{
			FetchAsync(artist, title);
		}
	}

	// 用播放位置推进当前句（1 秒级调用即可）。
	public static void Update(double elapsedSeconds)
	{
		lock (gate)
		{
			if (lines == null || lines.Count == 0)
			{
				activeIndex = -1;
				return;
			}
			int idx = -1;
			for (int i = 0; i < lines.Count; i++)
			{
				if (lines[i].Time >= 0.0 && lines[i].Time <= elapsedSeconds)
				{
					idx = i;
				}
				else if (lines[i].Time > elapsedSeconds)
				{
					break;
				}
			}
			activeIndex = idx;
		}
	}

	private static void FetchAsync(string artist, string title)
	{
		lock (gate)
		{
			// 10 秒节流，防止轮询风暴
			if ((DateTime.UtcNow - lastFetch).TotalSeconds < 10.0)
			{
				return;
			}
			lastFetch = DateTime.UtcNow;
		}
		System.Threading.Tasks.Task.Run(delegate
		{
			try
			{
				string src;
				List<LyricLine> result = FetchChain(artist, title, out src);
				lock (gate)
				{
					// 只接受“还是当前曲目”的结果，防止切歌后旧歌词覆盖新曲目
					string key = artist.ToLowerInvariant() + "|" + title.ToLowerInvariant();
					if (key == trackKey)
					{
						lines = result;
						activeIndex = -1;
					}
				}
				Store.Trace("LYRICS ok src=" + src + " lines=" + (result == null ? 0 : result.Count));
			}
			catch (Exception ex)
			{
				Store.Trace("LYRICS fetch failed: " + ex.GetType().Name + " " + ex.Message);
			}
		});
	}

	// 多源链：网易云官方曲库 → lrclib 开源库。
	private static List<LyricLine> FetchChain(string artist, string title, out string source)
	{
		List<LyricLine> result = TryNetease(artist, title);
		if (result != null && result.Count > 0)
		{
			source = "netease";
			return result;
		}
		result = TryLrclib(artist, title);
		source = "lrclib";
		return result;
	}

	// —— 网易云音乐官方公开 API ——
	private static List<LyricLine> TryNetease(string artist, string title)
	{
		try
		{
		// music.163.com 主域名对该接口返回 400 参数错误；interface.music.163.com 为同库可用域名
		string url = "https://interface.music.163.com/api/search/get/web?s=" + Uri.EscapeDataString(title + " " + artist) + "&type=1&offset=0&total=true&limit=10";
		NeteaseSearchResp search = GetJson<NeteaseSearchResp>(url, "https://music.163.com/", "os=pc; appver=2.0.2");
		if (search == null || search.Result == null || search.Result.Songs == null || search.Result.Songs.Count == 0)
		{
			// 主搜索接口偶发空结果：换 cloudsearch 接口再试一次
			search = GetJson<NeteaseSearchResp>("https://music.163.com/api/cloudsearch/pc?s=" + Uri.EscapeDataString(title + " " + artist) + "&type=1&limit=10", "https://music.163.com/", "os=pc; appver=2.0.2");
		}
		if (search == null || search.Result == null || search.Result.Songs == null || search.Result.Songs.Count == 0)
		{
			Store.Trace("LYRICS netease search empty resultNull=" + (search == null || search.Result == null) + " qlen=" + (title.Length + artist.Length));
			return null;
		}
		long bestId = 0;
		int bestScore = -1;
		string t = title.ToLowerInvariant();
		string a = artist.ToLowerInvariant();
		foreach (NeteaseSong song in search.Result.Songs)
		{
			int score = 0;
			string sn = (song.Name ?? "").Trim().ToLowerInvariant();
			string sa = "";
			if (song.Artists != null && song.Artists.Count > 0)
			{
				sa = (song.Artists[0].Name ?? "").Trim().ToLowerInvariant();
			}
			if (sn == t)
			{
				score += 4;
			}
			else if (sn.Length > 0 && (sn.Contains(t) || t.Contains(sn)))
			{
				score += 2;
			}
			if (sa == a)
			{
				score += 3;
			}
			else if (a.Length > 0 && sa.Length > 0 && (sa.Contains(a) || a.Contains(sa)))
			{
				score += 1;
			}
			if (score > bestScore)
			{
				bestScore = score;
				bestId = song.Id;
			}
		}
		if (bestId == 0)
		{
			return null;
		}
		NeteaseLyricResp lyric = GetJson<NeteaseLyricResp>("https://music.163.com/api/song/lyric?id=" + bestId + "&lv=1&kv=1&tv=-1", "https://music.163.com/", "appver=2.0.2; os=pc");
		if (lyric == null || lyric.Lrc == null || string.IsNullOrEmpty(lyric.Lrc.Lyric))
		{
			Store.Trace("LYRICS netease lyric empty id=" + bestId);
			return null;
		}
		return ParseLrc(lyric.Lrc.Lyric);
		}
		catch (Exception ex)
		{
			Store.Trace("LYRICS netease fail: " + ex.GetType().Name + " " + ex.Message);
			return null;
		}
	}


	// —— lrclib.net 开源歌词库（免费无密钥）——
	private static List<LyricLine> TryLrclib(string artist, string title)
	{
		string url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title);
		if (artist.Length > 0)
		{
			url += "&artist_name=" + Uri.EscapeDataString(artist);
		}
		List<LrclibHit> hits = GetJson<List<LrclibHit>>(url, null, null);
		if (hits == null || hits.Count == 0)
		{
			return null;
		}
		string t = title.ToLowerInvariant();
		string a = artist.ToLowerInvariant();
		LrclibHit best = null;
		int bestScore = -1;
		foreach (LrclibHit hit in hits)
		{
			if (string.IsNullOrEmpty(hit.SyncedLyrics))
			{
				continue;
			}
			int score = 0;
			string ht = (hit.TrackName ?? "").Trim().ToLowerInvariant();
			string ha = (hit.ArtistName ?? "").Trim().ToLowerInvariant();
			if (ht == t)
			{
				score += 4;
			}
			else if (ht.Length > 0 && (ht.Contains(t) || t.Contains(ht)))
			{
				score += 2;
			}
			if (ha == a)
			{
				score += 3;
			}
			else if (a.Length > 0 && ha.Length > 0 && (ha.Contains(a) || a.Contains(ha)))
			{
				score += 1;
			}
			if (score > bestScore)
			{
				bestScore = score;
				best = hit;
			}
		}
		return best == null ? null : ParseLrc(best.SyncedLyrics);
	}

	// 通用 JSON GET（DataContractJsonSerializer，与项目其余配置一致）；503 服务器忙时重试一次
	private static T GetJson<T>(string url, string referer, string cookie)
	{
		for (int attempt = 0; attempt < 2; attempt++)
		{
			try
			{
				HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
				req.Timeout = 6000;
				req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
				req.Accept = "application/json, text/plain, */*";
				req.Headers["Accept-Language"] = "zh-CN,zh;q=0.9";
				if (!string.IsNullOrEmpty(referer))
				{
					req.Referer = referer;
				}
				if (!string.IsNullOrEmpty(cookie))
				{
					req.Headers["Cookie"] = cookie;
				}
				using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
				using (Stream stream = resp.GetResponseStream())
				{
					DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(T));
					return (T)ser.ReadObject(stream);
				}
			}
			catch (WebException)
			{
				if (attempt == 0)
				{
					System.Threading.Thread.Sleep(1000);
					continue;
				}
				throw;
			}
		}
		return default(T);
	}

	// LRC 解析：行内所有 [mm:ss] / [mm:ss.xx] 标签都解析为独立句，文本取最后一个标签之后。
	private static List<LyricLine> ParseLrc(string lrc)
	{
		List<LyricLine> result = new List<LyricLine>();
		if (string.IsNullOrEmpty(lrc))
		{
			return result;
		}
		Regex tag = new Regex(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]");
		foreach (string raw in lrc.Replace("\r", "").Split('\n'))
		{
			string s = raw.Trim();
			if (s.Length == 0)
			{
				continue;
			}
			MatchCollection matches = tag.Matches(s);
			if (matches.Count == 0)
			{
				continue;
			}
			Match last = matches[matches.Count - 1];
			string text = s.Substring(last.Index + last.Length).Trim();
			if (text.Length == 0)
			{
				continue;
			}
			foreach (Match m in matches)
			{
				double sec = int.Parse(m.Groups[1].Value) * 60.0 + int.Parse(m.Groups[2].Value);
				if (m.Groups[3].Value.Length > 0)
				{
					string frac = m.Groups[3].Value.PadRight(3, '0');
					sec += int.Parse(frac) / 1000.0;
				}
				result.Add(new LyricLine { Time = sec, Text = text });
			}
		}
		result.Sort(delegate (LyricLine x, LyricLine y) { return x.Time.CompareTo(y.Time); });
		return result;
	}

	// —— 响应数据契约 ——
	[DataContract]
	private sealed class LrclibHit
	{
		[DataMember(Name = "trackName")]
		public string TrackName = null;

		[DataMember(Name = "artistName")]
		public string ArtistName = null;

		[DataMember(Name = "syncedLyrics")]
		public string SyncedLyrics = null;
	}

	[DataContract]
	private sealed class NeteaseSearchResp
	{
		[DataMember(Name = "result")]
		public NeteaseResult Result = null;

	}

	[DataContract]
	private sealed class NeteaseResult
	{
		[DataMember(Name = "songs")]
		public List<NeteaseSong> Songs = null;
	}

	[DataContract]
	private sealed class NeteaseSong
	{
		[DataMember(Name = "id")]
		public long Id;

		[DataMember(Name = "name")]
		public string Name = null;

		[DataMember(Name = "artists")]
		public List<NeteaseArtist> Artists = null;
	}

	[DataContract]
	private sealed class NeteaseArtist
	{
		[DataMember(Name = "name")]
		public string Name = null;
	}

	[DataContract]
	private sealed class NeteaseLyricResp
	{
		[DataMember(Name = "lrc")]
		public NeteaseLrc Lrc = null;

	}

	[DataContract]
	private sealed class NeteaseLrc
	{
		[DataMember(Name = "lyric")]
		public string Lyric = null;
	}

}
