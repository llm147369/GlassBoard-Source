using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class DockOptions
{
	[DataMember]
	public double IconSize = 44.0;

	[DataMember]
	public double Spacing = 6.0;

	[DataMember]
	public double Padding = 16.0;

	[DataMember]
	public double Radius = 28.0;

	[DataMember]
	public double OpacityPercent = 72.0;

	[DataMember]
	public double Zoom = 1.2;

	[DataMember]
	public double Offset;

	[DataMember]
	public double MotionLevel = 1.0;

	[DataMember]
	public int Alignment = 1;

	[DataMember]
	public int Accent;

	[DataMember]
	public int IconPack;

	[DataMember]
	public int MaxPinned = 9;

	[DataMember]
	public int ComputerStyle;

	[DataMember]
	public bool ShowComputer = false;

	// 收纳整合总开关：开启后此电脑/设置/电源/网络入口从 Dock 收起（收进应用面板与控制中心），
	// 关闭后各独立开关照常生效
	[DataMember]
	public bool Consolidate = true;

	[DataMember]
	public bool ShowLabels;

	[DataMember]
	public bool ShowSeparator = true;

	[DataMember]
	public bool AlwaysOnTop;

	[DataMember]
	public bool MotionEnabled = true;

	[DataMember]
	public bool AutoHide;

	[DataMember]
	public bool ShowRunning = true;

	[DataMember]
	public bool SpringReveal = true;

	[DataMember]
	public bool ShowPower;

	[DataMember]
	public bool ShowNetwork;

	[DataMember]
	public bool ShowControlCenter = true;

	[DataMember]
	public bool ShowRunningList = true;

	[DataMember]
	public bool ShowSettings = false;

	[DataMember]
	public List<string> ItemOrder = new List<string>();

	[DataMember]
	public List<string> RunningOrder = new List<string>();

	[DataMember]
	public bool ShowShortcutOverlays;

	[DataMember]
	public bool SharperIcons;

	[DataMember]
	public MaterialOptions Material = new MaterialOptions();

	[DataMember]
	public double HideDelay = 900.0;

	[DataMember]
	public double EdgeRange = 12.0;

	[DataMember]
	public double BlurRadius = 18.0;

	[DataMember]
	public double EdgeStrength = 1.0;

	[OnDeserializing]
	private void Defaults(StreamingContext context)
	{
		IconSize = 44.0;
		Spacing = 6.0;
		Padding = 16.0;
		Radius = 28.0;
		OpacityPercent = 72.0;
		Zoom = 1.2;
		Offset = 0.0;
		MotionLevel = 1.0;
		Alignment = 1;
		Accent = 0;
		MaxPinned = 9;
		ComputerStyle = 0;
		ShowComputer = false;
		Consolidate = true;
		ShowLabels = false;
		ShowSeparator = true;
		AlwaysOnTop = false;
		MotionEnabled = true;
		ShowSettings = false;
		ShowControlCenter = true;
		ShowRunningList = true;
		IconPack = 0;
		ItemOrder = new List<string>();
		RunningOrder = new List<string>();
		AutoHide = false;
		ShowRunning = true;
		SpringReveal = true;
		ShowShortcutOverlays = false;
		SharperIcons = false;
		HideDelay = 900.0;
		EdgeRange = 12.0;
		BlurRadius = 18.0;
		EdgeStrength = 1.0;
	}

	public DockOptions Copy()
	{
		DockOptions dockOptions = (DockOptions)MemberwiseClone();
		dockOptions.ItemOrder = (ItemOrder ?? new List<string>()).ToList();
		dockOptions.RunningOrder = (RunningOrder ?? new List<string>()).ToList();
		dockOptions.Material = (Material ?? new MaterialOptions()).Copy();
		return dockOptions;
	}

	public void Normalize()
	{
		ItemOrder = (ItemOrder ?? new List<string>()).Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(30)
			.ToList();
		RunningOrder = (RunningOrder ?? new List<string>()).Where((string x) => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(200)
			.ToList();
		if (Material == null)
		{
			Material = new MaterialOptions();
		}
		Material.Normalize();
		IconSize = LayoutRules.Clamp(IconSize, 32.0, 64.0, 44.0);
		Spacing = LayoutRules.Clamp(Spacing, 0.0, 20.0, 6.0);
		Padding = LayoutRules.Clamp(Padding, 8.0, 28.0, 16.0);
		Radius = LayoutRules.Clamp(Radius, 0.0, 48.0, 28.0);
		OpacityPercent = LayoutRules.GlassOpacity(OpacityPercent);
		Zoom = LayoutRules.Clamp(Zoom, 1.0, 1.6, 1.2);
		Offset = LayoutRules.Clamp(Offset, -500.0, 500.0, 0.0);
		MotionLevel = LayoutRules.Clamp(MotionLevel, 0.5, 1.5, 1.0);
		HideDelay = LayoutRules.Clamp(HideDelay, 200.0, 3000.0, 900.0);
		EdgeRange = LayoutRules.Clamp(EdgeRange, 3.0, 48.0, 12.0);
		BlurRadius = LayoutRules.Clamp(BlurRadius, 0.0, 40.0, 18.0);
		EdgeStrength = LayoutRules.Clamp(EdgeStrength, 0.2, 2.0, 1.0);
		Alignment = Math.Max(0, Math.Min(2, Alignment));
		Accent = Math.Max(0, Math.Min(5, Accent));
		IconPack = Math.Max(0, Math.Min(IconPacks.Names.Length - 1, IconPack));
		MaxPinned = Math.Max(3, Math.Min(20, MaxPinned));
		ComputerStyle = Math.Max(0, Math.Min(1, ComputerStyle));
	}

	public int SystemCount()
	{
		if (Consolidate)
		{
			// 收纳开启：Dock 上只剩应用面板 + 控制中心 + 运行列表
			return 1 + (ShowControlCenter ? 1 : 0) + (ShowRunningList ? 1 : 0);
		}
		return 1 + (ShowSettings ? 1 : 0) + (ShowComputer ? 1 : 0) + (ShowControlCenter ? 1 : 0) + (ShowPower ? 1 : 0) + (ShowNetwork ? 1 : 0) + (ShowRunningList ? 1 : 0);
	}

	public int VisiblePinned(double availableWidth, int count)
	{
		int num = SystemCount();
		double num2 = IconSize + 16.0 + Spacing;
		int val = Math.Max(0, (int)Math.Floor((availableWidth - 28.0 - Spacing - (double)(ShowSeparator ? 17 : 0)) / num2) - num);
		return Math.Min(count, Math.Min(MaxPinned, val));
	}

	public double HoverPadding()
	{
		// 每个按钮自带 Spacing/2 边距：两端合计 = Spacing，与图标间距完全一致
		return Spacing / 2.0;
	}

	public double WidthFor(int pinnedCount)
	{
		return 28.0 + Spacing + (double)(SystemCount() + pinnedCount) * (IconSize + 16.0 + Spacing) + (double)(ShowSeparator ? 17 : 0);
	}
}
