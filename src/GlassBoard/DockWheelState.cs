using System;

namespace GlassBoard;

public sealed class DockWheelState
{
	private long last = long.MinValue;

	private int accumulated;

	public bool Running { get; private set; }

	public bool Scroll(int delta, long milliseconds)
	{
		if (delta == 0)
		{
			return false;
		}
		if (last != long.MinValue && milliseconds - last < 280)
		{
			accumulated = 0;
			return false;
		}
		if (Math.Sign(accumulated) != Math.Sign(delta))
		{
			accumulated = 0;
		}
		accumulated += delta;
		if (Math.Abs(accumulated) < 120)
		{
			return false;
		}
		bool flag = accumulated < 0;
		accumulated = 0;
		if (flag == Running)
		{
			return false;
		}
		Running = flag;
		last = milliseconds;
		return true;
	}

	public void Set(bool running)
	{
		Running = running;
		accumulated = 0;
	}
}
