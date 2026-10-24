using System.Collections.Generic;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class BackupManifest
{
	[DataMember]
	public int Version;

	[DataMember]
	public string AppVersion;

	[DataMember]
	public string CreatedUtc;

	[DataMember]
	public Dictionary<string, string> Icons;
}
