namespace GlassBoard;

public static class BackgroundWorkRules
{
	public static double MediaPollSeconds(bool visible, bool available, bool playing)
	{
		if (visible)
		{
			if (available)
			{
				if (!playing)
				{
					// 暂停时也要快速轮询：恢复播放/拖动进度都发生在暂停态，慢轮询会造成歌词滞后数秒
					return 0.6;
				}
				return 1.0;
			}
			return 1.0;
		}
		if (playing)
		{
			return 1.5;
		}
		return 2.0;
	}
}
