using System;

namespace GlassBoard;

public static class MediaTimeline
{
	public static double Position(double position, double duration, bool playing, double rate, double age)
	{
		duration = LayoutRules.Clamp(duration, 0.0, 359999.0, 0.0);
		if (duration <= 0.0)
		{
			return 0.0;
		}
		position = LayoutRules.Clamp(position, 0.0, duration, 0.0);
		rate = LayoutRules.Clamp(rate, 0.0, 16.0, 1.0);
		age = LayoutRules.Clamp(age, 0.0, 86400.0, 0.0);
		return Math.Min(duration, position + (playing ? (rate * age) : 0.0));
	}

	public static double ClampSeek(double seconds, double minimum, double maximum, double duration)
	{
		duration = LayoutRules.Clamp(duration, 0.0, 359999.0, 0.0);
		minimum = LayoutRules.Clamp(minimum, 0.0, duration, 0.0);
		maximum = LayoutRules.Clamp(maximum, 0.0, duration, duration);
		if (maximum <= minimum)
		{
			maximum = duration;
		}
		return LayoutRules.Clamp(seconds, minimum, maximum, minimum);
	}
}
