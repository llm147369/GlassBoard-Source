using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class RemovalJournal
{
	[DataMember]
	public List<string> Paths = new List<string>();

	[DataMember]
	public List<string> Identities = new List<string>();

	[DataMember]
	public List<string> ActivePaths = new List<string>();

	public void Merge(Preferences prefs)
	{
		prefs.Removed = (from p in (prefs.Removed ?? new List<string>()).Concat(Paths ?? new List<string>())
			where !string.IsNullOrWhiteSpace(p)
			select p).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		prefs.RemovedIdentities = (from p in (prefs.RemovedIdentities ?? new List<string>()).Concat(Identities ?? new List<string>())
			where !string.IsNullOrWhiteSpace(p)
			select p).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}
}
