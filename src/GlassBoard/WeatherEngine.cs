using System;
using System.Threading.Tasks;

namespace GlassBoard;

public sealed class WeatherEngine
{
	private readonly Controller C;

	private bool busy;

	private bool closed;

	private int serial;

	private int failures;

	private string city = "";

	private DateTime next = DateTime.MinValue;

	public WeatherSnapshot Snapshot;

	public string Message = "设置城市后获取天气";

	public event Action Changed;

	public WeatherEngine(Controller c)
	{
		C = c;
	}

	public void Update(bool force = false)
	{
		if (!closed && C.Prefs.Widgets.Weather)
		{
			string text = C.Prefs.Widgets.City;
			if (text != city)
			{
				city = text;
				Snapshot = null;
				failures = 0;
				next = DateTime.MinValue;
				serial++;
			}
			if (!busy && (force || DateTime.UtcNow >= next))
			{
				Fetch();
			}
		}
	}

	private async void Fetch()
	{
		if (busy || closed)
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(city))
		{
			Message = "在组件设置中填写天气城市";
			next = DateTime.UtcNow.AddMinutes(30.0);
			Notify();
			return;
		}
		int version = serial;
		string requested = city;
		busy = true;
		Message = "正在查询天气…";
		Notify();
		try
		{
			WeatherSnapshot weatherSnapshot = await Task.Run(() => WeatherClient.Fetch(requested));
			if (!closed && version == serial && !(requested != city))
			{
				Snapshot = weatherSnapshot;
				failures = 0;
				Message = weatherSnapshot.Provider + " · 更新于 " + DateTime.Now.ToString("HH:mm");
			}
		}
		catch (Exception ex)
		{
			if (!closed && version == serial)
			{
				failures++;
				Message = ((Snapshot == null) ? "" : "显示上次数据 · ") + ex.Message + " · 稍后自动重试，也可点击刷新";
				Store.Log(ex);
			}
		}
		finally
		{
			busy = false;
			next = DateTime.UtcNow.AddSeconds((failures == 0) ? 1800.0 : Math.Min(300.0, 30.0 * Math.Pow(2.0, Math.Min(4, failures - 1))));
			if (!closed)
			{
				Notify();
				if (requested != city)
				{
					Fetch();
				}
			}
		}
	}

	private void Notify()
	{
		if (!closed && Changed != null)
		{
			Changed();
		}
	}

	public void Stop()
	{
		closed = true;
		serial++;
		Changed = null;
	}
}
