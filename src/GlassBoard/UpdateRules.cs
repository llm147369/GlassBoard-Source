using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GlassBoard;

public static class UpdateRules
{
	public const string Repository = "https://github.com/llm147369/GlassBoard-Source";

	public const string Api = "https://api.github.com/repos/llm147369/GlassBoard-Source";

	public const string Manifest = "https://raw.githubusercontent.com/llm147369/GlassBoard-Source/main/update.json";

	public const string ManifestFallback = "https://github.com/llm147369/GlassBoard-Source/raw/refs/heads/main/update.json";

	public const long MaxArchive = 16777216L;

	private static readonly HashSet<string> allowed = new HashSet<string>(new string[21]
	{
		"GlassBoard.exe", "GlassBoard.Updater.exe", "GlassBoard.exe.config", "Interop.UIAutomationClient.dll", "README.md", "Start.cmd", "CheckMemory.cmd", "CheckWindows.cmd", "CollectDiagnostics.cmd", "RecoverWidgets.cmd",
		"RestoreDesktop.cmd", "SHA256SUMS.txt", "THIRD_PARTY_NOTICES.md", "lib/NPSMLib.dll", "licenses/NPSMLib_LICENSE.txt", "licenses/now-playing-service_LICENSE.txt", "src/CheckMemory.ps1", "src/CheckWindows.ps1",
		"src/CollectDiagnostics.ps1", "src/MediaBridge.ps1", "src/NativeMediaBridge.ps1"
	}, StringComparer.Ordinal);

	public static string Alternate(string url)
	{
		if (url == null || !url.StartsWith("https://raw.githubusercontent.com/llm147369/GlassBoard-Source/main/", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return "https://github.com/llm147369/GlassBoard-Source/raw/refs/heads/main/" + url.Substring("https://raw.githubusercontent.com/llm147369/GlassBoard-Source/main/".Length);
	}

	public static Version PackageVersion(string name)
	{
		Match match = Regex.Match(name ?? "", "^GlassBoard_v([0-9]+\\.[0-9]+\\.[0-9]+(?:\\.[0-9]+)?)_User\\.zip$", RegexOptions.IgnoreCase);
		if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var result))
		{
			return null;
		}
		return new Version(result.Major, result.Minor, Math.Max(0, result.Build), Math.Max(0, result.Revision));
	}

	public static bool Trusted(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var result) || result.Scheme != "https" || !string.IsNullOrEmpty(result.UserInfo) || !result.IsDefaultPort)
		{
			return false;
		}
		if (!result.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !result.AbsolutePath.StartsWith("/llm147369/GlassBoard-Source/releases/download/", StringComparison.OrdinalIgnoreCase))
		{
			if (result.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
			{
				return result.AbsolutePath.StartsWith("/llm147369/GlassBoard-Source/", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		return true;
	}

	private static UpdateOffer Offer(UpdateAsset asset, string notes)
	{
		if (asset == null || asset.Size < 1 || asset.Size > 16777216)
		{
			return null;
		}
		Version version = PackageVersion(asset.Name);
		string url = asset.ReleaseUrl ?? asset.FileUrl;
		if (version == null || !Trusted(url))
		{
			return null;
		}
		string text = null;
		if (!string.IsNullOrWhiteSpace(asset.Digest))
		{
			if (!Regex.IsMatch(asset.Digest, "^sha256:[a-fA-F0-9]{64}$"))
			{
				return null;
			}
			text = asset.Digest.Substring(7);
		}
		string text2 = null;
		if (!string.IsNullOrEmpty(asset.BlobSha))
		{
			if (!Regex.IsMatch(asset.BlobSha, "^[a-fA-F0-9]{40}$"))
			{
				return null;
			}
			text2 = asset.BlobSha;
		}
		if (text == null && text2 == null)
		{
			return null;
		}
		return new UpdateOffer
		{
			Version = version,
			Url = url,
			Name = asset.Name,
			Notes = (string.IsNullOrEmpty(notes) ? "" : notes.Substring(0, Math.Min(notes.Length, 4000))),
			Sha256 = text,
			BlobSha = text2,
			Size = asset.Size
		};
	}

	public static UpdateOffer Choose(IEnumerable<UpdateRelease> releases, IEnumerable<UpdateAsset> files)
	{
		List<UpdateOffer> list = new List<UpdateOffer>();
		foreach (UpdateRelease item in releases ?? new UpdateRelease[0])
		{
			if (item == null || item.Draft || item.Prerelease)
			{
				continue;
			}
			foreach (UpdateAsset item2 in item.Assets ?? new List<UpdateAsset>())
			{
				UpdateOffer updateOffer = Offer(item2, item.Notes);
				if (updateOffer != null)
				{
					list.Add(updateOffer);
				}
			}
		}
		foreach (UpdateAsset item3 in files ?? new UpdateAsset[0])
		{
			if (item3 != null && item3.Type == "file")
			{
				UpdateOffer updateOffer2 = Offer(item3, "仓库直接发布的用户版更新包。");
				if (updateOffer2 != null)
				{
					list.Add(updateOffer2);
				}
			}
		}
		return list.OrderByDescending((UpdateOffer o) => o.Version).FirstOrDefault();
	}

	public static bool Due(long now, long last)
	{
		if (last > 0 && last <= now)
		{
			return now - last >= TimeSpan.FromDays(1.0).Ticks;
		}
		return true;
	}

	public static string Hash(byte[] bytes)
	{
		using SHA256 sHA = SHA256.Create();
		return BitConverter.ToString(sHA.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
	}

	public static string BlobHash(byte[] bytes)
	{
		using SHA1 sHA = SHA1.Create();
		byte[] bytes2 = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
		sHA.TransformBlock(bytes2, 0, bytes2.Length, bytes2, 0);
		sHA.TransformFinalBlock(bytes, 0, bytes.Length);
		return BitConverter.ToString(sHA.Hash).Replace("-", "").ToLowerInvariant();
	}

	public static void Verify(byte[] bytes, UpdateOffer offer)
	{
		if (bytes.LongLength != offer.Size || bytes.LongLength > 16777216)
		{
			throw new InvalidDataException("更新包大小不一致");
		}
		if ((offer.Sha256 != null && !string.Equals(Hash(bytes), offer.Sha256, StringComparison.OrdinalIgnoreCase)) || (offer.BlobSha != null && !string.Equals(BlobHash(bytes), offer.BlobSha, StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidDataException("更新包校验失败，请重新检查版本后下载");
		}
	}

	public static bool Allowed(string path)
	{
		return allowed.Contains(path);
	}
}
