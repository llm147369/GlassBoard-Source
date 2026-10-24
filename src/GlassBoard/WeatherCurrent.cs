using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class WeatherCurrent
{
	[DataMember(Name = "temperature_2m")]
	public double Temperature;

	[DataMember(Name = "weather_code")]
	public int Code;

	[DataMember(Name = "wind_speed_10m")]
	public double Wind;

	[DataMember(Name = "time")]
	public string Time;

	[DataMember]
	public string Description;
}
