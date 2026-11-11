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
		// 逐字时间戳（相对句开始，秒）；null=无逐字数据（按字数均分推进）
		public List<double> WordTimes;
	}

	private static readonly object gate = new object();
	private static List<LyricLine> lines;
	private static int activeIndex = -1;
	private static string trackKey = "";
	private static DateTime lastFetch = DateTime.MinValue;
	private static string lastFetchKey = "";
	private static double offsetMs = 0.0;
	private static bool hasOffsetTag = false;

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

	// 当前句开始时间（秒）；无歌词返回 -1
	public static double LineStart
	{
		get
		{
			lock (gate)
			{
				return lines != null && activeIndex >= 0 && activeIndex < lines.Count ? lines[activeIndex].Time : -1.0;
			}
		}
	}

	// 当前句结束时间（=下一句开始；最后一句按字数估算每字 0.32s）；无歌词返回 -1
	public static double LineEnd
	{
		get
		{
			lock (gate)
			{
				if (lines == null || activeIndex < 0 || activeIndex >= lines.Count)
				{
					return -1.0;
				}
				double t = lines[activeIndex].Time;
				if (activeIndex + 1 < lines.Count)
				{
					return lines[activeIndex + 1].Time;
				}
				string text = lines[activeIndex].Text ?? "";
				return t + Math.Max(2.0, 0.32 * text.Length);
			}
		}
	}

	// 当前句的逐字开始时间（相对句开始，秒）；无逐字数据返回 null。
	public static List<double> ActiveWordTimes
	{
		get
		{
			lock (gate)
			{
				return lines != null && activeIndex >= 0 && activeIndex < lines.Count ? lines[activeIndex].WordTimes : null;
			}
		}
	}

	// LRC 文件自带 [offset:±ms] 整体偏移（毫秒）；无标签返回 0。
	public static double LrcOffsetMs
	{
		get { lock (gate) { return offsetMs; } }
	}

	// 曲目变化时调用：清空旧歌词并异步检索新曲目。
	public static void Reset(string artist, string title, string album = null, double durationSec = 0.0)
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
			offsetMs = 0.0;
			hasOffsetTag = false;
		}
		if (changed && title.Length > 0)
		{
			FetchAsync(artist, title, album ?? "", durationSec);
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

	private static void FetchAsync(string artist, string title, string album, double durationSec)
	{
		// 切歌立即请求；同一曲目的重复轮询才受 10 秒节流限制。
		lock (gate)
		{
			if (trackKey == lastFetchKey && (DateTime.UtcNow - lastFetch).TotalSeconds < 10.0)
			{
				return;
			}
			lastFetchKey = trackKey;
			lastFetch = DateTime.UtcNow;
		}
		// 本地缓存命中：直接应用，切回听过的歌立即出歌词（网络链只用于未缓存曲目）
		List<LyricLine> cached = TryCacheRead(artist, title);
		if (cached != null)
		{
			lock (gate)
			{
				string key = artist.ToLowerInvariant() + "|" + title.ToLowerInvariant();
				if (key == trackKey)
				{
					lines = cached;
					activeIndex = -1;
				}
			}
			Store.Trace("LYRICS ok src=cache lines=" + cached.Count);
			return;
		}
		System.Threading.Tasks.Task.Run(delegate
		{
			try
			{
				string src;
				List<LyricLine> result = FetchChain(artist, title, album, durationSec, out src);
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
				if (result != null && result.Count > 0)
				{
					TryCacheWrite(artist, title, result);
				}
				Store.Trace("LYRICS ok src=" + src + " lines=" + (result == null ? 0 : result.Count));
			}
			catch (Exception ex)
			{
				Store.Trace("LYRICS fetch failed: " + ex.GetType().Name + " " + ex.Message);
			}
		});
	}

	// —— 本地歌词缓存（Store.Folder\lyrics\{sha1}.json，DataContractJsonSerializer）——
	private static string CachePath(string artist, string title)
	{
		string key = (artist.ToLowerInvariant() + "|" + title.ToLowerInvariant());
		byte[] hash = System.Security.Cryptography.SHA1.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
		string hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
		string dir = Path.Combine(Store.Folder, "lyrics");
		try
		{
			if (!Directory.Exists(dir))
			{
				Directory.CreateDirectory(dir);
			}
		}
		catch
		{
		}
		return Path.Combine(dir, hex + ".json");
	}

	private static List<LyricLine> TryCacheRead(string artist, string title)
	{
		try
		{
			string path = CachePath(artist, title);
			if (!File.Exists(path))
			{
				return null;
			}
			byte[] raw = File.ReadAllBytes(path);
			if (raw.Length == 0)
			{
				return null;
			}
			using (MemoryStream ms = new MemoryStream(raw))
			{
				DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(List<LyricLine>));
				return (List<LyricLine>)ser.ReadObject(ms);
			}
		}
		catch
		{
			return null;
		}
	}

	private static void TryCacheWrite(string artist, string title, List<LyricLine> result)
	{
		try
		{
			string path = CachePath(artist, title);
			using (MemoryStream ms = new MemoryStream())
			{
				DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(List<LyricLine>));
				ser.WriteObject(ms, result);
				File.WriteAllBytes(path, ms.ToArray());
			}
		}
		catch
		{
		}
	}

	// 多源并行抢答：网易云官方曲库、QQ 音乐曲库、lrclib 同时请求，先到先用；
	// 质量排序 netease(yrc>lrc) > qq(qrc>lrc) > lrclib——只有更优结果才会替换已显示歌词。
	private static List<LyricLine> FetchChain(string artist, string title, string album, double durationSec, out string source)
	{
		List<LyricLine> ne = null;
		List<LyricLine> qq = null;
		List<LyricLine> lb = null;
		List<LyricLine> neLrc = null;
		List<LyricLine> qqLrc = null;
		System.Threading.Tasks.Task neTask = System.Threading.Tasks.Task.Run(delegate
		{
			try { ne = TryNetease(artist, title, album, durationSec); }
			catch { ne = null; }
		});
		System.Threading.Tasks.Task qqTask = System.Threading.Tasks.Task.Run(delegate
		{
			try
			{
				qq = TryQQMusic(artist, title, album, durationSec);
				if (qq != null && qq.Count > 0 && (qq[0].WordTimes == null || qq[0].WordTimes.Count == 0))
				{
					qqLrc = qq;
					qq = null;
				}
			}
			catch { qq = null; qqLrc = null; }
		});
		System.Threading.Tasks.Task lbTask = System.Threading.Tasks.Task.Run(delegate
		{
			try { lb = TryLrclib(artist, title); }
			catch { lb = null; }
		});
		try
		{
			System.Threading.Tasks.Task.WaitAll(new[] { neTask, qqTask, lbTask }, 6000);
		}
		catch
		{
		}
		if (ne != null && ne.Count > 0)
		{
			source = "netease";
			return ne;
		}
		if (qq != null && qq.Count > 0)
		{
			source = "qq";
			return qq;
		}
		if (lb != null && lb.Count > 0)
		{
			source = "lrclib";
			return lb;
		}
		if (qqLrc != null && qqLrc.Count > 0)
		{
			source = "qq";
			return qqLrc;
		}
		source = "none";
		return null;
	}

	// —— 网易云音乐官方公开 API ——
	private static List<LyricLine> TryNetease(string artist, string title, string album, double durationSec)
	{
		try
		{
		// music.163.com 主域名对该接口返回 400 参数错误；interface.music.163.com 为同库可用域名
		string url = "https://interface.music.163.com/api/search/get/web?s=" + Uri.EscapeDataString(title + " " + artist) + "&type=1&offset=0&total=true&limit=20";
		NeteaseSearchResp search = GetJson<NeteaseSearchResp>(url, "https://music.163.com/", "os=pc; appver=2.0.2");
		if (search == null || search.Result == null || search.Result.Songs == null || search.Result.Songs.Count == 0)
		{
			// 主搜索接口偶发空结果：换 cloudsearch 接口再试一次
			search = GetJson<NeteaseSearchResp>("https://music.163.com/api/cloudsearch/pc?s=" + Uri.EscapeDataString(title + " " + artist) + "&type=1&limit=20", "https://music.163.com/", "os=pc; appver=2.0.2");
		}
		if (search == null || search.Result == null || search.Result.Songs == null || search.Result.Songs.Count == 0)
		{
			Store.Trace("LYRICS netease search empty resultNull=" + (search == null || search.Result == null) + " qlen=" + (title.Length + artist.Length));
			return null;
		}
		// 打分：标题 4 / 部分 2；歌手 3 / 部分 1；专辑 2；时长 ±10s 内 2（否则 -2）
		// 取前两名候选，第一候选歌词不可用（纯音乐/空）时试第二候选——大幅降低选错版本概率。
		string t = title.ToLowerInvariant();
		string a = artist.ToLowerInvariant();
		string al = (album ?? "").ToLowerInvariant();
		long bestId = 0;
		long secondId = 0;
		int bestScore = -1;
		int secondScore = -1;
		foreach (NeteaseSong song in search.Result.Songs)
		{
			int score = 0;
			string sn = (song.Name ?? "").Trim().ToLowerInvariant();
			string sa = "";
			if (song.Artists != null && song.Artists.Count > 0)
			{
				sa = (song.Artists[0].Name ?? "").Trim().ToLowerInvariant();
			}
			string sg = song.Album != null ? (song.Album.Name ?? "").Trim().ToLowerInvariant() : "";
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
			if (al.Length > 0 && sg.Length > 0 && (sg == al || sg.Contains(al) || al.Contains(sg)))
			{
				score += 2;
			}
			if (durationSec > 0.0 && song.Duration > 0)
			{
				// 只奖不罚：播放器上报的时长可能失真（如汽水音乐 GSMTC 报 99 秒假时长），
				// 时长不匹配不扣分，防错配交给标题/歌手/专辑。
				double dur = song.Duration / 1000.0;
				if (Math.Abs(dur - durationSec) <= 10.0)
				{
					score += 2;
				}
			}
			if (score > bestScore)
			{
				secondScore = bestScore;
				secondId = bestId;
				bestScore = score;
				bestId = song.Id;
			}
			else if (score > secondScore)
			{
				secondScore = score;
				secondId = song.Id;
			}
		}
		if (bestId == 0)
		{
			return null;
		}
		List<LyricLine> result = TryNeteaseLyric(bestId);
		if ((result == null || result.Count == 0) && secondId != 0)
		{
			result = TryNeteaseLyric(secondId);
		}
		return result;
		}
		catch (Exception ex)
		{
			Store.Trace("LYRICS netease fail: " + ex.GetType().Name + " " + ex.Message);
			return null;
		}
	}

	// 拉取指定网易云歌曲的歌词（yrc 逐字优先，无则 lrc；纯音乐/空歌词返回 null）
	private static List<LyricLine> TryNeteaseLyric(long id)
	{
		NeteaseLyricResp lyric = GetJson<NeteaseLyricResp>("https://music.163.com/api/song/lyric?id=" + id + "&lv=1&kv=1&tv=-1", "https://music.163.com/", "appver=2.0.2; os=pc");
		if (lyric == null)
		{
			Store.Trace("LYRICS netease lyric empty id=" + id);
			return null;
		}
		// 逐字 yrc 优先（大厂逐字高亮）；没有则普通 lrc
		if (lyric.Yrc != null && !string.IsNullOrEmpty(lyric.Yrc.Lyric))
		{
			List<LyricLine> y = ParseYrc(lyric.Yrc.Lyric);
			if (y.Count > 0)
			{
				return y;
			}
		}
		if (lyric.Lrc == null || string.IsNullOrEmpty(lyric.Lrc.Lyric))
		{
			Store.Trace("LYRICS netease lrc empty id=" + id);
			return null;
		}
		List<LyricLine> r = ParseLrc(lyric.Lrc.Lyric);
		// 纯音乐曲目：正文里出现提示语且无有效歌词行 → 视为不可用，交给兜底源
		if (r.Count == 0)
		{
			return null;
		}
		return r;
	}


	// —— QQ 音乐曲库（搜索免签名，歌词接口 base64；打分同网易云）——
	private static List<LyricLine> TryQQMusic(string artist, string title, string album, double durationSec)
	{
		string kw = Uri.EscapeDataString((title + " " + artist).Trim());
		QqSearch s = null;
		try
		{
			s = GetJson<QqSearch>("https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w=" + kw + "&format=json&p=1&n=20&cr=1", "https://y.qq.com", null);
		}
		catch
		{
			s = null;
		}
		if (s == null || s.data == null || s.data.song == null || s.data.song.list == null)
		{
			return null;
		}
		string wantArtist = artist ?? string.Empty;
		string wantAlbum = album ?? string.Empty;
		bool haveAlbum = !string.IsNullOrEmpty(wantAlbum);
		QqSong best = null;
		QqSong second = null;
		int bestScore = -1;
		int secondScore = -1;
		foreach (QqSong q in s.data.song.list)
		{
			if (q == null || string.IsNullOrEmpty(q.songmid))
			{
				continue;
			}
			string qTitle = q.songname ?? string.Empty;
			string qSinger = string.Empty;
			if (q.singer != null)
			{
				foreach (QqSinger sg in q.singer)
				{
					if (sg != null && !string.IsNullOrEmpty(sg.name))
					{
						qSinger += (qSinger.Length == 0 ? string.Empty : " / ") + sg.name;
					}
				}
			}
			string qAlbum = q.albumname ?? string.Empty;
			int score = 0;
			if (qTitle == title)
			{
				score += 7;
			}
			else if (qTitle.Contains(title) || title.Contains(qTitle))
			{
				score += 3;
			}
			if (!string.IsNullOrEmpty(wantArtist) && qSinger.Length > 0)
			{
				if (qSinger == wantArtist)
				{
					score += 4;
				}
				else if (qSinger.Contains(wantArtist) || wantArtist.Contains(qSinger))
				{
					score += 2;
				}
			}
			if (haveAlbum && !string.IsNullOrEmpty(qAlbum))
			{
				if (qAlbum == wantAlbum)
				{
					score += 2;
				}
				else if (qAlbum.Contains(wantAlbum) || wantAlbum.Contains(qAlbum))
				{
					score += 1;
				}
			}
			if (durationSec > 10.0 && q.interval > 0)
			{
				// 只奖不罚：上报时长可能失真，时长不匹配不扣分。
				double d = Math.Abs(durationSec - q.interval);
				if (d <= 10.0)
				{
					score += 2;
				}
			}
			if (qTitle == title && score >= 5)
			{
				score += 4;
			}
			if (score > bestScore)
			{
				secondScore = bestScore;
				second = best;
				bestScore = score;
				best = q;
			}
			else if (score > secondScore)
			{
				secondScore = score;
				second = q;
			}
		}
		if (best == null || bestScore < 6)
		{
			return null;
		}
		List<LyricLine> result = null;
		foreach (QqSong c in new[] { best, second })
		{
			if (c == null)
			{
				continue;
			}
			List<LyricLine> r = QqLyric(c.songmid);
			if (r != null && r.Count > 0)
			{
				result = r;
				break;
			}
		}
		return result;
	}

	private static List<LyricLine> QqLyric(string mid)
	{
		QqLyricResp r = null;
		try
		{
			r = GetJson<QqLyricResp>("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid=" + mid + "&g_tk=5381&format=json&nobase64=1", "https://y.qq.com/portal/player.html", null);
		}
		catch
		{
			r = null;
		}
		if (r == null || string.IsNullOrEmpty(r.lyric))
		{
			return null;
		}
		string lrc;
		try
		{
			byte[] raw = Convert.FromBase64String(r.lyric);
			lrc = System.Text.Encoding.UTF8.GetString(raw);
		}
		catch
		{
			lrc = r.lyric;
		}
		return ParseLrc(lrc);
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
	// 支持文件头 [offset:±毫秒] 整体偏移。
	private static List<LyricLine> ParseLrc(string lrc)
	{
		List<LyricLine> result = new List<LyricLine>();
		if (string.IsNullOrEmpty(lrc))
		{
			return result;
		}
		// [offset:+/-毫秒] 整体偏移标签
		Match off = Regex.Match(lrc, @"\[offset:\s*([+-]?\d+)\s*\]");
		if (off.Success)
		{
			int v;
			if (int.TryParse(off.Groups[1].Value, out v))
			{
				lock (gate)
				{
					offsetMs = v;
					hasOffsetTag = true;
				}
			}
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

	// 网易云 yrc 逐字歌词解析：[句开始毫秒,句时长毫秒](字开始,字时长,0)字(字开始,字时长,0)字...
	private static List<LyricLine> ParseYrc(string yrc)
	{
		List<LyricLine> result = new List<LyricLine>();
		if (string.IsNullOrEmpty(yrc))
		{
			return result;
		}
		try
		{
			Regex lineTag = new Regex(@"\[(\d+),(\d+)\]");
			string[] raws = yrc.Replace("\r", "").Split('\n');
			foreach (string raw in raws)
			{
				string s = raw.Trim();
				if (s.Length == 0)
				{
					continue;
				}
				Match lt = lineTag.Match(s);
				if (!lt.Success)
				{
					continue;
				}
				double lineStart = long.Parse(lt.Groups[1].Value) / 1000.0;
				string body = s.Substring(lt.Index + lt.Length);
				MatchCollection words = Regex.Matches(body, @"\((\d+),(\d+),\d+\)([^()]*)");
				if (words.Count == 0)
				{
					continue;
				}
				string text = "";
				List<double> wordTimes = new List<double>();
				foreach (Match w in words)
				{
					double wt = long.Parse(w.Groups[1].Value) / 1000.0;
					wordTimes.Add(wt);
					text += w.Groups[2].Value;
				}
				if (text.Length == 0)
				{
					continue;
				}
				result.Add(new LyricLine { Time = lineStart, Text = text, WordTimes = wordTimes });
			}
			result.Sort(delegate (LyricLine x, LyricLine y) { return x.Time.CompareTo(y.Time); });
		}
		catch (Exception ex)
		{
			Store.Trace("LYRICS yrc parse fail: " + ex.Message);
			return result;
		}
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

		[DataMember(Name = "duration")]
		public long Duration;

		[DataMember(Name = "album")]
		public NeteaseAlbum Album = null;
	}

	[DataContract]
	private sealed class NeteaseAlbum
	{
		[DataMember(Name = "name")]
		public string Name = null;
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

		[DataMember(Name = "yrc")]
		public NeteaseLrc Yrc = null;

	}

	[DataContract]
	private sealed class NeteaseLrc
	{
		[DataMember(Name = "lyric")]
		public string Lyric = null;
	}

	[DataContract]
	private sealed class QqSearch
	{
		[DataMember(Name = "data")]
		public QqSearchData data = null;
	}

	[DataContract]
	private sealed class QqSearchData
	{
		[DataMember(Name = "song")]
		public QqSongList song = null;
	}

	[DataContract]
	private sealed class QqSongList
	{
		[DataMember(Name = "list")]
		public List<QqSong> list = null;
	}

	[DataContract]
	private sealed class QqSong
	{
		[DataMember(Name = "songmid")]
		public string songmid = null;

		[DataMember(Name = "songname")]
		public string songname = null;

		[DataMember(Name = "albumname")]
		public string albumname = null;

		[DataMember(Name = "interval")]
		public int interval;

		[DataMember(Name = "singer")]
		public List<QqSinger> singer = null;
	}

	[DataContract]
	private sealed class QqSinger
	{
		[DataMember(Name = "name")]
		public string name = null;
	}

	[DataContract]
	private sealed class QqLyricResp
	{
		[DataMember(Name = "lyric")]
		public string lyric = null;

		[DataMember(Name = "trans")]
		public string trans = null;
	}

}
