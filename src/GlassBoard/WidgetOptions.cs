using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class WidgetOptions
{
	[DataMember]
	public bool Clock;

	[DataMember]
	public bool Weather;

	[DataMember]
	public bool Music;

	[DataMember]
	public bool Notes;

	[DataMember]
	public bool Visualizer;

	[DataMember]
	public bool Battery;

	[DataMember]
	public int ClockPanel;

	[DataMember]
	public int WeatherPanel;

	[DataMember]
	public int MusicPanel;

	[DataMember]
	public int NotesPanel;

	[DataMember]
	public int VisualizerPanel;

	[DataMember]
	public int BatteryPanel;

	// 律动样式：0 极光镜像柱 1 频谱曲线 2 双螺旋 3 粒子喷泉 4 音轨条带 5 径向频谱
	[DataMember]
	public int VisualizerStyle;

	// 律动配色：0 极光蓝紫 1 青绿 2 粉红 3 橙金 4 紫红 5 绿 6 红 7 白 8 彩虹 9 跟随强调色
	[DataMember]
	public int VisualizerColor;

	// 律动组件位置锚点：0 顶部 1 左 2 右 3 下方（Dock 栏上方，默认）。律动组件为独立无框装饰，
	// 不参与拖动，位置由此锚点 + 偏移决定。
	[DataMember]
	public int VisualizerAnchor = 3;

	// 律动组件锚点偏移（物理像素语义）：正=右/下，负=左/上。
	[DataMember]
	public double VisualizerOffsetX;

	[DataMember]
	public double VisualizerOffsetY;

	[DataMember]
	public bool CompactMusic;

	[DataMember]
	public bool Topmost;

	[DataMember]
	public bool Locked;

	[DataMember]
	public string MusicMode = "system";

	[DataMember]
	public List<string> Playlist = new List<string>();

	[DataMember]
	public int TrackIndex;

	[DataMember]
	public int RepeatMode;

	[DataMember]
	public bool Shuffle;

	[DataMember]
	public bool NativeMediaCompatibility;

	[DataMember]
	public bool AutoNativeMediaCompatibility = true;

	[DataMember]
	public List<WidgetPanelPosition> Panels = new List<WidgetPanelPosition>();

	[DataMember]
	public List<ClockAlarm> Alarms = new List<ClockAlarm>();

	[DataMember]
	public DateTime? TimerDueUtc;

	[DataMember]
	public double TimerPausedSeconds;

	[DataMember]
	public string City = "";

	[DataMember]
	public string TimeZoneId = "";

	[DataMember]
	public double Volume = 0.6;

	[DataMember]
	public double OpacityPercent = 72.0;

	[DataMember]
	public bool HideControls;

	[DataMember]
	public MaterialOptions Material = new MaterialOptions();

	[DataMember]
	public List<WidgetStyle> Styles = new List<WidgetStyle>();

	[OnDeserializing]
	private void Defaults(StreamingContext context)
	{
		AutoNativeMediaCompatibility = true;
		City = "";
		TimeZoneId = "";
		Volume = 0.6;
		OpacityPercent = 72.0;
		MusicMode = "system";
		Playlist = new List<string>();
		Panels = new List<WidgetPanelPosition>();
		VisualizerAnchor = 3;
		VisualizerOffsetX = 0.0;
		VisualizerOffsetY = 0.0;
	}

	public void Normalize()
	{
		NormalizeStyles();
		if (Material == null)
		{
			Material = new MaterialOptions();
		}
		Material.Normalize();
		Alarms = (Alarms ?? new List<ClockAlarm>()).Where((ClockAlarm a) => a != null).Take(50).ToList();
		foreach (ClockAlarm alarm in Alarms)
		{
			alarm.Normalize();
		}
		TimerPausedSeconds = LayoutRules.Clamp(TimerPausedSeconds, 0.0, 86400.0, 0.0);
		City = (City ?? "").Trim();
		if (City.Length > 100)
		{
			City = City.Substring(0, 100);
		}
		TimeZoneId = (TimeZones.Valid(TimeZoneId) ? (TimeZoneId ?? "") : "");
		Volume = LayoutRules.Clamp(Volume, 0.0, 1.0, 0.6);
		OpacityPercent = LayoutRules.Clamp(OpacityPercent, 0.0, 100.0, 72.0);
		ClockPanel = Panel(ClockPanel);
		WeatherPanel = Panel(WeatherPanel);
		MusicPanel = Panel(MusicPanel);
		NotesPanel = Panel(NotesPanel);
		BatteryPanel = Panel(BatteryPanel);
		VisualizerStyle = Math.Max(0, Math.Min(5, VisualizerStyle));
		VisualizerColor = Math.Max(0, Math.Min(9, VisualizerColor));
		VisualizerAnchor = Math.Max(0, Math.Min(3, VisualizerAnchor));
		VisualizerOffsetX = LayoutRules.Clamp(VisualizerOffsetX, -4000.0, 4000.0, 0.0);
		VisualizerOffsetY = LayoutRules.Clamp(VisualizerOffsetY, -4000.0, 4000.0, 0.0);
		MusicMode = ((MusicMode == "local") ? "local" : "system");
		Playlist = (Playlist ?? new List<string>()).Where((string p) => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		TrackIndex = ((Playlist.Count != 0) ? Math.Max(0, Math.Min(Playlist.Count - 1, TrackIndex)) : 0);
		RepeatMode = Math.Max(0, Math.Min(2, RepeatMode));
		Panels = (from p in Panels ?? new List<WidgetPanelPosition>()
			where p != null && p.Id >= 0 && p.Id <= 4
			group p by p.Id into g
			select g.First()).ToList();
		foreach (WidgetPanelPosition panel in Panels)
		{
			panel.Normalize();
		}
	}

	private static int Panel(int value)
	{
		return Math.Max(0, Math.Min(3, value));
	}

	public WidgetOptions Copy()
	{
		WidgetOptions widgetOptions = (WidgetOptions)MemberwiseClone();
		widgetOptions.Styles = (Styles ?? new List<WidgetStyle>()).Select((WidgetStyle x) => x.Copy()).ToList();
		widgetOptions.Material = (Material ?? new MaterialOptions()).Copy();
		widgetOptions.Alarms = (Alarms ?? new List<ClockAlarm>()).Select((ClockAlarm a) => a.Copy()).ToList();
		widgetOptions.Playlist = new List<string>(Playlist ?? new List<string>());
		widgetOptions.Panels = (from p in Panels ?? new List<WidgetPanelPosition>()
			where p != null
			select p.Copy()).ToList();
		return widgetOptions;
	}

	public WidgetPanelPosition Position(int id)
	{
		WidgetPanelPosition widgetPanelPosition = Panels.FirstOrDefault((WidgetPanelPosition x) => x.Id == id);
		if (widgetPanelPosition == null)
		{
			widgetPanelPosition = new WidgetPanelPosition
			{
				Id = id
			};
			Panels.Add(widgetPanelPosition);
		}
		return widgetPanelPosition;
	}

	public void Separate()
	{
		ClockPanel = 0;
		WeatherPanel = 1;
		MusicPanel = 2;
		NotesPanel = 3;
		BatteryPanel = 0;
	}

	public void Fuse()
	{
		ClockPanel = (WeatherPanel = (MusicPanel = (NotesPanel = (BatteryPanel = 0))));
	}

	public void NormalizeStyles()
	{
		Styles = (from x in Styles ?? new List<WidgetStyle>()
			where x != null && WidgetRules.Kinds.Contains(x.Kind)
			group x by x.Kind into g
			select g.First()).ToList();
		foreach (WidgetStyle style in Styles)
		{
			style.Normalize();
			if (style.Kind == "visualizer")
			{
				style.Frameless = true;
			}
		}
	}

	public WidgetStyle Style(string kind)
	{
		if (Styles == null)
		{
			Styles = new List<WidgetStyle>();
		}
		WidgetStyle widgetStyle = Styles.FirstOrDefault((WidgetStyle x) => x.Kind == kind);
		if (widgetStyle == null)
		{
			widgetStyle = new WidgetStyle
			{
				Kind = kind
			};
			Styles.Add(widgetStyle);
		}
		if (kind == "visualizer")
		{
			widgetStyle.Frameless = true;
		}
		return widgetStyle;
	}
}
