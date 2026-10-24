using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class PlacementOptions
{
	[DataMember]
	public double? DockCenter;

	[DataMember]
	public double? DockLeft;

	[DataMember]
	public double? DockTop;

	[DataMember]
	public double? PanelLeft;

	[DataMember]
	public double? PanelTop;

	[DataMember]
	public double? PanelWidth;

	[DataMember]
	public double? PanelHeight;

	[DataMember]
	public double? WidgetLeft;

	[DataMember]
	public double? WidgetTop;

	[DataMember]
	public bool DockLocked = true;

	[DataMember]
	public bool PanelLocked;

	[OnDeserializing]
	private void Defaults(StreamingContext context)
	{
		DockLocked = true;
		PanelLocked = false;
	}

	public PlacementOptions Copy()
	{
		return (PlacementOptions)MemberwiseClone();
	}

	public void Normalize()
	{
		DockCenter = Valid(DockCenter);
		DockLeft = Valid(DockLeft);
		DockTop = Valid(DockTop);
		PanelLeft = Valid(PanelLeft);
		PanelTop = Valid(PanelTop);
		WidgetLeft = Valid(WidgetLeft);
		WidgetTop = Valid(WidgetTop);
		PanelWidth = Valid(PanelWidth);
		PanelHeight = Valid(PanelHeight);
		if (PanelWidth < 320.0)
		{
			PanelWidth = null;
		}
		if (PanelHeight < 320.0)
		{
			PanelHeight = null;
		}
	}

	private static double? Valid(double? x)
	{
		if (!x.HasValue || (!double.IsNaN(x.Value) && !double.IsInfinity(x.Value)))
		{
			return x;
		}
		return null;
	}
}
