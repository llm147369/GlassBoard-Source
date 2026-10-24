using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class GeoPlace
{
	[DataMember(Name = "name")]
	public string Name;

	[DataMember(Name = "country")]
	public string Country;

	[DataMember(Name = "latitude")]
	public double Latitude;

	[DataMember(Name = "longitude")]
	public double Longitude;
}
