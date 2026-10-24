using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class MediaRequest
{
	[DataMember]
	public string Command;

	[DataMember]
	public double Seconds;

	[DataMember]
	public string Key;

	[DataMember]
	public string Source;

	[DataMember]
	public string Title;

	[DataMember]
	public string Artist;

	[DataMember]
	public string Album;

	[DataMember]
	public bool Playing;

	[DataMember]
	public bool UseNative;

	[DataMember]
	public bool CoverOnly;
}
