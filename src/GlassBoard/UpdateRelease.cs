using System.Collections.Generic;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class UpdateRelease
{
	[DataMember(Name = "tag_name")]
	public string Tag;

	[DataMember(Name = "body")]
	public string Notes;

	[DataMember(Name = "draft")]
	public bool Draft;

	[DataMember(Name = "prerelease")]
	public bool Prerelease;

	[DataMember(Name = "assets")]
	public List<UpdateAsset> Assets;
}
