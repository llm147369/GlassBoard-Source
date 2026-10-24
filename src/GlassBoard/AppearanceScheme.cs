using System.Collections.Generic;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class AppearanceScheme
{
	[DataMember]
	public int Version = 1;

	[DataMember]
	public string Name;

	[DataMember]
	public bool Light;

	[DataMember]
	public bool BoardHideSearch;

	[DataMember]
	public bool BoardHideGroups;

	[DataMember]
	public bool BoardHideFolders;

	[DataMember]
	public int PanelSize;

	[DataMember]
	public double BoardIconSize = 48.0;

	[DataMember]
	public double BoardIconGap = 5.0;

	[DataMember]
	public double OpacityPercent = 72.0;

	[DataMember]
	public double DockGap = 12.0;

	[DataMember]
	public DockOptions Dock;

	[DataMember]
	public MaterialOptions BoardMaterial;

	[DataMember]
	public PlacementOptions Placement;

	[DataMember]
	public WidgetOptions Widgets;

	[DataMember]
	public List<Entry> Apps;

	[DataMember]
	public List<string> Groups;

	[DataMember(EmitDefaultValue = false)]
	public List<string> DockOrder;

	[DataMember]
	public Dictionary<string, string> Icons = new Dictionary<string, string>();

	[OnDeserializing]
	private void Defaults(StreamingContext context)
	{
		BoardIconSize = 48.0;
		BoardIconGap = 5.0;
	}
}
