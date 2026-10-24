using System;
using System.Linq;
using System.Text;

namespace GlassBoard;

public static class NeteaseTimelineRules
{
	public static bool Time(string text, out double seconds)
	{
		seconds = 0.0;
		string[] array = text.Trim().Split(':');
		if (array.Length < 2 || array.Length > 3)
		{
			return false;
		}
		int[] array2 = new int[array.Length];
		for (int i = 0; i < array.Length; i++)
		{
			if (!int.TryParse(array[i], out array2[i]) || array2[i] < 0)
			{
				return false;
			}
		}
		if (array2[array.Length - 1] > 59 || (array.Length == 3 && array2[1] > 59))
		{
			return false;
		}
		seconds = ((array.Length == 3) ? ((double)array2[0] * 3600.0 + (double)(array2[1] * 60) + (double)array2[2]) : ((double)array2[0] * 60.0 + (double)array2[1]));
		return seconds <= 359999.0;
	}

	public static bool Progress(string text, out double current, out double total)
	{
		current = (total = 0.0);
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string[] array = text.Normalize(NormalizationForm.FormKC).Replace(" ", "").Split('/', '|');
		if (array.Length == 2 && Time(array[0], out current) && Time(array[1], out total) && total > 0.0)
		{
			return current <= total;
		}
		return false;
	}

	public static bool Source(string source)
	{
		return (source ?? "").IndexOf("cloudmusic", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	public static bool WindowTitle(string name, string track)
	{
		if (!string.IsNullOrWhiteSpace(track) && !string.IsNullOrWhiteSpace(name) && name.IndexOf("MediaPlayer", StringComparison.OrdinalIgnoreCase) < 0)
		{
			if (!name.Trim().Equals(track.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return new string[3] { " - ", " – ", " — " }.Any((string separator) => name.Trim().StartsWith(track.Trim() + separator, StringComparison.OrdinalIgnoreCase));
			}
			return true;
		}
		return false;
	}

	public static double RangeTarget(double seconds, double duration, double min, double max)
	{
		if (duration <= 0.0 || max <= min || double.IsNaN(seconds) || double.IsInfinity(seconds))
		{
			throw new ArgumentException("Invalid playback range");
		}
		return min + Math.Max(0.0, Math.Min(1.0, seconds / duration)) * (max - min);
	}
}
