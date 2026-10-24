using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class GlobalStyleOptions
{
	[DataMember]
	public bool Unify = false;

	[DataMember]
	public MaterialOptions Material = new MaterialOptions();

	[DataMember]
	public double OpacityPercent = 72.0;

	[DataMember]
	public double Blur = 18.0;

	[DataMember]
	public double Edge = 1.0;

	[DataMember]
	public double Radius = 28.0;

	[OnDeserializing]
	private void Defaults(StreamingContext ctx)
	{
		Material = new MaterialOptions();
		OpacityPercent = 72.0;
		Blur = 18.0;
		Edge = 1.0;
		Radius = 28.0;
	}

	public void Normalize()
	{
		if (Material == null)
		{
			Material = new MaterialOptions();
		}
		Material.Normalize();
		OpacityPercent = LayoutRules.Clamp(OpacityPercent, 10.0, 100.0, 72.0);
		Blur = LayoutRules.Clamp(Blur, 0.0, 40.0, 18.0);
		Edge = LayoutRules.Clamp(Edge, 0.2, 2.0, 1.0);
		Radius = LayoutRules.Clamp(Radius, 0.0, 48.0, 28.0);
	}
}
