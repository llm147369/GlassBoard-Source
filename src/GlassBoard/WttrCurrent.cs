using System.Collections.Generic;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class WttrCurrent
{
	[DataMember(Name = "temp_C")]
	public string Temperature;

	[DataMember(Name = "windspeedKmph")]
	public string Wind;

	[DataMember(Name = "weatherDesc")]
	public List<WttrText> Description;

	[DataMember(Name = "lang_zh")]
	public List<WttrText> Chinese;

	[DataMember(Name = "lang_xx")]
	public List<WttrText> Localized;
}
