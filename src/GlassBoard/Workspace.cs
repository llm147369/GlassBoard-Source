using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class Workspace
{
	[DataMember]
	public string Name;

	[DataMember]
	public List<string> Paths = new List<string>();

	public void Normalize()
	{
		Name = (Name ?? "").Trim();
		Paths = (Paths ?? new List<string>()).Where((string p) => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}
}
