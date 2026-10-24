using System;

namespace GlassBoard;

// Dock 系统图标可更换的"内置图标样式"（复用内置图标包的 9 种几何）
public static class SystemIconActions
{
	public static readonly string[] GlyphKinds = new string[10] { "apps", "computer", "settings", "power", "wifi", "control", "volume", "brightness", "bluetooth", "search" };

	public static readonly string[] GlyphNames = new string[10] { "应用", "此电脑", "设置", "电源", "Wi-Fi", "控制", "音量", "亮度", "蓝牙", "搜索" };

	public static string GlyphKind(string glyph)
	{
		if (string.IsNullOrWhiteSpace(glyph))
		{
			return "apps";
		}
		int num = Array.IndexOf(GlyphKinds, glyph);
		if (num < 0)
		{
			return "apps";
		}
		return GlyphKinds[num];
	}

	public static string GlyphDisplay(string glyph)
	{
		int num = Array.IndexOf(GlyphKinds, GlyphKind(glyph));
		return GlyphNames[num];
	}

	public static bool IsValid(string glyph)
	{
		return glyph != null && Array.IndexOf(GlyphKinds, glyph) >= 0;
	}
}
