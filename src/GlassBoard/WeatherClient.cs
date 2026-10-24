using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace GlassBoard;

public static class WeatherClient
{
	private static readonly object gate = new object();

	private static readonly Dictionary<string, GeoPlace> places = new Dictionary<string, GeoPlace>(StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<string, string> aliases = new Dictionary<string, string>
	{
		{ "北京", "Beijing" },
		{ "上海", "Shanghai" },
		{ "天津", "Tianjin" },
		{ "重庆", "Chongqing" },
		{ "杭州", "Hangzhou" },
		{ "广州", "Guangzhou" },
		{ "深圳", "Shenzhen" },
		{ "南京", "Nanjing" },
		{ "成都", "Chengdu" },
		{ "武汉", "Wuhan" },
		{ "西安", "Xi'an" },
		{ "苏州", "Suzhou" },
		{ "郑州", "Zhengzhou" },
		{ "长沙", "Changsha" },
		{ "济南", "Jinan" },
		{ "青岛", "Qingdao" },
		{ "沈阳", "Shenyang" },
		{ "大连", "Dalian" },
		{ "哈尔滨", "Harbin" },
		{ "长春", "Changchun" },
		{ "福州", "Fuzhou" },
		{ "厦门", "Xiamen" },
		{ "昆明", "Kunming" },
		{ "贵阳", "Guiyang" },
		{ "南宁", "Nanning" },
		{ "南昌", "Nanchang" },
		{ "合肥", "Hefei" },
		{ "石家庄", "Shijiazhuang" },
		{ "太原", "Taiyuan" },
		{ "兰州", "Lanzhou" },
		{ "西宁", "Xining" },
		{ "银川", "Yinchuan" },
		{ "乌鲁木齐", "Urumqi" },
		{ "拉萨", "Lhasa" },
		{ "海口", "Haikou" },
		{ "香港", "Hong Kong" },
		{ "澳门", "Macau" },
		{ "台北", "Taipei" }
	};

	public static string NormalizeCity(string city)
	{
		city = (city ?? "").Trim().Normalize(NormalizationForm.FormKC).Replace('，', ',');
		if (city.IndexOf(',') >= 0)
		{
			return city;
		}
		int num = city.LastIndexOf('省');
		if (num >= 0 && num < city.Length - 1)
		{
			city = city.Substring(num + 1);
		}
		if (city.Length > 2 && city.EndsWith("市", StringComparison.Ordinal))
		{
			city = city.Substring(0, city.Length - 1);
		}
		return city.Trim();
	}

	public static T Read<T>(Stream stream)
	{
		return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
	}

	private static T Get<T>(string url)
	{
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(url);
		httpWebRequest.Timeout = 8000;
		httpWebRequest.ReadWriteTimeout = 8000;
		httpWebRequest.UserAgent = "GlassBoard/" + Assembly.GetExecutingAssembly().GetName().Version;
		httpWebRequest.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
		using WebResponse webResponse = httpWebRequest.GetResponse();
		using Stream stream = webResponse.GetResponseStream();
		return Read<T>(stream);
	}

	public static bool Coordinates(string value, out GeoPlace place)
	{
		place = null;
		string[] array = (value ?? "").Split(',');
		if (array.Length != 2 || !double.TryParse(array[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.TryParse(array[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var result2) || double.IsNaN(result) || double.IsNaN(result2) || double.IsInfinity(result) || double.IsInfinity(result2) || Math.Abs(result) > 90.0 || Math.Abs(result2) > 180.0)
		{
			return false;
		}
		place = new GeoPlace
		{
			Name = value,
			Country = "经纬度",
			Latitude = result,
			Longitude = result2
		};
		return true;
	}

	private static GeoPlace Resolve(string city, Func<string, GeoReply> reader)
	{
		if (Coordinates(city, out var place))
		{
			return place;
		}
		lock (gate)
		{
			if (places.TryGetValue(city, out place))
			{
				return place;
			}
		}
		List<string> list = new List<string> { city };
		if (aliases.TryGetValue(city, out var value))
		{
			List<string> list2 = list;
			string text = value;
			string text2;
			if (city == "台北")
			{
				text2 = "TW";
			}
			else if (city == "香港")
			{
				text2 = "HK";
			}
			else
			{
				text2 = ((city == "澳门") ? "MO" : "CN");
			}
			list2.Add(text + ", " + text2);
		}
		foreach (string item in list)
		{
			GeoReply geoReply = reader("https://geocoding-api.open-meteo.com/v1/search?count=10&language=zh&format=json&name=" + Uri.EscapeDataString(item));
			if (geoReply == null || geoReply.Results == null || geoReply.Results.Count <= 0)
			{
				continue;
			}
			GeoPlace geoPlace = geoReply.Results.FirstOrDefault((GeoPlace x) => NormalizeCity(x.Name).Equals(city, StringComparison.OrdinalIgnoreCase)) ?? geoReply.Results[0];
			lock (gate)
			{
				if (places.Count >= 100)
				{
					places.Clear();
				}
				places[city] = geoPlace;
			}
			return geoPlace;
		}
		throw new InvalidOperationException("未找到城市，请填写城市名、城市加国家，或纬度,经度");
	}

	private static string Text(List<WttrText> values)
	{
		if (values != null)
		{
			return string.Join(" ", from x in values
				where x != null && !string.IsNullOrWhiteSpace(x.Value)
				select x.Value);
		}
		return "";
	}

	public static WeatherSnapshot FromWttr(WttrReply reply)
	{
		if (reply == null || reply.Current == null || reply.Current.Count == 0 || reply.Areas == null || reply.Areas.Count == 0)
		{
			throw new InvalidDataException("备用天气服务未返回有效地点和天气");
		}
		WttrCurrent wttrCurrent = reply.Current[0];
		WttrArea wttrArea = reply.Areas[0];
		if (!double.TryParse(wttrCurrent.Temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.TryParse(wttrCurrent.Wind, NumberStyles.Float, CultureInfo.InvariantCulture, out var result2) || double.IsNaN(result) || double.IsInfinity(result) || result < -100.0 || result > 70.0 || double.IsNaN(result2) || double.IsInfinity(result2) || result2 < 0.0 || !Coordinates(wttrArea.Latitude + "," + wttrArea.Longitude, out var place))
		{
			throw new InvalidDataException("备用天气数据格式不正确");
		}
		place.Name = Text(wttrArea.Name);
		place.Country = Text(wttrArea.Country);
		if (string.IsNullOrWhiteSpace(place.Name) || string.IsNullOrWhiteSpace(place.Country))
		{
			throw new InvalidDataException("备用天气服务未返回地点名称");
		}
		string text = Text(wttrCurrent.Chinese);
		if (!Regex.IsMatch(text ?? "", "[\\u3400-\\u9fff]") && Regex.IsMatch(Text(wttrCurrent.Localized), "[\\u3400-\\u9fff]"))
		{
			text = Text(wttrCurrent.Localized);
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			text = Text(wttrCurrent.Description);
		}
		text = WeatherPresentation.Description(-1, text);
		return new WeatherSnapshot
		{
			Place = place,
			Provider = "wttr.in",
			Current = new WeatherCurrent
			{
				Temperature = result,
				Wind = result2,
				Description = text,
				Code = -1
			}
		};
	}

	public static string Failure(Exception exception)
	{
		Exception baseException = exception.GetBaseException();
		WebException ex = (exception as WebException) ?? (baseException as WebException);
		if (ex != null)
		{
			if (ex.Status == WebExceptionStatus.Timeout)
			{
				return "天气服务连接超时";
			}
			if (ex.Status == WebExceptionStatus.NameResolutionFailure || ex.Status == WebExceptionStatus.ProxyNameResolutionFailure)
			{
				return "天气域名或代理解析失败";
			}
			if (ex.Status == WebExceptionStatus.TrustFailure || ex.Status == WebExceptionStatus.SecureChannelFailure)
			{
				return "天气 HTTPS 连接失败，请检查系统时间和证书";
			}
			if (ex.Response is HttpWebResponse httpWebResponse)
			{
				return "天气服务返回 HTTP " + (int)httpWebResponse.StatusCode;
			}
			return "天气服务连接失败，请检查网络或系统代理";
		}
		if (!(baseException is InvalidOperationException) && !(baseException is ArgumentException))
		{
			return "天气数据暂不可用";
		}
		return baseException.Message;
	}

	public static WeatherSnapshot Fetch(string city)
	{
		return Fetch(city, Get<GeoReply>, Get<WeatherReply>, Get<WttrReply>);
	}

	public static WeatherSnapshot Fetch(string city, Func<string, GeoReply> geoReader, Func<string, WeatherReply> forecastReader, Func<string, WttrReply> backupReader)
	{
		if (string.IsNullOrWhiteSpace(city))
		{
			throw new ArgumentException("请先在设置中填写天气城市");
		}
		city = NormalizeCity(city);
		GeoPlace geoPlace = null;
		Exception ex = null;
		try
		{
			geoPlace = Resolve(city, geoReader);
			string text = geoPlace.Latitude.ToString(CultureInfo.InvariantCulture);
			string text2 = geoPlace.Longitude.ToString(CultureInfo.InvariantCulture);
			WeatherReply weatherReply = forecastReader("https://api.open-meteo.com/v1/forecast?latitude=" + text + "&longitude=" + text2 + "&current=temperature_2m,weather_code,wind_speed_10m&timezone=auto");
			if (weatherReply == null || weatherReply.Current == null)
			{
				throw new InvalidDataException("天气服务没有返回当前数据");
			}
			return new WeatherSnapshot
			{
				Place = geoPlace,
				Current = weatherReply.Current,
				Provider = "Open-Meteo"
			};
		}
		catch (Exception ex2)
		{
			ex = ex2;
		}
		try
		{
			string stringToEscape = ((geoPlace == null) ? city : (geoPlace.Latitude.ToString(CultureInfo.InvariantCulture) + "," + geoPlace.Longitude.ToString(CultureInfo.InvariantCulture)));
			return FromWttr(backupReader("https://wttr.in/" + Uri.EscapeDataString(stringToEscape) + "?format=j1&lang=zh"));
		}
		catch (Exception ex3)
		{
			throw new InvalidOperationException("主服务：" + Failure(ex) + "；备用服务：" + Failure(ex3), ex3);
		}
	}
}
