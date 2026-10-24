using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class ClockRules
{
	public static DateTime Next(int hour, int minute, DateTime now)
	{
		DateTime dateTime = now.Date.AddHours(hour).AddMinutes(minute);
		if (!(dateTime <= now))
		{
			return dateTime;
		}
		return dateTime.AddDays(1.0);
	}

	public static double Remaining(WidgetOptions o, DateTime utc)
	{
		if (!o.TimerDueUtc.HasValue)
		{
			return Math.Max(0.0, o.TimerPausedSeconds);
		}
		return Math.Max(0.0, (o.TimerDueUtc.Value - utc).TotalSeconds);
	}

	public static void Start(WidgetOptions o, double seconds, DateTime utc)
	{
		o.TimerPausedSeconds = 0.0;
		o.TimerDueUtc = utc.AddSeconds(LayoutRules.Clamp(seconds, 1.0, 86400.0, 300.0));
	}

	public static void Pause(WidgetOptions o, DateTime utc)
	{
		o.TimerPausedSeconds = Remaining(o, utc);
		o.TimerDueUtc = null;
	}

	public static void Resume(WidgetOptions o, DateTime utc)
	{
		if (o.TimerPausedSeconds > 0.0)
		{
			Start(o, o.TimerPausedSeconds, utc);
		}
	}

	public static void Cancel(WidgetOptions o)
	{
		o.TimerPausedSeconds = 0.0;
		o.TimerDueUtc = null;
	}

	public static List<string> Check(WidgetOptions o, DateTime local, DateTime utc)
	{
		List<string> list = new List<string>();
		foreach (ClockAlarm alarm in o.Alarms)
		{
			if (alarm.Enabled && !(alarm.DueLocal > local))
			{
				list.Add(alarm.Label);
				if (alarm.Daily)
				{
					alarm.DueLocal = Next(alarm.Hour, alarm.Minute, local);
				}
				else
				{
					alarm.Enabled = false;
				}
			}
		}
		if (o.TimerDueUtc.HasValue && o.TimerDueUtc.Value <= utc)
		{
			list.Add("倒计时结束");
			Cancel(o);
		}
		return list;
	}

	public static string Display(WidgetOptions o, DateTime utc)
	{
		double num = Remaining(o, utc);
		if (num > 0.0)
		{
			return (o.TimerDueUtc.HasValue ? "倒计时 " : "已暂停 ") + ((int)(Math.Ceiling(num) / 3600.0)).ToString("00") + ":" + TimeSpan.FromSeconds(Math.Ceiling(num)).ToString("mm\\:ss");
		}
		ClockAlarm clockAlarm = (from a in o.Alarms
			where a.Enabled
			orderby a.DueLocal
			select a).FirstOrDefault();
		if (clockAlarm != null)
		{
			return "闹钟 " + clockAlarm.DueLocal.ToString("M/d HH:mm") + " · " + clockAlarm.Label;
		}
		return "点击 ••• 设置闹钟和倒计时";
	}
}
