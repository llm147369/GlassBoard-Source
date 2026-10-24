using System.Collections.Generic;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class WttrArea
{
	[DataMember(Name = "areaName")]
	public List<WttrText> Name;

	[DataMember(Name = "country")]
	public List<WttrText> Country;

	[DataMember(Name = "latitude")]
	public string Latitude;

	[DataMember(Name = "longitude")]
	public string Longitude;
}
