using System;

namespace GlassBoard;

public class AutoHidePolicy
{
	public bool Hidden;

	private DateTime lastSeen = DateTime.MinValue;

	public bool Update(DateTime now, bool enabled, bool over, bool edge, bool blocked, double delay)
	{
		if (!enabled | over | edge | blocked)
		{
			lastSeen = now;
			Hidden = false;
		}
		else if (lastSeen == DateTime.MinValue)
		{
			lastSeen = now;
		}
		else if ((now - lastSeen).TotalMilliseconds >= delay)
		{
			Hidden = true;
		}
		return Hidden;
	}
}
