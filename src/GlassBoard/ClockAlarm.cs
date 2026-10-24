using System;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class ClockAlarm
{
	[DataMember]
	public string Id = Guid.NewGuid().ToString("N");

	[DataMember]
	public string Label = "闹钟";

	[DataMember]
	public int Hour;

	[DataMember]
	public int Minute;

	[DataMember]
	public bool Enabled = true;

	[DataMember]
	public bool Daily;

	[DataMember]
	public DateTime DueLocal;

	public ClockAlarm Copy()
	{
		return (ClockAlarm)MemberwiseClone();
	}

	public void Normalize()
	{
		if (string.IsNullOrEmpty(Id))
		{
			Id = Guid.NewGuid().ToString("N");
		}
		Label = (string.IsNullOrWhiteSpace(Label) ? "闹钟" : Label.Trim());
		if (Label.Length > 80)
		{
			Label = Label.Substring(0, 80);
		}
		Hour = Math.Max(0, Math.Min(23, Hour));
		Minute = Math.Max(0, Math.Min(59, Minute));
		if (DueLocal.Year < 2000)
		{
			DueLocal = ClockRules.Next(Hour, Minute, DateTime.Now);
		}
	}
}
