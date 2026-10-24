using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class WidgetRules
{
	public static readonly string[] Kinds = new string[4] { "clock", "weather", "music", "notes" };

	public static readonly string[] Names = new string[4] { "时钟", "天气", "音乐", "备忘录" };

	public static bool Enabled(WidgetOptions o, string kind)
	{
		return kind switch
		{
			"notes" => o.Notes, 
			"music" => o.Music, 
			"weather" => o.Weather, 
			"clock" => o.Clock, 
			_ => false, 
		};
	}

	public static int Panel(WidgetOptions o, string kind)
	{
		return kind switch
		{
			"music" => o.MusicPanel, 
			"weather" => o.WeatherPanel, 
			"clock" => o.ClockPanel, 
			_ => o.NotesPanel, 
		};
	}

	public static void Assign(WidgetOptions o, string kind, int panel)
	{
		panel = Math.Max(0, Math.Min(3, panel));
		switch (kind)
		{
		case "clock":
			o.ClockPanel = panel;
			break;
		case "weather":
			o.WeatherPanel = panel;
			break;
		case "music":
			o.MusicPanel = panel;
			break;
		case "notes":
			o.NotesPanel = panel;
			break;
		}
	}

	public static void Enable(WidgetOptions o, string kind, bool value)
	{
		switch (kind)
		{
		case "clock":
			o.Clock = value;
			break;
		case "weather":
			o.Weather = value;
			break;
		case "music":
			o.Music = value;
			break;
		case "notes":
			o.Notes = value;
			break;
		}
	}

	public static Dictionary<int, List<string>> Layout(WidgetOptions o)
	{
		return (from k in Kinds
			where Enabled(o, k)
			group k by Panel(o, k)).ToDictionary((IGrouping<int, string> g) => g.Key, (IGrouping<int, string> g) => g.ToList());
	}

	public static void Recover(WidgetOptions o)
	{
		string[] kinds = Kinds;
		foreach (string kind in kinds)
		{
			Enable(o, kind, value: true);
		}
		o.Normalize();
	}

	public static int FreePanel(WidgetOptions o, string kind)
	{
		HashSet<int> used = new HashSet<int>(from k in Kinds
			where k != kind && Enabled(o, k)
			select Panel(o, k));
		return Enumerable.Range(0, 4).First((int p) => !used.Contains(p));
	}

	public static int NextTrack(int current, int count, int repeat, bool shuffle, int random, bool ended)
	{
		if (count <= 0)
		{
			return -1;
		}
		if (ended && repeat == 2)
		{
			return Math.Max(0, Math.Min(count - 1, current));
		}
		if (shuffle && count > 1)
		{
			int num = Math.Abs(random % (count - 1));
			if (num < current)
			{
				return num;
			}
			return num + 1;
		}
		int num2 = current + 1;
		if (num2 >= count)
		{
			if (!ended || repeat != 0)
			{
				return 0;
			}
			return -1;
		}
		return num2;
	}

	public static string Duration(double seconds)
	{
		seconds = ((double.IsNaN(seconds) || double.IsInfinity(seconds)) ? 0.0 : Math.Max(0.0, seconds));
		TimeSpan timeSpan = TimeSpan.FromSeconds(Math.Min(seconds, 359999.0));
		if (!(timeSpan.TotalHours >= 1.0))
		{
			return (int)timeSpan.TotalMinutes + ":" + timeSpan.Seconds.ToString("00");
		}
		return (int)timeSpan.TotalHours + ":" + timeSpan.Minutes.ToString("00") + ":" + timeSpan.Seconds.ToString("00");
	}
}
