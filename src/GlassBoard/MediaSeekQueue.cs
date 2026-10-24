namespace GlassBoard;

public sealed class MediaSeekQueue
{
	private double? value;

	private string key;

	public void Put(double seconds, string track)
	{
		value = seconds;
		key = track;
	}

	public bool Take(string track, out double seconds)
	{
		seconds = 0.0;
		if (!value.HasValue)
		{
			return false;
		}
		double num = value.Value;
		value = null;
		if (key != track)
		{
			return false;
		}
		seconds = num;
		return true;
	}

	public void Clear()
	{
		value = null;
		key = null;
	}
}
