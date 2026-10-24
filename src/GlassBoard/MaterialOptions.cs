using System;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace GlassBoard;

[DataContract]
public class MaterialOptions
{
	[DataMember]
	public int Kind;

	[DataMember]
	public string Tint = "";

	[DataMember]
	public double Gloss = 0.15;

	[DataMember]
	public double Grain;

	[DataMember]
	public bool Shadow = true;

	[OnDeserializing]
	private void Defaults(StreamingContext context)
	{
		Gloss = 0.15;
		Shadow = true;
		Tint = "";
	}

	public void Normalize()
	{
		Kind = Math.Max(0, Math.Min(3, Kind));
		Gloss = LayoutRules.Clamp(Gloss, 0.0, 1.0, 0.15);
		Grain = LayoutRules.Clamp(Grain, 0.0, 1.0, 0.0);
		if (!Regex.IsMatch(Tint ?? "", "^#[a-fA-F0-9]{6}$"))
		{
			Tint = "";
		}
	}

	public MaterialOptions Copy()
	{
		return (MaterialOptions)MemberwiseClone();
	}
}
