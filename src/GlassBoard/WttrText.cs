using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class WttrText
{
	[DataMember(Name = "value")]
	public string Value;
}
