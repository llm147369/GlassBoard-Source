using System;
using System.Globalization;
using System.Linq;

namespace GlassBoard;

public static class ClockCatalog
{
	public static readonly string[] Names = new string[13]
	{
		"经典时间", "手写星期叠字", "极简大数字", "星期与日期", "分栏数字", "宽距星期", "日期海报", "纵向数字", "细线指针", "日程轨道",
		"衬线杂志", "等宽电子", "横向日历"
	};

	public static string Weekday(DateTime now, int style)
	{
		string text = now.ToString("dddd", CultureInfo.GetCultureInfo("en-US"));
		return style switch
		{
			11 => now.ToString("ddd", CultureInfo.GetCultureInfo("en-US")).ToUpperInvariant(), 
			5 => string.Join(" ", from c in text.ToUpperInvariant()
				select c.ToString()), 
			_ => text, 
		};
	}

	public static string DateLabel(DateTime now, int style)
	{
		CultureInfo cultureInfo = CultureInfo.GetCultureInfo("en-US");
		return style switch
		{
			11 => now.ToString("yyyy / MM / dd", cultureInfo), 
			10 => now.ToString("dddd · dd MMMM", cultureInfo), 
			5 => now.ToString("dd MMMM yyyy", cultureInfo).ToUpperInvariant(), 
			_ => now.ToString("M月d日  dddd"), 
		};
	}

	public static double HourAngle(DateTime now)
	{
		return (double)(now.Hour % 12 * 30) + (double)now.Minute * 0.5;
	}

	public static double MinuteAngle(DateTime now)
	{
		return now.Minute * 6;
	}

	public static double DayFraction(DateTime now)
	{
		return (double)(now.Hour * 60 + now.Minute) / 1440.0;
	}
}
