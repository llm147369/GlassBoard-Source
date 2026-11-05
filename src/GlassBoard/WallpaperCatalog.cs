using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GlassBoard;

// 官方壁纸图库：GitHub 仓库 wallpapers.json 清单 + wallpapers/ 目录直链。
// 网络策略与 OnlineUpdate 一致：直连 raw.githubusercontent.com 优先，
// 失败依次尝试 ghfast.top / gh-proxy.com / ghproxy.net 前缀镜像，并记住最近成功基址；
// 清单本地缓存兜底（离线可用），下载后按 sha256 + 大小校验完整性。
internal static class WallpaperCatalog
{
	public const string RawPrefix = "https://raw.githubusercontent.com/llm147369/GlassBoard-Source/main/";
	public const string AltPrefix = "https://github.com/llm147369/GlassBoard-Source/raw/refs/heads/main/";
	public const string ManifestUrl = RawPrefix + "wallpapers.json";
	public const string ManifestAlt = AltPrefix + "wallpapers.json";

	public const long MaxEntryBytes = 31457280L;

	private static readonly string[] Mirrors = new string[3]
	{
		"https://ghfast.top/",
		"https://gh-proxy.com/",
		"https://ghproxy.net/"
	};

	// 最近一次成功的下载基址（""=直连），下次下载优先复用
	private static string lastWorkingBase = "";

	private static readonly string CacheFile = Path.Combine(Store.Folder, "wallpapers.cache.json");

	[DataContract]
	public sealed class Entry
	{
		[DataMember(Name = "name")]
		public string Name;
		[DataMember(Name = "file")]
		public string File;
		[DataMember(Name = "size")]
		public long Size;
		[DataMember(Name = "sha256")]
		public string Sha256;
		[DataMember(Name = "category")]
		public string Category;

		public string Url => RawPrefix + File;

		public string AltUrl => AltPrefix + File;
	}

	[DataContract]
	public sealed class Manifest
	{
		[DataMember(Name = "wallpapers")]
		public List<Entry> Wallpapers;
	}

	private static HttpClient Client(int timeoutSeconds = 25)
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		HttpClient client = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(timeoutSeconds)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("GlassBoard-Wallpapers/1.0");
		return client;
	}

	// 清单候选地址：最近成功基址 → 直连 → 备用直连 → 三个镜像
	private static IEnumerable<string> ManifestCandidates()
	{
		List<string> list = new List<string>();
		if (lastWorkingBase.Length > 0)
		{
			list.Add(lastWorkingBase + ManifestUrl);
		}
		list.Add(ManifestUrl);
		if (!list.Contains(ManifestAlt))
		{
			list.Add(ManifestAlt);
		}
		foreach (string mirror in Mirrors)
		{
			string candidate = mirror + ManifestUrl;
			if (!list.Contains(candidate))
			{
				list.Add(candidate);
			}
		}
		return list;
	}

	// 拉取清单：网络失败时回退本地缓存；缓存也没有则抛出。
	public static async Task<List<Entry>> FetchAsync()
	{
		Exception lastError = null;
		foreach (string url in ManifestCandidates())
		{
			try
			{
				using (HttpClient client = Client(12))
				{
					string json = await client.GetStringAsync(url);
					List<Entry> list = Parse(json);
					if (list.Count == 0)
					{
						continue;
					}
					lastWorkingBase = url.StartsWith(ManifestUrl, StringComparison.Ordinal) || url.StartsWith(ManifestAlt, StringComparison.Ordinal) ? "" : url.Substring(0, url.IndexOf(ManifestUrl, StringComparison.Ordinal));
					try
					{
						Directory.CreateDirectory(Store.Folder);
						File.WriteAllText(CacheFile, json);
					}
					catch
					{
					}
					return list;
				}
			}
			catch (Exception ex)
			{
				lastError = ex;
			}
		}
		try
		{
			if (File.Exists(CacheFile))
			{
				List<Entry> cached = Parse(File.ReadAllText(CacheFile));
				if (cached.Count > 0)
				{
					return cached;
				}
			}
		}
		catch (Exception ex)
		{
			lastError = ex;
		}
		throw new Exception("无法连接 GitHub 壁纸图库，网络可能受限。", lastError);
	}

	private static List<Entry> Parse(string json)
	{
		using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
		{
			Manifest manifest = (Manifest)new DataContractJsonSerializer(typeof(Manifest)).ReadObject(stream);
			return (manifest?.Wallpapers ?? new List<Entry>()).Where(Sane).ToList();
		}
	}

	// 清单条目过滤：只允许 wallpapers/ 目录下的图片，禁止路径穿越；
	// sha256 必须是合法 64 位十六进制；大小在允许范围内。
	private static bool Sane(Entry e)
	{
		if (e == null || string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.File))
		{
			return false;
		}
		if (!Regex.IsMatch(e.File, "^wallpapers/[A-Za-z0-9_\\-\\.]+\\.(jpg|jpeg|png|bmp)$", RegexOptions.IgnoreCase))
		{
			return false;
		}
		if (e.Size < 8 || e.Size > MaxEntryBytes)
		{
			return false;
		}
		if (e.Sha256 == null || !Regex.IsMatch(e.Sha256, "^[a-fA-F0-9]{64}$"))
		{
			return false;
		}
		return true;
	}

	private static IEnumerable<string> DownloadCandidates(Entry e)
	{
		HashSet<string> tried = new HashSet<string>(StringComparer.Ordinal);
		if (lastWorkingBase.Length > 0 && tried.Add(lastWorkingBase + e.Url))
		{
			yield return lastWorkingBase + e.Url;
		}
		if (tried.Add(e.Url))
		{
			yield return e.Url;
		}
		if (tried.Add(e.AltUrl))
		{
			yield return e.AltUrl;
		}
		foreach (string mirror in Mirrors)
		{
			string candidate = mirror + e.Url;
			if (tried.Add(candidate))
			{
				yield return candidate;
			}
		}
	}

	// 下载到 dest：直连/镜像依次尝试，完成后校验大小 + sha256，失败删除半成品并抛出。
	public static async Task DownloadAsync(Entry e, string dest, Action<long, long> onProgress)
	{
		Exception lastError = null;
		foreach (string url in DownloadCandidates(e))
		{
			try
			{
				using (HttpClient client = Client(90))
				using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
				{
					response.EnsureSuccessStatusCode();
					long total = response.Content.Headers.ContentLength ?? e.Size;
					using (Stream source = await response.Content.ReadAsStreamAsync())
					using (FileStream target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
					{
						byte[] buffer = new byte[65536];
						long done = 0L;
						int read;
						while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
						{
							await target.WriteAsync(buffer, 0, read);
							done += read;
							onProgress?.Invoke(done, total);
						}
					}
				}
				byte[] bytes = File.ReadAllBytes(dest);
				if (bytes.LongLength != e.Size || !string.Equals(UpdateRules.Hash(bytes), e.Sha256, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("壁纸文件校验失败");
				}
				return;
			}
			catch (Exception ex)
			{
				lastError = ex;
				try
				{
					if (File.Exists(dest))
					{
						File.Delete(dest);
					}
				}
				catch
				{
				}
			}
		}
		throw new Exception("下载壁纸失败，网络可能受限。", lastError);
	}

	// 在线缩略图（images.weserv.nl 实时缩放，不占本地空间；离线时缩略图降级为空）
	public static string ThumbUrl(Entry e)
	{
		try
		{
			return "https://images.weserv.nl/?url=" + Uri.EscapeDataString(e.Url) + "&w=480&h=270&fit=cover&output=jpg";
		}
		catch
		{
			return null;
		}
	}
}
