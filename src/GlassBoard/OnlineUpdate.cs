using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace GlassBoard;

// 在线更新：读取 GitHub Release 最新版本，下载便携包解压，
// 写入重启脚本（等待本进程退出 → 覆盖程序文件 → 重新启动）完成原地升级。
// 网络回退：api.github.com / github.com 直连失败时自动切换国内可达的加速镜像，
// 让无法访问 GitHub 的用户也能检查与下载更新。
public static class OnlineUpdate
{
	private const string RepoOwner = "llm147369";
	private const string RepoName = "GlassBoard-Source";
	private const string ApiPath = "repos/" + RepoOwner + "/" + RepoName + "/releases/latest";
	private const string LatestApi = "https://api.github.com/" + ApiPath;
	private const string ReleasePage = "https://github.com/" + RepoOwner + "/" + RepoName + "/releases/latest";

	// 前缀式加速镜像：任意 github.com / api.github.com 地址拼在其后即可代理。
	// 按顺序尝试，全部失败才算检查失败。
	private static readonly string[] Mirrors = new string[3]
	{
		"https://ghfast.top/",
		"https://gh-proxy.com/",
		"https://ghproxy.net/"
	};

	// 最近一次成功的下载基址（""=直连，"https://ghfast.top/" 等），下次下载优先复用
	private static string lastWorkingBase = "";

	[DataContract]
	public sealed class ReleaseInfo
	{
		[DataMember(Name = "tag_name")]
		public string TagName;
		[DataMember(Name = "name")]
		public string Name;
		[DataMember(Name = "html_url")]
		public string HtmlUrl;
		[DataMember(Name = "body")]
		public string Body;
		[DataMember(Name = "published_at")]
		public string PublishedAt;
		[DataMember(Name = "assets")]
		public List<ReleaseAsset> Assets;
	}

	[DataContract]
	public sealed class ReleaseAsset
	{
		[DataMember(Name = "name")]
		public string Name;
		[DataMember(Name = "size")]
		public long Size;
		[DataMember(Name = "browser_download_url")]
		public string DownloadUrl;
		[DataMember(Name = "updated_at")]
		public string UpdatedAt;
	}

	public static string CurrentVersion()
	{
		return Assembly.GetExecutingAssembly().GetName().Version.ToString();
	}

	// 语义化版本比较（允许 v 前缀与 - 后缀）：a>b 返回 1，相等返回 0，否则 -1
	public static int CompareVersion(string a, string b)
	{
		int[] left = Split(a);
		int[] right = Split(b);
		for (int i = 0; i < 4; i++)
		{
			if (left[i] != right[i])
			{
				return left[i] > right[i] ? 1 : -1;
			}
		}
		return 0;
	}

	private static int[] Split(string version)
	{
		int[] parts = new int[4];
		string[] segments = (version ?? "").TrimStart('v', 'V').Split('.', '-', '+');
		for (int i = 0; i < parts.Length; i++)
		{
			int num = 0;
			if (i < segments.Length)
			{
				int.TryParse(segments[i], out num);
			}
			parts[i] = num;
		}
		return parts;
	}

	private static HttpClient Client(int timeoutSeconds = 25)
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		HttpClient client = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(timeoutSeconds)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("GlassBoard-Updater/1.0");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		return client;
	}

	// 检查版本 API 的候选地址：直连优先，失败后逐个尝试镜像
	private static IEnumerable<string> ApiCandidates()
	{
		yield return LatestApi;
		foreach (string mirror in Mirrors)
		{
			yield return mirror + LatestApi;
		}
	}

	public static async Task<ReleaseInfo> FetchLatestAsync()
	{
		Exception lastError = null;
		foreach (string url in ApiCandidates())
		{
			try
			{
				// API 只取一个小 JSON：12 秒快速失败，尽快切下一个镜像
				using (HttpClient client = Client(12))
				{
					string json = await client.GetStringAsync(url);
					using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
					{
						ReleaseInfo info = (ReleaseInfo)new DataContractJsonSerializer(typeof(ReleaseInfo)).ReadObject(stream);
						if (info == null || string.IsNullOrEmpty(info.TagName))
						{
							continue;
						}
						// 记录本次成功用的基址，下载时优先复用（直连成功记 ""）
						lastWorkingBase = url.StartsWith(LatestApi, StringComparison.Ordinal) ? "" : url.Substring(0, url.IndexOf(LatestApi, StringComparison.Ordinal));
						return info;
					}
				}
			}
			catch (Exception ex)
			{
				lastError = ex;
			}
		}
		throw new Exception("无法连接 GitHub 更新服务，网络可能受限。", lastError);
	}

	// 启动自动检查只取最新版本号
	public static async Task<string> FetchLatestTagAsync()
	{
		ReleaseInfo info = await FetchLatestAsync();
		return info == null ? "" : (info.TagName ?? "");
	}

	// 把 Release 说明（GitHub release body）整理成可显示的纯文本摘要，超长按换行截断
	public static string ReleaseNotesSummary(ReleaseInfo info, int max)
	{
		try
		{
			string text = (info == null) ? "" : (info.Body ?? "");
			if (string.IsNullOrWhiteSpace(text))
			{
				return "";
			}
			text = text.Replace("**", "");
			while (text.Contains("  "))
			{
				text = text.Replace("  ", " ");
			}
			text = text.Trim();
			if (text.Length > max)
			{
				text = text.Substring(0, max);
				int cut = text.LastIndexOf('\n');
				if (cut > max / 2)
				{
					text = text.Substring(0, cut);
				}
				text = text.TrimEnd() + " …";
			}
			return text;
		}
		catch
		{
			return "";
		}
	}

	// 同版本号但远端构建更新（例如同 tag 重新打包了新修复）的判定：
	// 用 Release 时间戳与本地 exe 文件时间比较（2 分钟余量），弥补纯版本号比较的盲区。
	public static bool RemoteBuildNewer(ReleaseInfo info, ReleaseAsset asset)
	{
		try
		{
			string raw = (asset != null && !string.IsNullOrEmpty(asset.UpdatedAt)) ? asset.UpdatedAt : info.PublishedAt;
			if (string.IsNullOrEmpty(raw))
			{
				return false;
			}
			DateTimeOffset remote = DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
			string exe = Assembly.GetExecutingAssembly().Location;
			if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
			{
				return false;
			}
			DateTime local = File.GetLastWriteTimeUtc(exe);
			return remote.UtcDateTime > local.AddMinutes(2.0);
		}
		catch
		{
			return false;
		}
	}

	// 返回「有可用更新时的版本信息」，无更新返回 null：版本更高，或同版本但远端构建更新。
	public static async Task<ReleaseInfo> LatestAvailableAsync()
	{
		ReleaseInfo info = await FetchLatestAsync();
		if (info == null || string.IsNullOrEmpty(info.TagName))
		{
			return null;
		}
		int cmp = CompareVersion(info.TagName, CurrentVersion());
		if (cmp > 0)
		{
			return info;
		}
		if (cmp == 0)
		{
			ReleaseAsset asset = info.Assets == null ? null : info.Assets.FirstOrDefault((ReleaseAsset a) => !string.IsNullOrEmpty(a.DownloadUrl) && a.DownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
			if (RemoteBuildNewer(info, asset))
			{
				return info;
			}
		}
		return null;
	}

	// 返回「有可用更新时的版本号」，无更新返回空串
	public static async Task<string> LatestAvailableTagAsync()
	{
		ReleaseInfo info = await LatestAvailableAsync();
		return info == null ? "" : (info.TagName ?? "");
	}

	// WPF 的 MessageBox.Show(owner=null, …) 会抛 ArgumentNullException("window")；
	// 托盘菜单调用时没有所属窗口，走无 owner 重载。
	private static MessageBoxResult ShowDialog(Window owner, string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK)
	{
		return owner == null
			? MessageBox.Show(text, caption, buttons)
			: MessageBox.Show(owner, text, caption, buttons);
	}

	// 手动检查入口（设置页 / 托盘菜单）：状态栏与进度条实时反馈
	public static async Task CheckManualAsync(Window owner, TextBlock status = null, ProgressBar bar = null)
	{
		void Set(string text, bool showBar = false, double value = 0.0)
		{
			if (status != null)
			{
				status.Text = text;
			}
			if (bar != null)
			{
				bar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
				bar.Value = value;
			}
		}
		string current = CurrentVersion();
		try
		{
			Set("正在检查新版本…");
			ReleaseInfo info = await FetchLatestAsync();
			if (info == null || string.IsNullOrEmpty(info.TagName))
			{
				Set("检查失败：无法读取版本信息，请稍后重试。");
				return;
			}
			ReleaseAsset asset = info.Assets == null ? null : info.Assets.FirstOrDefault((ReleaseAsset a) => !string.IsNullOrEmpty(a.DownloadUrl) && a.DownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
			int cmp = CompareVersion(info.TagName, current);
			bool sameBuild = (cmp == 0) && RemoteBuildNewer(info, asset);
			if (cmp < 0 || (cmp == 0 && !sameBuild))
			{
				Set("当前版本 v" + current + " 已是最新。");
				ShowDialog(owner, "当前版本 v" + current + " 已是最新版本。", "GlassBoard-Source 在线更新");
				return;
			}
			string tag = info.TagName.TrimStart('v', 'V');
			string notes = ReleaseNotesSummary(info, 400);
			string noteBlock = string.IsNullOrEmpty(notes) ? "" : ("\n\n更新内容：\n" + notes);
			string prompt = (cmp > 0)
				? ("发现新版本 v" + tag + noteBlock + "\n\n更新只替换程序文件，配置与自定义图标不受影响。\n现在下载并更新？")
				: ("发现 v" + tag + " 的新构建（同版本号，包含最新修复）。" + noteBlock + "\n\n更新只替换程序文件，配置与自定义图标不受影响。\n现在下载并更新？");
			if (ShowDialog(owner, prompt, "GlassBoard-Source 在线更新", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
			{
				Set("已取消更新，当前版本 v" + current + "。");
				return;
			}
			if (asset == null)
			{
				Set("更新失败：发布包中未找到便携包下载地址。");
				return;
			}
			string zipPath = Path.Combine(Path.GetTempPath(), "GlassBoard-update-" + tag + ".zip");
			string extractDir = Path.Combine(Path.GetTempPath(), "GlassBoard-update-" + tag);
			Set("正在下载 v" + tag + "（0%）…", showBar: true);
			await DownloadAsync(asset, zipPath, (long done, long total) =>
			{
				double percent = total > 0 ? done * 100.0 / total : 0.0;
				Set("正在下载 v" + tag + "（" + (int)percent + "%）…", showBar: true, value: percent);
			});
			Set("下载完成，正在解压…", showBar: true, value: 100.0);
			if (Directory.Exists(extractDir))
			{
				Directory.Delete(extractDir, recursive: true);
			}
			ZipFile.ExtractToDirectory(zipPath, extractDir);
			File.Delete(zipPath);
			if (ShowDialog(owner, "更新包已就绪。\n\n点击「确定」后程序会自动重启并完成升级。", "GlassBoard-Source 在线更新", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
			{
				Set("已取消更新，当前版本 v" + current + "。");
				return;
			}
			Set("正在重启并应用更新…", showBar: true, value: 100.0);
			InstallAndRestart(extractDir);
		}
		catch (Exception ex)
		{
			Store.Log(new Exception("在线更新失败", ex));
			Set("更新失败：" + ex.Message);
			ShowDialog(owner, "更新失败：" + ex.Message + "\n\n网络受限时可打开以下链接手动下载便携包，解压覆盖即可：\n" + ReleasePage, "GlassBoard-Source 在线更新");
		}
	}

	// 下载候选地址：上次成功的基址优先，其次直连，最后逐个镜像
	private static IEnumerable<string> DownloadCandidates(ReleaseAsset asset)
	{
		HashSet<string> tried = new HashSet<string>(StringComparer.Ordinal);
		if (!string.IsNullOrEmpty(lastWorkingBase))
		{
			tried.Add(lastWorkingBase + asset.DownloadUrl);
			yield return lastWorkingBase + asset.DownloadUrl;
		}
		if (!tried.Contains(asset.DownloadUrl))
		{
			tried.Add(asset.DownloadUrl);
			yield return asset.DownloadUrl;
		}
		foreach (string mirror in Mirrors)
		{
			string candidate = mirror + asset.DownloadUrl;
			if (tried.Add(candidate))
			{
				yield return candidate;
			}
		}
	}

	private static async Task DownloadAsync(ReleaseAsset asset, string dest, Action<long, long> onProgress)
	{
		Exception lastError = null;
		foreach (string url in DownloadCandidates(asset))
		{
			try
			{
				using (HttpClient client = Client(90))
				using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
				{
					response.EnsureSuccessStatusCode();
					long total = response.Content.Headers.ContentLength ?? asset.Size;
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
				return;
			}
			catch (Exception ex)
			{
				// 该地址失败（断流/超时/网络受限）→ 尝试下一个镜像
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
		throw new Exception("下载更新包失败，网络可能受限。", lastError);
	}

	// 写入重启脚本：等待本进程退出 → robocopy 覆盖程序文件 → 重新启动。
	// 用户配置不覆盖：目标目录已有 DefaultConfig.json 或 .done 时，丢弃包内配置。
	private static void InstallAndRestart(string extractedDir)
	{
		string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
		// 目录不可写（Program Files 等）：robocopy 覆盖会静默失败，直接引导手动更新
		try
		{
			string probe = Path.Combine(appDir, "GlassBoard-write-test.tmp");
			File.WriteAllText(probe, "1");
			File.Delete(probe);
		}
		catch
		{
			MessageBox.Show("程序目录不可写（可能需要管理员权限），无法自动覆盖程序文件。\n\n请以管理员身份运行 GlassBoard-Source 后重试，或手动下载便携包覆盖：\n" + ReleasePage, "GlassBoard-Source 在线更新");
			return;
		}
		if (File.Exists(Path.Combine(appDir, "DefaultConfig.json")) || File.Exists(Path.Combine(appDir, "DefaultConfig.json.done")))
		{
			string packagedConfig = Path.Combine(extractedDir, "DefaultConfig.json");
			if (File.Exists(packagedConfig))
			{
				File.Delete(packagedConfig);
			}
		}
		string script = Path.Combine(Path.GetTempPath(), "GlassBoard-update-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cmd");
		string[] lines = new string[]
		{
			"@echo off",
			":wait",
			"tasklist /FI \"IMAGENAME eq GlassBoard.exe\" 2>nul | find /I \"GlassBoard.exe\" >nul",
			"if not errorlevel 1 (",
			"  timeout /t 1 /nobreak >nul",
			"  goto wait",
			")",
			"robocopy \"" + extractedDir + "\" \"" + appDir + "\" /E /IS /IT /R:1 /W:1 /NFL /NDL /NJH /NJS >nul",
			"start \"\" \"" + Path.Combine(appDir, "GlassBoard.exe") + "\"",
			"rmdir /S /Q \"" + extractedDir + "\"",
			"del \"%~f0\""
		};
		File.WriteAllLines(script, lines, Encoding.ASCII);
		Process.Start(new ProcessStartInfo(script)
		{
			UseShellExecute = false,
			CreateNoWindow = true
		});
		Application.Current.Shutdown();
	}
}
