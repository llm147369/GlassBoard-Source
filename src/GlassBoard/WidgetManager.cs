using System;
using System.Collections.Generic;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Threading;

namespace GlassBoard;

public sealed class WidgetManager : IDisposable
{
	public readonly Controller C;

	public readonly NotesRepository Notes = new NotesRepository();

	public readonly WeatherEngine Weather;

	public MusicEngine Music;

	private readonly WidgetRegistry<WidgetWindow> windows = new WidgetRegistry<WidgetWindow>();

	private readonly DispatcherTimer timer = new DispatcherTimer
	{
		Interval = TimeSpan.FromSeconds(1.0)
	};

	private bool closed;

	private bool syncing;

	private bool pending;

	private bool recreate;

	private bool queued;

	private DateTime lastWeatherCheck = DateTime.MinValue;

	public string LastError = "";

	private readonly List<Window> reminderWindows = new List<Window>();

	internal List<WidgetWindow> SnapshotWindows => windows.Values.ToList();

	public bool MusicVisible => windows.Values.Any((WidgetWindow w) => w.IsVisible && w.Kinds.Contains("music"));

	public WidgetManager(Controller c)
	{
		C = c;
		Weather = new WeatherEngine(c);
		Weather.Changed += () =>
		{
			foreach (WidgetWindow value in windows.Values)
			{
				if (value.IsVisible)
				{
					value.UpdateWeather();
				}
			}
		};
		timer.Tick += (object s, EventArgs e) =>
		{
			if (closed)
			{
				return;
			}
			if ((DateTime.UtcNow - lastWeatherCheck).TotalSeconds >= 30.0)
			{
				lastWeatherCheck = DateTime.UtcNow;
				Weather.Update();
			}
			List<string> list = ClockRules.Check(C.Prefs.Widgets, DateTime.Now, DateTime.UtcNow);
			if (list.Count > 0)
			{
				C.Save();
				ShowReminder(list);
			}
			foreach (WidgetWindow value2 in windows.Values)
			{
				if (value2.IsVisible)
				{
					value2.Tick();
				}
			}
		};
		timer.Start();
	}

	private void ShowReminder(List<string> messages)
	{
		Window w = FeatureUi.Dialog("时间到了", null, 380.0, 230.0);
		w.Topmost = true;
		w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(24.0)
		};
		w.Content = stackPanel;
		stackPanel.Children.Add(new TextBlock
		{
			Text = string.Join("\n", messages),
			FontSize = 20.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 0.0, 20.0)
		});
		WrapPanel wrapPanel = new WrapPanel();
		stackPanel.Children.Add(wrapPanel);
		wrapPanel.Children.Add(FeatureUi.Button("5 分钟后提醒", () =>
		{
			DateTime dueLocal = DateTime.Now.AddMinutes(5.0);
			C.Prefs.Widgets.Alarms.Add(new ClockAlarm
			{
				Label = string.Join(" · ", messages),
				DueLocal = dueLocal,
				Hour = dueLocal.Hour,
				Minute = dueLocal.Minute,
				Enabled = true
			});
			C.Save();
			w.Close();
		}));
		wrapPanel.Children.Add(FeatureUi.Button("知道了", () =>
		{
			w.Close();
		}));
		DispatcherTimer sound = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(2.0)
		};
		DateTime until = DateTime.UtcNow.AddSeconds(30.0);
		sound.Tick += (object s, EventArgs e) =>
		{
			if (DateTime.UtcNow >= until)
			{
				sound.Stop();
			}
			else
			{
				SystemSounds.Exclamation.Play();
			}
		};
		w.Closed += (object s, EventArgs e) =>
		{
			sound.Stop();
			reminderWindows.Remove(w);
		};
		reminderWindows.Add(w);
		w.Show();
		SystemSounds.Exclamation.Play();
		sound.Start();
		if (C.Tray != null)
		{
			C.Tray.ShowBalloonTip(5000, "GlassBoard 时间提醒", string.Join(" · ", messages), ToolTipIcon.Info);
		}
	}

	public void QueueSync()
	{
		if (closed || queued)
		{
			return;
		}
		queued = true;
		System.Windows.Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
		{
			queued = false;
			if (closed)
			{
				return;
			}
			try
			{
				Sync();
			}
			catch (Exception ex)
			{
				Store.Log(ex);
				LastError = "组件刷新失败：" + ex.Message;
				Store.Trace("WIDGET QUEUED FAILED " + ex);
			}
		}));
	}

	public void SetVisible(bool visible)
	{
		foreach (WidgetWindow value in windows.Values)
		{
			if (visible && !C.GlassSuppressed)
			{
				value.Show();
				value.Tick();
				value.UpdateMusic();
				value.UpdateWeather();
			}
			else
			{
				value.Hide();
			}
		}
		if (visible && Music != null)
		{
			Music.Wake();
		}
	}

	public void Reopen()
	{
		recreate = true;
		Sync();
	}

	public void Sync()
	{
		if (closed)
		{
			return;
		}
		if (syncing)
		{
			pending = true;
			return;
		}
		syncing = true;
		try
		{
			do
			{
				pending = false;
				Reconcile();
			}
			while (pending && !closed);
		}
		finally
		{
			syncing = false;
		}
	}

	private void Reconcile()
	{
		WidgetOptions o = C.Prefs.Widgets;
		o.Normalize();
		Dictionary<int, List<string>> dictionary = WidgetRules.Layout(o);
		LastError = "";
		Store.Trace("WIDGET SYNC enabled=" + string.Join(",", WidgetRules.Kinds.Where((string k) => WidgetRules.Enabled(o, k))) + " panels=" + dictionary.Count);
		Notes.Flush();
		bool flag = recreate;
		recreate = false;
		foreach (int key in windows.Keys)
		{
			if (windows.TryGetValue(key, out var value) && (flag || value.IsStopped || !dictionary.TryGetValue(key, out var value2) || !value.Kinds.SequenceEqual(value2)))
			{
				windows.Remove(key, value);
				TryStop(value);
			}
		}
		if (!o.Music && Music != null)
		{
			MusicEngine music = Music;
			Music = null;
			try
			{
				music.Dispose();
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
		}
		if (o.Music && Music == null)
		{
			Music = new MusicEngine(C);
			Music.Changed += RefreshMusic;
		}
		foreach (KeyValuePair<int, List<string>> item in dictionary)
		{
			if (!windows.TryGetValue(item.Key, out var value3))
			{
				value3 = new WidgetWindow(this, item.Key);
				windows.Add(item.Key, value3);
			}
			try
			{
				value3.UpdateOptions(item.Value);
				if (C.GlassSuppressed)
				{
					value3.Hide();
				}
				else if (!value3.IsVisible)
				{
					value3.Show();
				}
				Store.Trace("WIDGET SHOW panel=" + item.Key + " kinds=" + string.Join(",", item.Value) + " visible=" + value3.IsVisible + " loaded=" + value3.IsLoaded + " rect=" + value3.Left + "," + value3.Top + "," + value3.Width + "," + value3.Height);
			}
			catch (Exception ex2)
			{
				windows.Remove(item.Key, value3);
				TryStop(value3);
				Store.Log(ex2);
				Store.Trace("WIDGET FAILED panel=" + item.Key + " error=" + ex2);
				LastError = "面板 " + (char)(65 + item.Key) + " 打开失败：" + ex2.Message;
			}
		}
		timer.Interval = TimeSpan.FromSeconds((dictionary.Count != 0 || o.Clock || !o.Alarms.All((ClockAlarm a) => !a.Enabled) || o.TimerDueUtc.HasValue) ? 1 : 30);
		if (Music != null)
		{
			try
			{
				Music.Update();
				Music.Start();
			}
			catch (Exception ex3)
			{
				Store.Log(ex3);
				LastError = "音乐初始化失败：" + ex3.Message;
			}
		}
		Weather.Update();
	}

	private void TryStop(WidgetWindow w)
	{
		try
		{
			w.Stop();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	private void RefreshMusic()
	{
		foreach (WidgetWindow item in windows.Values.ToList())
		{
			if (item.IsVisible)
			{
				item.UpdateMusic();
			}
		}
	}

	public void RefreshNotes()
	{
		foreach (WidgetWindow item in windows.Values.ToList())
		{
			item.ReloadNotes();
		}
	}

	public void Assign(string kind, int panel)
	{
		Notes.Flush();
		WidgetRules.Assign(C.Prefs.Widgets, kind, panel);
		C.Save();
		Sync();
	}

	public void Enable(string kind, bool value)
	{
		WidgetRules.Enable(C.Prefs.Widgets, kind, value);
		Notes.Flush();
		C.Save();
		Store.Trace("WIDGET COMMAND kind=" + kind + " enabled=" + value);
		QueueSync();
	}

	public void Closed(WidgetWindow window, bool disable)
	{
		if (!windows.Remove(window.PanelId, window) || !(!closed & disable))
		{
			return;
		}
		foreach (string kind in window.Kinds)
		{
			WidgetRules.Enable(C.Prefs.Widgets, kind, value: false);
		}
		C.Save();
		Store.Trace("WIDGET CLOSED panel=" + window.PanelId);
		QueueSync();
	}

	public void Dispose()
	{
		closed = true;
		timer.Stop();
		foreach (Window item in reminderWindows.ToList())
		{
			item.Close();
		}
		foreach (WidgetWindow value in windows.Values)
		{
			windows.Remove(value.PanelId, value);
			TryStop(value);
		}
		try
		{
			Notes.Dispose();
		}
		finally
		{
			Weather.Stop();
			if (Music != null)
			{
				MusicEngine music = Music;
				Music = null;
				music.Dispose();
			}
		}
	}
}
