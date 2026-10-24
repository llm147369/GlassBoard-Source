namespace GlassBoard;

public static class MusicMetadataRecovery
{
	public static MediaSnapshot Recover(MediaSnapshot previous, MediaSnapshot current)
	{
		if (current == null)
		{
			return new MediaSnapshot();
		}
		if (previous == null || !previous.Available || !current.Available || previous.Source != current.Source || string.IsNullOrEmpty(previous.Key) || string.IsNullOrEmpty(current.MetadataStatus) || current.MetadataStatus == "OK")
		{
			return current;
		}
		MediaSnapshot mediaSnapshot = current.Copy();
		mediaSnapshot.Title = previous.Title;
		mediaSnapshot.Artist = previous.Artist;
		mediaSnapshot.Album = previous.Album;
		mediaSnapshot.Key = previous.Key;
		mediaSnapshot.Cover = previous.Cover;
		mediaSnapshot.CoverStatus = previous.CoverStatus;
		return mediaSnapshot;
	}
}
