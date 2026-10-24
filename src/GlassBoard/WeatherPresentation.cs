using System.Text.RegularExpressions;

namespace GlassBoard;

public static class WeatherPresentation
{
	public static string Place(string requested, GeoPlace resolved)
	{
		if (!string.IsNullOrWhiteSpace(requested) && !WeatherClient.Coordinates(requested, out var _))
		{
			return WeatherClient.NormalizeCity(requested);
		}
		if (resolved != null)
		{
			return resolved.Name;
		}
		return "未知地点";
	}

	public static string Description(int code, string description)
	{
		string text = (description ?? "").Trim();
		if (Regex.IsMatch(text, "[\\u3400-\\u9fff]"))
		{
			return text;
		}
		string text2 = text.ToLowerInvariant();
		if (text2.Contains("thunder"))
		{
			return "雷雨";
		}
		if (text2.Contains("blizzard"))
		{
			return "暴风雪";
		}
		if (text2.Contains("sleet"))
		{
			return "雨夹雪";
		}
		if (text2.Contains("snow"))
		{
			if (!text2.Contains("heavy"))
			{
				if (!text2.Contains("light"))
				{
					return "雪";
				}
				return "小雪";
			}
			return "大雪";
		}
		if (text2.Contains("freezing") && (text2.Contains("rain") || text2.Contains("drizzle")))
		{
			return "冻雨";
		}
		if (text2.Contains("rain") || text2.Contains("drizzle"))
		{
			if (!text2.Contains("heavy") && !text2.Contains("torrential"))
			{
				if (!text2.Contains("moderate"))
				{
					if (!text2.Contains("light") && !text2.Contains("drizzle"))
					{
						if (!text2.Contains("shower"))
						{
							return "雨";
						}
						return "阵雨";
					}
					return "小雨";
				}
				return "中雨";
			}
			return "大雨";
		}
		if (text2.Contains("fog") || text2.Contains("mist"))
		{
			return "雾";
		}
		if (text2.Contains("overcast"))
		{
			return "阴";
		}
		if (text2.Contains("cloud"))
		{
			return "多云";
		}
		if (text2.Contains("sunny"))
		{
			return "晴";
		}
		if (text2.Contains("clear"))
		{
			return "晴";
		}
		if (text2.Contains("ice") || text2.Contains("hail"))
		{
			return "冰粒";
		}
		return WmoDescription(code);
	}

	private static string WmoDescription(int code)
	{
		switch (code)
		{
		case 0:
			return "晴";
		case 1:
			return "晴间多云";
		case 2:
			return "多云";
		case 3:
			return "阴";
		case 45:
		case 48:
			return "雾";
		case 51:
		case 53:
		case 55:
			return "毛毛雨";
		case 56:
		case 57:
		case 66:
		case 67:
			return "冻雨";
		case 61:
			return "小雨";
		case 63:
			return "中雨";
		case 65:
			return "大雨";
		case 71:
			return "小雪";
		case 73:
			return "中雪";
		case 75:
			return "大雪";
		case 77:
			return "雪粒";
		case 80:
		case 81:
		case 82:
			return "阵雨";
		default:
			switch (code)
			{
			case 85:
			case 86:
				return "阵雪";
			case 95:
			case 96:
			case 99:
				return "雷雨";
			default:
				return "天气暂未知";
			}
		}
	}

	public static string Icon(int code, string description)
	{
		string text = Description(code, description);
		if (text.Contains("雷"))
		{
			return "storm";
		}
		if (text.Contains("雪") || text.Contains("冰"))
		{
			return "snow";
		}
		if (text.Contains("雨"))
		{
			return "rain";
		}
		if (text.Contains("雾"))
		{
			return "fog";
		}
		if (text.Contains("云") || text.Contains("阴"))
		{
			return "cloud";
		}
		if (text == "晴")
		{
			return "sun";
		}
		return "cloud";
	}
}
