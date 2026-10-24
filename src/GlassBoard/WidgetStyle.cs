using System;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace GlassBoard;

[DataContract]
public class WidgetStyle
{
	[DataMember]
	public string Kind = "clock";

	[DataMember]
	public string TextColor = "";

	[DataMember]
	public string Font = "";

	[DataMember]
	public double TextOpacity = 100.0;

	[DataMember]
	public double ClockSize = 48.0;

	[DataMember]
	public bool Frameless;

	[DataMember]
	public bool TextShadow;

	[DataMember]
	public bool ShowDate = true;

	[DataMember]
	public bool Hour24 = true;

	[DataMember]
	public int ClockStyle;

	[DataMember]
	public int MusicButtons;

	[OnDeserializing]
	private void Defaults(StreamingContext c)
	{
		TextOpacity = 100.0;
		ClockSize = 48.0;
		ShowDate = (Hour24 = true);
		TextColor = (Font = "");
	}

	public void Normalize()
	{
		TextColor = Color(TextColor);
		Font = (Font ?? "").Trim();
		if (Font.Length > 120)
		{
			Font = "";
		}
		TextOpacity = LayoutRules.Clamp(TextOpacity, 0.0, 100.0, 100.0);
		ClockSize = LayoutRules.Clamp(ClockSize, 28.0, 100.0, 48.0);
		ClockStyle = Math.Max(0, Math.Min(ClockCatalog.Names.Length - 1, ClockStyle));
		MusicButtons = Math.Max(0, Math.Min(4, MusicButtons));
	}

	public static string Color(string value)
	{
		value = (value ?? "").Trim();
		if (!value.StartsWith("#") && value.Length == 6)
		{
			value = "#" + value;
		}
		if (!Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$"))
		{
			return "";
		}
		return value.ToUpperInvariant();
	}

	public WidgetStyle Copy()
	{
		return (WidgetStyle)MemberwiseClone();
	}

	public string Signature()
	{
		return Kind + "/" + TextColor + "/" + Font + "/" + TextOpacity + "/" + ClockSize + "/" + Frameless + "/" + TextShadow + "/" + ShowDate + "/" + Hour24 + "/" + ClockStyle + "/" + MusicButtons;
	}
}
