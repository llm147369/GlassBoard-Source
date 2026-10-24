using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class WeatherReply
{
	[DataMember(Name = "current")]
	public WeatherCurrent Current;
}
