using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace GlassBoard;

[DataContract]
public class BoardEditState
{
	[DataMember]
	public List<Entry> Apps;

	[DataMember]
	public List<string> Removed;

	[DataMember]
	public List<string> DockOrder;

	[DataMember]
	public List<string> RemovedIdentities;

	[DataMember]
	public List<string> DockItems;

	[DataMember]
	public List<string> DockRunningItems;

	public static BoardEditState Capture(Preferences p)
	{
		return new BoardEditState
		{
			Apps = p.Apps.Select(FeatureRules.Copy).ToList(),
			DockItems = ((p.Dock == null) ? new List<string>() : (p.Dock.ItemOrder ?? new List<string>()).ToList()),
			DockRunningItems = ((p.Dock == null) ? new List<string>() : (p.Dock.RunningOrder ?? new List<string>()).ToList()),
			DockOrder = (p.DockOrder ?? new List<string>()).ToList(),
			Removed = p.Removed.ToList(),
			RemovedIdentities = (p.RemovedIdentities ?? new List<string>()).ToList()
		};
	}

	public void Apply(Preferences p)
	{
		if (p.Dock == null)
		{
			p.Dock = new DockOptions();
		}
		p.Dock.ItemOrder = (DockItems ?? new List<string>()).ToList();
		p.Dock.RunningOrder = (DockRunningItems ?? new List<string>()).ToList();
		p.Apps = Apps.Select(FeatureRules.Copy).ToList();
		p.DockOrder = (DockOrder ?? new List<string>()).ToList();
		p.Removed = Removed.ToList();
		p.RemovedIdentities = (RemovedIdentities ?? new List<string>()).ToList();
		p.Normalize();
	}

	public string Signature()
	{
		using MemoryStream memoryStream = new MemoryStream();
		new DataContractJsonSerializer(typeof(BoardEditState)).WriteObject((Stream)memoryStream, (object)this);
		return Encoding.UTF8.GetString(memoryStream.ToArray());
	}
}
