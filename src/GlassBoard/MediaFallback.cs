namespace GlassBoard;

public static class MediaFallback
{
	public static MediaSnapshot Merge(MediaSnapshot standard, MediaSnapshot extra)
	{
		if (standard == null)
		{
			return new MediaSnapshot();
		}
		MediaSnapshot mediaSnapshot = standard.Copy();
		if (!standard.Available || extra == null || !extra.Available || string.IsNullOrEmpty(standard.Key) || standard.Key != extra.Key || standard.Source != extra.Source)
		{
			return mediaSnapshot;
		}
		if (string.IsNullOrEmpty(mediaSnapshot.Cover) && !string.IsNullOrEmpty(extra.Cover))
		{
			mediaSnapshot.Cover = extra.Cover;
			mediaSnapshot.CoverStatus = extra.CoverStatus;
		}
		if (extra.Duration > 0.0 && !double.IsNaN(extra.Duration) && !double.IsInfinity(extra.Duration) && (mediaSnapshot.Duration <= 0.0 || (!mediaSnapshot.CanSeek && extra.CanSeek)))
		{
			mediaSnapshot.Duration = extra.Duration;
			mediaSnapshot.Position = extra.Position;
			mediaSnapshot.MinSeek = extra.MinSeek;
			mediaSnapshot.MaxSeek = extra.MaxSeek;
			mediaSnapshot.Age = extra.Age;
			mediaSnapshot.Rate = extra.Rate;
			mediaSnapshot.CanSeek = extra.CanSeek;
			mediaSnapshot.TimelineStatus = extra.TimelineStatus;
			mediaSnapshot.Backend = ((extra.Backend == "Netease UIA") ? extra.Backend : "NPSMLib isolated");
		}
		return mediaSnapshot;
	}
}
