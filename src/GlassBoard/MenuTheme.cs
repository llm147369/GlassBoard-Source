using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace GlassBoard;

public sealed class MenuTheme
{
	private static readonly Dictionary<string, MenuTheme> themes = new Dictionary<string, MenuTheme>();

	private readonly Style menu;

	private readonly Style item;

	private readonly Style separator;

	private MenuTheme(bool light, int accent, string tint, int opacityPercent)
	{
		(Color bg, Color ink, Color line, Color hover) = Colors(light, accent, tint, opacityPercent);
		string background = Hex(bg);
		string inkHex = Hex(ink);
		string lineHex = Hex(line);
		string hoverHex = Hex(hover);
		string accentHex = Hex(IconPacks.AccentColor(accent));
		Func<string, string> func = (string x) => x.Replace("@BG", background).Replace("@INK", inkHex).Replace("@LINE", lineHex)
			.Replace("@HOVER", hoverHex).Replace("@ACCENT", accentHex);
		string arg = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='MenuItem'>\n<Grid>\n <Border x:Name='Chrome' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='{TemplateBinding Background}' CornerRadius='6' Padding='{TemplateBinding Padding}'>\n  <Grid>\n   <Grid.ColumnDefinitions><ColumnDefinition Width='22'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='16'/></Grid.ColumnDefinitions>\n   <ContentPresenter x:Name='Icon' ContentSource='Icon' VerticalAlignment='Center' HorizontalAlignment='Center'/>\n   <TextBlock x:Name='Check' Text='✓' Foreground='@ACCENT' Visibility='Collapsed' VerticalAlignment='Center'/>\n   <ContentPresenter Grid.Column='1' ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center' HorizontalAlignment='Left'/>\n   <TextBlock Grid.Column='2' Text='{TemplateBinding InputGestureText}' Foreground='{TemplateBinding Foreground}' Opacity='.7' Margin='18,0,8,0' VerticalAlignment='Center'/>\n   <Path x:Name='Arrow' Grid.Column='3' Data='M 0,0 L 4,4 L 0,8' Stroke='@ACCENT' StrokeThickness='1.6' Visibility='Collapsed' VerticalAlignment='Center' HorizontalAlignment='Right'/>\n  </Grid>\n </Border>\n <Popup x:Name='PART_Popup' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Placement='Right' HorizontalOffset='-1' AllowsTransparency='True' Focusable='False' IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>\n  <Border x:Name='SubmenuSurface' Background='@BG' BorderBrush='@LINE' BorderThickness='1' CornerRadius='10' Padding='5' TextElement.Foreground='@INK' SnapsToDevicePixels='True'>\n   <ScrollViewer CanContentScroll='True' MaxHeight='420' VerticalScrollBarVisibility='Hidden' HorizontalScrollBarVisibility='Disabled'>\n    <ItemsPresenter Grid.IsSharedSizeScope='True'/>\n   </ScrollViewer>\n  </Border>\n </Popup>\n</Grid>\n<ControlTemplate.Triggers>\n <Trigger Property='HasItems' Value='True'><Setter TargetName='Arrow' Property='Visibility' Value='Visible'/></Trigger>\n <Trigger Property='IsChecked' Value='True'><Setter TargetName='Check' Property='Visibility' Value='Visible'/><Setter TargetName='Icon' Property='Visibility' Value='Collapsed'/></Trigger>\n <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Chrome' Property='Background' Value='@HOVER'/></Trigger>\n <Trigger Property='IsSubmenuOpen' Value='True'><Setter TargetName='Chrome' Property='Background' Value='@HOVER'/></Trigger>\n <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Chrome' Property='Opacity' Value='.45'/></Trigger>\n</ControlTemplate.Triggers>\n</ControlTemplate>";
		item = new Style(typeof(MenuItem));
		item.Setters.Add(new Setter(Control.ForegroundProperty, Polish.Ink(light)));
		item.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		item.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8.0, 9.0, 8.0, 9.0)));
		item.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(func(arg))));
		separator = new Style(typeof(Separator));
		separator.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(func("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Separator'><Border Height='1' Background='@LINE' Margin='8,5'/></ControlTemplate>"))));
		menu = new Style(typeof(ContextMenu));
		menu.Setters.Add(new Setter(Control.ForegroundProperty, Polish.Ink(light)));
		menu.Setters.Add(new Setter(Control.BackgroundProperty, Polish.Color(background)));
		menu.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
		menu.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(func("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContextMenu'><Border Background='{TemplateBinding Background}' BorderBrush='@LINE' BorderThickness='1' CornerRadius='10' Padding='5' SnapsToDevicePixels='True'><ScrollViewer CanContentScroll='True' MaxHeight='420' VerticalScrollBarVisibility='Hidden' HorizontalScrollBarVisibility='Disabled'><ItemsPresenter Grid.IsSharedSizeScope='True'/></ScrollViewer></Border></ControlTemplate>"))));
		item.Seal();
		separator.Seal();
		menu.Seal();
	}

	private static MenuTheme For(bool light, int accent, string tint, int opacityPercent)
	{
		string key = (light ? "1" : "0") + "|" + accent + "|" + (tint ?? "") + "|" + opacityPercent;
		if (!themes.TryGetValue(key, out var value))
		{
			value = new MenuTheme(light, accent, tint, opacityPercent);
			themes[key] = value;
		}
		return value;
	}

	public static void Install(Window window, bool light, int accent = 0, string tint = "", int opacityPercent = 100)
	{
		MenuTheme menuTheme = For(light, accent, tint, opacityPercent);
		window.Resources[typeof(ContextMenu)] = menuTheme.menu;
		window.Resources[typeof(MenuItem)] = menuTheme.item;
		window.Resources[typeof(Separator)] = menuTheme.separator;
	}

	public static ContextMenu Create(bool light, int accent = 0, string tint = "", int opacityPercent = 100)
	{
		MenuTheme menuTheme = For(light, accent, tint, opacityPercent);
		ContextMenu contextMenu = new ContextMenu
		{
			Style = menuTheme.menu,
			Resources = 
			{
				[(object)typeof(MenuItem)] = menuTheme.item,
				[(object)typeof(Separator)] = menuTheme.separator
			}
		};
		MenuAutoDismiss.Track(contextMenu);
		return contextMenu;
	}

	// 主题切换时刷新已存在的菜单（Dock 背景右键菜单只创建一次，需用此方法重刷样式）
	public static void Apply(ContextMenu contextMenu, bool light, int accent = 0, string tint = "", int opacityPercent = 100)
	{
		if (contextMenu == null)
		{
			return;
		}
		MenuTheme menuTheme = For(light, accent, tint, opacityPercent);
		contextMenu.Style = menuTheme.menu;
		contextMenu.Resources[(object)typeof(MenuItem)] = menuTheme.item;
		contextMenu.Resources[(object)typeof(Separator)] = menuTheme.separator;
	}

	// 供 Dock 悬浮预览等复用同一套菜单配色（跟随浅色玻璃开关、强调色、所在窗口材质色调与透明度）
	public static (Color bg, Color ink, Color line, Color hover) Palette(bool light, int accent = 0, string tint = "", int opacityPercent = 100)
	{
		return Colors(light, accent, tint, opacityPercent);
	}

	// 菜单配色：底色直接采用所在窗口玻璃的色调（与 GlassMaterials.Fill 同源），
	// 透明度跟随该窗口的玻璃透明度，再掺入强调色；选中勾与子菜单箭头直接用强调色，
	// 悬停保留强调色作为交互反馈（文字保持不透明以保证可读性）
	private static (Color bg, Color ink, Color line, Color hover) Colors(bool light, int accent, string tint, int opacityPercent)
	{
		Color accentColor = IconPacks.AccentColor(accent);
		Color background = (light ? Hex("#FAFCFF") : Hex("#202B3D"));
		if (!string.IsNullOrWhiteSpace(tint) && TryColor(tint, out Color tintColor))
		{
			background = tintColor;
		}
		byte alpha = (byte)Math.Round(Math.Max(25.0, Math.Min(100.0, opacityPercent)) * 255.0 / 100.0);
		background.A = alpha;
		Color ink = (light ? Hex("#202635") : Hex("#F4F6FC"));
		Color line = (light ? Hex("#CFD7E5") : Hex("#48566D"));
		line.A = alpha;
		return (Blend(background, accentColor, 0.10), ink, Blend(line, accentColor, 0.45), Blend(background, accentColor, 0.45));
	}

	private static bool TryColor(string value, out Color color)
	{
		try
		{
			color = (Color)ColorConverter.ConvertFromString(value);
			return true;
		}
		catch
		{
			color = default(Color);
			return false;
		}
	}

	private static Color Blend(Color background, Color accentColor, double amount)
	{
		// 只掺 RGB、保留背景的 alpha，保证菜单透明度不被掺混破坏
		byte alpha = background.A;
		return Color.FromArgb(alpha, (byte)((double)background.R + ((double)(int)accentColor.R - (double)background.R) * amount), (byte)((double)background.G + ((double)(int)accentColor.G - (double)background.G) * amount), (byte)((double)background.B + ((double)(int)accentColor.B - (double)background.B) * amount));
	}

	private static string Hex(Color color)
	{
		return "#" + color.A.ToString("X2") + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
	}

	private static Color Hex(string value)
	{
		return (Color)ColorConverter.ConvertFromString(value);
	}
}
