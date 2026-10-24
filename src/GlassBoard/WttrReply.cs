using System.Collections.Generic;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class WttrReply
{
	[DataMember(Name = "current_condition")]
	public List<WttrCurrent> Current;

	[DataMember(Name = "nearest_area")]
	public List<WttrArea> Areas;
}
