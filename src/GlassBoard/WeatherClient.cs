using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
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
		httpWebRequest.AutomaticDecompression = DecompressionMethods.GZip;
		using WebResponse webResponse = httpWebRequest.GetResponse();
		Stream stream = webResponse.GetResponseStream();
		string text = webResponse.Headers["Content-Encoding"] ?? "";
		if (text.IndexOf("deflate", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			// 部分气象接口的 deflate 是 zlib 封装（RFC1950），HttpWebRequest 的原始 deflate
			// 解码器只认裸流 → InvalidDataException；改用手动 DeflateStream（.NET Framework 即 zlib 格式）。
			stream = new DeflateStream(stream, CompressionMode.Decompress);
		}
		using (stream)
		{
			return Read<T>(stream);
		}
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
		// 中文地名：不带国家限定常被匹配到海外同名地点（大量小城市“获取不到”的根因），
		// 追加“中国”限定变体后按顺序尝试。
		bool cjk = Regex.IsMatch(city, "[\\u3400-\\u9fff]");
		if (cjk)
		{
			list.Add(city + ", CN");
			list.Add(city + " 中国");
		}
		foreach (string item in list)
		{
			GeoReply geoReply = reader("https://geocoding-api.open-meteo.com/v1/search?count=10&language=zh&format=json&name=" + Uri.EscapeDataString(item));
			if (geoReply != null && geoReply.Results != null && geoReply.Results.Count > 0)
			{
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
		}
		// open-meteo 找不到时用 Nominatim 兜底（覆盖区县、乡镇等更细的地名）
		foreach (string item in list)
		{
			try
			{
				NominatimReply[] hits = Get<NominatimReply[]>("https://nominatim.openstreetmap.org/search?format=jsonv2&limit=3&accept-language=zh&q=" + Uri.EscapeDataString(item));
				NominatimReply hit = ((hits == null) ? null : hits.FirstOrDefault((NominatimReply x) => x != null && double.TryParse(x.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var _) && double.TryParse(x.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var _)));
				if (hit != null)
				{
					string name = (hit.DisplayName ?? "").Split(',')[0].Trim();
					if (name.Length == 0)
					{
						name = city;
					}
					GeoPlace geoPlace2 = new GeoPlace
					{
						Name = name,
						Country = "CN",
						Latitude = double.Parse(hit.Lat, CultureInfo.InvariantCulture),
						Longitude = double.Parse(hit.Lon, CultureInfo.InvariantCulture)
					};
					lock (gate)
					{
						if (places.Count >= 100)
						{
							places.Clear();
						}
						places[city] = geoPlace2;
					}
					return geoPlace2;
				}
			}
			catch
			{
			}
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

	// —— IP 自动定位（无需手动设置城市）：依次尝试多个免费定位服务 ——
	[DataContract]
	internal sealed class IpInfoReply
	{
		[DataMember(Name = "city")]
		public string City = "";

		[DataMember(Name = "region")]
		public string Region = "";

		[DataMember(Name = "country")]
		public string Country = "";

		[DataMember(Name = "loc")]
		public string Loc = "";
	}

	[DataContract]
	internal sealed class IpApiReply
	{
		[DataMember(Name = "status")]
		public string Status = "";

		[DataMember(Name = "lat")]
		public double Lat = 0.0;

		[DataMember(Name = "lon")]
		public double Lon = 0.0;

		[DataMember(Name = "city")]
		public string City = "";

		[DataMember(Name = "regionName")]
		public string Region = "";

		[DataMember(Name = "country")]
		public string Country = "";
	}

	[DataContract]
	internal sealed class IpapiCoReply
	{
		[DataMember(Name = "latitude")]
		public double Latitude = 0.0;

		[DataMember(Name = "longitude")]
		public double Longitude = 0.0;

		[DataMember(Name = "city")]
		public string City = "";

		[DataMember(Name = "region")]
		public string Region = "";

		[DataMember(Name = "country_name")]
		public string Country = "";
	}

	[DataContract]
	internal sealed class NominatimReply
	{
		[DataMember(Name = "lat")]
		public string Lat = "";

		[DataMember(Name = "lon")]
		public string Lon = "";

		[DataMember(Name = "display_name")]
		public string DisplayName = "";
	}

	private static string JoinName(string city, string region)
	{
		city = (city ?? "").Trim();
		region = (region ?? "").Trim();
		if (city.Length == 0)
		{
			return (region.Length == 0) ? "自动定位" : region;
		}
		if (region.Length == 0 || string.Equals(city, region, StringComparison.OrdinalIgnoreCase))
		{
			return city;
		}
		return city + " · " + region;
	}

	public static GeoPlace AutoLocate()
	{
		Exception last = null;
		try
		{
			IpInfoReply info = Get<IpInfoReply>("https://ipinfo.io/json");
			if (info != null && !string.IsNullOrWhiteSpace(info.Loc) && Coordinates(info.Loc, out GeoPlace place))
			{
				place.Name = JoinName(info.City, info.Region);
				place.Country = string.IsNullOrWhiteSpace(info.Country) ? "自动定位" : info.Country;
				return place;
			}
		}
		catch (Exception ex)
		{
			last = ex;
		}
		try
		{
			IpApiReply api = Get<IpApiReply>("http://ip-api.com/json/?lang=zh-CN&fields=status,lat,lon,city,regionName,country");
			if (api != null && (api.Status == "success") && Math.Abs(api.Lat) <= 90.0 && Math.Abs(api.Lon) <= 180.0 && (Math.Abs(api.Lat) > 0.01 || Math.Abs(api.Lon) > 0.01))
			{
				return new GeoPlace
				{
					Name = JoinName(api.City, api.Region),
					Country = string.IsNullOrWhiteSpace(api.Country) ? "自动定位" : api.Country,
					Latitude = api.Lat,
					Longitude = api.Lon
				};
			}
		}
		catch (Exception ex)
		{
			last = ex;
		}
		try
		{
			IpapiCoReply co = Get<IpapiCoReply>("https://ipapi.co/json/");
			if (co != null && Math.Abs(co.Latitude) <= 90.0 && Math.Abs(co.Longitude) <= 180.0 && (Math.Abs(co.Latitude) > 0.01 || Math.Abs(co.Longitude) > 0.01))
			{
				return new GeoPlace
				{
					Name = JoinName(co.City, co.Region),
					Country = string.IsNullOrWhiteSpace(co.Country) ? "自动定位" : co.Country,
					Latitude = co.Latitude,
					Longitude = co.Longitude
				};
			}
		}
		catch (Exception ex)
		{
			last = ex;
		}
		if (last != null)
		{
			throw new InvalidOperationException("无法自动定位当前城市（" + Failure(last) + "），请检查网络或手动填写城市", last);
		}
		throw new InvalidOperationException("无法自动定位当前城市，请检查网络或手动填写城市");
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
		GeoPlace geoPlace = Resolve(city, geoReader);
		return FetchAt(geoPlace, forecastReader, backupReader);
	}

	// 直接按已解析的位置取天气（自动定位路径共用同一套主/备服务）
	public static WeatherSnapshot FetchAt(GeoPlace geoPlace)
	{
		return FetchAt(geoPlace, Get<WeatherReply>, Get<WttrReply>);
	}

	private static WeatherSnapshot FetchAt(GeoPlace geoPlace, Func<string, WeatherReply> forecastReader, Func<string, WttrReply> backupReader)
	{
		if (geoPlace == null || double.IsNaN(geoPlace.Latitude) || double.IsNaN(geoPlace.Longitude) || double.IsInfinity(geoPlace.Latitude) || double.IsInfinity(geoPlace.Longitude) || Math.Abs(geoPlace.Latitude) > 90.0 || Math.Abs(geoPlace.Longitude) > 180.0)
		{
			throw new ArgumentException("无效的天气位置");
		}
		string text = geoPlace.Latitude.ToString(CultureInfo.InvariantCulture);
		string text2 = geoPlace.Longitude.ToString(CultureInfo.InvariantCulture);
		Exception ex = null;
		try
		{
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
			return FromWttr(backupReader("https://wttr.in/" + Uri.EscapeDataString(text + "," + text2) + "?format=j1&lang=zh"));
		}
		catch (Exception ex3)
		{
			throw new InvalidOperationException("主服务：" + Failure(ex) + "；备用服务：" + Failure(ex3), ex3);
		}
	}
}
