using System;

namespace GlassBoard;

/// <summary>
/// 时钟组件可选时区列表与换算。
/// 空 Id 表示跟随系统本地时区。
/// </summary>
public static class TimeZones
{
	public sealed class Zone
	{
		public string Id;
		public string Label;
	}

	public static readonly Zone[] List = new Zone[]
	{
		new Zone { Id = "", Label = "跟随系统" },
		new Zone { Id = "China Standard Time", Label = "中国 · 北京" },
		new Zone { Id = "Singapore Standard Time", Label = "新加坡" },
		new Zone { Id = "Tokyo Standard Time", Label = "日本 · 东京" },
		new Zone { Id = "Korea Standard Time", Label = "韩国 · 首尔" },
		new Zone { Id = "India Standard Time", Label = "印度" },
		new Zone { Id = "Arab Standard Time", Label = "阿联酋 · 迪拜" },
		new Zone { Id = "Russian Standard Time", Label = "俄罗斯 · 莫斯科" },
		new Zone { Id = "W. Europe Standard Time", Label = "德国 · 柏林" },
		new Zone { Id = "GMT Standard Time", Label = "英国 · 伦敦" },
		new Zone { Id = "Eastern Standard Time", Label = "美国 · 东部" },
		new Zone { Id = "Central Standard Time", Label = "美国 · 中部" },
		new Zone { Id = "Pacific Standard Time", Label = "美国 · 西部" },
		new Zone { Id = "AUS Eastern Standard Time", Label = "澳大利亚 · 悉尼" },
		new Zone { Id = "New Zealand Standard Time", Label = "新西兰" }
	};

	public static readonly string[] Labels = BuildLabels();

	private static string[] BuildLabels()
	{
		string[] labels = new string[List.Length];
		for (int i = 0; i < List.Length; i++)
		{
			string id = List[i].Id;
			if (id.Length == 0)
			{
				labels[i] = List[i].Label;
				continue;
			}
			TimeZoneInfo zone;
			try
			{
				zone = TimeZoneInfo.FindSystemTimeZoneById(id);
			}
			catch
			{
				labels[i] = List[i].Label;
				continue;
			}
			int hours = (int)zone.BaseUtcOffset.TotalHours;
			int minutes = Math.Abs(zone.BaseUtcOffset.Minutes);
			string offset = minutes == 0 ? hours.ToString("+0;-0") : hours.ToString("+0;-0") + ":" + minutes.ToString("00");
			labels[i] = List[i].Label + " (UTC" + offset + ")";
		}
		return labels;
	}

	/// <summary>按 Id 取目标时区的当前时间；空 Id 或未知 Id 回退系统本地时间。</summary>
	public static DateTime Now(string id)
	{
		if (!string.IsNullOrEmpty(id))
		{
			try
			{
				return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(id));
			}
			catch
			{
			}
		}
		return DateTime.Now;
	}

	/// <summary>Id 是否可用（空 = 跟随系统，恒可用）。</summary>
	public static bool Valid(string id)
	{
		if (string.IsNullOrEmpty(id))
		{
			return true;
		}
		try
		{
			TimeZoneInfo.FindSystemTimeZoneById(id);
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>Id 在列表中的索引；未知 Id 返回 0（跟随系统）。</summary>
	public static int Index(string id)
	{
		for (int i = 0; i < List.Length; i++)
		{
			if (string.Equals(List[i].Id, id, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}
		return 0;
	}
}
