using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class WidgetPanelPosition
{
	[DataMember(EmitDefaultValue = false)]
	public bool? HideControls;

	[DataMember]
	public int Id;

	[DataMember]
	public double? Left;

	[DataMember]
	public double? Top;

	[DataMember]
	public double? Width;

	[DataMember]
	public double? Height;

	[DataMember]
	public double? OpacityPercent;

	public void Normalize()
	{
		Width = WidgetSizing.Dimension(Width, 240.0, 1600.0);
		Height = WidgetSizing.Dimension(Height, 140.0, 1600.0);
		if (OpacityPercent.HasValue)
		{
			OpacityPercent = LayoutRules.Clamp(OpacityPercent.Value, 0.0, 100.0, 72.0);
		}
		if (Left.HasValue && (double.IsNaN(Left.Value) || double.IsInfinity(Left.Value)))
		{
			Left = null;
		}
		if (Top.HasValue && (double.IsNaN(Top.Value) || double.IsInfinity(Top.Value)))
		{
			Top = null;
		}
	}

	public WidgetPanelPosition Copy()
	{
		return (WidgetPanelPosition)MemberwiseClone();
	}
}
