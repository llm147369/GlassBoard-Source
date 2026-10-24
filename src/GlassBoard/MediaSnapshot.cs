using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class MediaSnapshot
{
	[DataMember]
	public bool Available;

	[DataMember]
	public bool Playing;

	[DataMember]
	public bool CanSeek;

	[DataMember]
	public bool CanPrevious;

	[DataMember]
	public bool CanNext;

	[DataMember]
	public bool CanToggle;

	[DataMember]
	public string Status;

	[DataMember]
	public string Title;

	[DataMember]
	public string Artist;

	[DataMember]
	public string Album;

	[DataMember]
	public string Source;

	[DataMember]
	public string Key;

	[DataMember]
	public string Cover;

	[DataMember]
	public string Backend;

	[DataMember]
	public string Value;

	[DataMember]
	public string CoverStatus;

	[DataMember]
	public string MetadataStatus;

	[DataMember]
	public string TimelineStatus;

	[DataMember]
	public double Position;

	[DataMember]
	public double Duration;

	[DataMember]
	public double Age;

	[DataMember]
	public double MinSeek;

	[DataMember]
	public double MaxSeek;

	[DataMember]
	public double Rate = 1.0;

	[OnDeserializing]
	private void Defaults(StreamingContext context)
	{
		Rate = 1.0;
	}

	public MediaSnapshot Copy()
	{
		return (MediaSnapshot)MemberwiseClone();
	}
}
