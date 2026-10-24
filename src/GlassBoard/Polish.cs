using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GlassBoard;

public static class Polish
{
	private static readonly BoundedCache<string, Brush> brushes = new BoundedCache<string, Brush>(96, StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<bool, Dictionary<object, Style>> themes = new Dictionary<bool, Dictionary<object, Style>>();

	private static readonly FontFamily uiFont = new FontFamily("Segoe UI, Microsoft YaHei UI");

	public static Brush Accent => Color("#6A9BFA");

	public static Brush Color(string hex)
	{
		if (brushes.TryGet(hex, out var value))
		{
			return value;
		}
		SolidColorBrush solidColorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
		solidColorBrush.Freeze();
		brushes.Put(hex, solidColorBrush);
		return solidColorBrush;
	}

	public static Brush Ink(bool light)
	{
		return Color(light ? "#202635" : "#F4F6FC");
	}

	public static Brush Muted(bool light)
	{
		return Color(light ? "#697385" : "#9CA9BF");
	}

	public static Brush Card(bool light)
	{
		return Color(light ? "#A8FFFFFF" : "#70263043");
	}

	public static Brush Line(bool light)
	{
		return Color(light ? "#2A7687A2" : "#24D5E3FF");
	}

	private static Binding Parent(string path)
	{
		return new Binding(path)
		{
			RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
		};
	}

	private static FrameworkElementFactory Frame(double radius)
	{
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(Border), "Chrome");
		frameworkElementFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
		frameworkElementFactory.SetBinding(Border.BackgroundProperty, Parent("Background"));
		frameworkElementFactory.SetBinding(Border.BorderBrushProperty, Parent("BorderBrush"));
		frameworkElementFactory.SetBinding(Border.BorderThicknessProperty, Parent("BorderThickness"));
		return frameworkElementFactory;
	}

	private static FrameworkElementFactory Presenter()
	{
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(ContentPresenter));
		frameworkElementFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		frameworkElementFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		frameworkElementFactory.SetBinding(FrameworkElement.MarginProperty, Parent("Padding"));
		return frameworkElementFactory;
	}

	public static Style ButtonStyle(bool light)
	{
		Style style = new Style(typeof(Button))
		{
			Setters = 
			{
				(SetterBase)new Setter(Control.ForegroundProperty, Ink(light)),
				(SetterBase)new Setter(Control.BackgroundProperty, Card(light)),
				(SetterBase)new Setter(Control.BorderBrushProperty, Line(light)),
				(SetterBase)new Setter(Control.BorderThicknessProperty, new Thickness(1.0)),
				(SetterBase)new Setter(Control.PaddingProperty, new Thickness(13.0, 8.0, 13.0, 8.0)),
				(SetterBase)new Setter(Control.FontSizeProperty, 13.0),
				(SetterBase)new Setter(Control.FontWeightProperty, FontWeights.Medium),
				(SetterBase)new Setter(FrameworkElement.CursorProperty, Cursors.Hand)
			}
		};
		FrameworkElementFactory frameworkElementFactory = Frame(11.0);
		frameworkElementFactory.AppendChild(Presenter());
		ControlTemplate controlTemplate = new ControlTemplate(typeof(Button))
		{
			VisualTree = frameworkElementFactory
		};
		Trigger trigger = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		trigger.Setters.Add(new Setter(Border.BackgroundProperty, Color(light ? "#ECF2FF" : "#405478"), "Chrome"));
		controlTemplate.Triggers.Add(trigger);
		Trigger trigger2 = new Trigger
		{
			Property = ButtonBase.IsPressedProperty,
			Value = true
		};
		trigger2.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65));
		controlTemplate.Triggers.Add(trigger2);
		Trigger trigger3 = new Trigger
		{
			Property = UIElement.IsEnabledProperty,
			Value = false
		};
		trigger3.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
		controlTemplate.Triggers.Add(trigger3);
		Trigger trigger4 = new Trigger
		{
			Property = UIElement.IsKeyboardFocusedProperty,
			Value = true
		};
		trigger4.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Chrome"));
		controlTemplate.Triggers.Add(trigger4);
		style.Setters.Add(new Setter(Control.TemplateProperty, controlTemplate));
		return style;
	}

	public static void Apply(Window w, bool light)
	{
		w.UseLayoutRounding = true;
		w.SnapsToDevicePixels = true;
		w.FontFamily = uiFont;
		w.Foreground = Ink(light);
		w.FontSize = 13.0;
		if (themes.TryGetValue(light, out var value))
		{
			foreach (KeyValuePair<object, Style> item in value)
			{
				if (w.Resources[item.Key] != item.Value)
				{
					w.Resources[item.Key] = item.Value;
				}
			}
			return;
		}
		w.Resources[typeof(Button)] = ButtonStyle(light);
		MenuTheme.Install(w, light);
		Style style = new Style(typeof(TextBox));
		style.Setters.Add(new Setter(Control.BackgroundProperty, Color(light ? "#DFFFFFFF" : "#90151E2D")));
		style.Setters.Add(new Setter(Control.ForegroundProperty, Ink(light)));
		style.Setters.Add(new Setter(Control.BorderBrushProperty, Line(light)));
		style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1.0)));
		style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10.0, 3.0, 10.0, 3.0)));
		style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 36.0));
		style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
		style.Setters.Add(new Setter(TextBoxBase.CaretBrushProperty, Accent));
		style.Setters.Add(new Setter(TextBoxBase.SelectionBrushProperty, Accent));
		FrameworkElementFactory frameworkElementFactory = Frame(10.0);
		FrameworkElementFactory frameworkElementFactory2 = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
		frameworkElementFactory2.SetBinding(Control.VerticalContentAlignmentProperty, Parent("VerticalContentAlignment"));
		frameworkElementFactory2.SetBinding(ScrollViewer.HorizontalScrollBarVisibilityProperty, Parent("HorizontalScrollBarVisibility"));
		frameworkElementFactory2.SetBinding(ScrollViewer.VerticalScrollBarVisibilityProperty, Parent("VerticalScrollBarVisibility"));
		frameworkElementFactory.AppendChild(frameworkElementFactory2);
		ControlTemplate controlTemplate = new ControlTemplate(typeof(TextBox))
		{
			VisualTree = frameworkElementFactory
		};
		Trigger trigger = new Trigger
		{
			Property = UIElement.IsKeyboardFocusWithinProperty,
			Value = true
		};
		trigger.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Chrome"));
		controlTemplate.Triggers.Add(trigger);
		style.Setters.Add(new Setter(Control.TemplateProperty, controlTemplate));
		w.Resources[typeof(TextBox)] = style;
		Style style2 = new Style(typeof(ComboBox));
		style2.Setters.Add(new Setter(Control.ForegroundProperty, Ink(light)));
		style2.Setters.Add(new Setter(Control.BackgroundProperty, Card(light)));
		style2.Setters.Add(new Setter(Control.BorderBrushProperty, Line(light)));
		style2.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1.0)));
		style2.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12.0, 6.0, 30.0, 6.0)));
		style2.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 32.0));
		FrameworkElementFactory frameworkElementFactory3 = new FrameworkElementFactory(typeof(Grid));
		FrameworkElementFactory frameworkElementFactory4 = new FrameworkElementFactory(typeof(ToggleButton));
		frameworkElementFactory4.SetValue(UIElement.FocusableProperty, false);
		frameworkElementFactory4.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
		{
			RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
			Mode = BindingMode.TwoWay
		});
		FrameworkElementFactory frameworkElementFactory5 = Frame(10.0);
		frameworkElementFactory5.SetValue(Border.BackgroundProperty, Card(light));
		frameworkElementFactory5.SetValue(Border.BorderBrushProperty, Line(light));
		frameworkElementFactory5.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
		ControlTemplate value2 = new ControlTemplate(typeof(ToggleButton))
		{
			VisualTree = frameworkElementFactory5
		};
		frameworkElementFactory4.SetValue(Control.TemplateProperty, value2);
		frameworkElementFactory3.AppendChild(frameworkElementFactory4);
		FrameworkElementFactory frameworkElementFactory6 = new FrameworkElementFactory(typeof(ContentPresenter));
		frameworkElementFactory6.SetBinding(ContentPresenter.ContentProperty, Parent("SelectionBoxItem"));
		frameworkElementFactory6.SetBinding(ContentPresenter.ContentTemplateProperty, Parent("SelectionBoxItemTemplate"));
		frameworkElementFactory6.SetValue(FrameworkElement.MarginProperty, new Thickness(12.0, 4.0, 30.0, 4.0));
		frameworkElementFactory6.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		frameworkElementFactory6.SetValue(UIElement.IsHitTestVisibleProperty, false);
		frameworkElementFactory3.AppendChild(frameworkElementFactory6);
		FrameworkElementFactory frameworkElementFactory7 = new FrameworkElementFactory(typeof(TextBlock));
		frameworkElementFactory7.SetValue(TextBlock.TextProperty, "⌄");
		frameworkElementFactory7.SetValue(TextBlock.ForegroundProperty, Muted(light));
		frameworkElementFactory7.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
		frameworkElementFactory7.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		frameworkElementFactory7.SetValue(FrameworkElement.MarginProperty, new Thickness(0.0, 0.0, 12.0, 4.0));
		frameworkElementFactory7.SetValue(UIElement.IsHitTestVisibleProperty, false);
		frameworkElementFactory3.AppendChild(frameworkElementFactory7);
		FrameworkElementFactory frameworkElementFactory8 = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
		frameworkElementFactory8.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
		frameworkElementFactory8.SetValue(Popup.AllowsTransparencyProperty, true);
		frameworkElementFactory8.SetBinding(Popup.IsOpenProperty, Parent("IsDropDownOpen"));
		FrameworkElementFactory frameworkElementFactory9 = new FrameworkElementFactory(typeof(Border));
		frameworkElementFactory9.SetValue(Border.CornerRadiusProperty, new CornerRadius(12.0));
		frameworkElementFactory9.SetValue(Border.BackgroundProperty, Color(light ? "#F8FAFF" : "#202A3C"));
		frameworkElementFactory9.SetValue(Border.BorderBrushProperty, Line(light));
		frameworkElementFactory9.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
		frameworkElementFactory9.SetValue(FrameworkElement.MarginProperty, new Thickness(0.0, 5.0, 0.0, 4.0));
		frameworkElementFactory9.SetValue(FrameworkElement.MinWidthProperty, 130.0);
		frameworkElementFactory9.SetBinding(FrameworkElement.WidthProperty, Parent("ActualWidth"));
		FrameworkElementFactory frameworkElementFactory10 = new FrameworkElementFactory(typeof(ScrollViewer));
		frameworkElementFactory10.SetValue(FrameworkElement.MaxHeightProperty, 260.0);
		frameworkElementFactory10.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
		FrameworkElementFactory child = new FrameworkElementFactory(typeof(ItemsPresenter));
		frameworkElementFactory10.AppendChild(child);
		frameworkElementFactory9.AppendChild(frameworkElementFactory10);
		frameworkElementFactory8.AppendChild(frameworkElementFactory9);
		frameworkElementFactory3.AppendChild(frameworkElementFactory8);
		style2.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ComboBox))
		{
			VisualTree = frameworkElementFactory3
		}));
		w.Resources[typeof(ComboBox)] = style2;
		Style style3 = new Style(typeof(ComboBoxItem));
		style3.Setters.Add(new Setter(Control.ForegroundProperty, Ink(light)));
		style3.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12.0, 9.0, 12.0, 9.0)));
		style3.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
		style3.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		FrameworkElementFactory frameworkElementFactory11 = Frame(8.0);
		frameworkElementFactory11.AppendChild(Presenter());
		style3.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ComboBoxItem))
		{
			VisualTree = frameworkElementFactory11
		}));
		Trigger trigger2 = new Trigger
		{
			Property = ComboBoxItem.IsHighlightedProperty,
			Value = true
		};
		trigger2.Setters.Add(new Setter(Control.BackgroundProperty, Color(light ? "#DCE8FF" : "#3F547B")));
		style3.Triggers.Add(trigger2);
		w.Resources[typeof(ComboBoxItem)] = style3;
		Style style4 = new Style(typeof(ListBox));
		style4.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		style4.Setters.Add(new Setter(Control.ForegroundProperty, Ink(light)));
		style4.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0.0)));
		w.Resources[typeof(ListBox)] = style4;
		Style style5 = new Style(typeof(ListBoxItem));
		style5.Setters.Add(new Setter(Control.ForegroundProperty, Ink(light)));
		style5.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12.0, 10.0, 12.0, 10.0)));
		style5.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0.0, 2.0, 0.0, 2.0)));
		style5.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		FrameworkElementFactory frameworkElementFactory12 = Frame(10.0);
		frameworkElementFactory12.AppendChild(Presenter());
		style5.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
		{
			VisualTree = frameworkElementFactory12
		}));
		Trigger trigger3 = new Trigger
		{
			Property = ListBoxItem.IsSelectedProperty,
			Value = true
		};
		trigger3.Setters.Add(new Setter(Control.BackgroundProperty, Color(light ? "#DCE8FF" : "#3A5074")));
		style5.Triggers.Add(trigger3);
		w.Resources[typeof(ListBoxItem)] = style5;
		Style style6 = new Style(typeof(TabItem));
		style6.Setters.Add(new Setter(Control.ForegroundProperty, Muted(light)));
		style6.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		style6.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14.0, 12.0, 14.0, 12.0)));
		style6.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0.0, 3.0, 8.0, 3.0)));
		style6.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Medium));
		FrameworkElementFactory frameworkElementFactory13 = Frame(11.0);
		FrameworkElementFactory frameworkElementFactory14 = Presenter();
		frameworkElementFactory14.SetBinding(ContentPresenter.ContentProperty, Parent("Header"));
		frameworkElementFactory13.AppendChild(frameworkElementFactory14);
		style6.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(TabItem))
		{
			VisualTree = frameworkElementFactory13
		}));
		Trigger trigger4 = new Trigger
		{
			Property = TabItem.IsSelectedProperty,
			Value = true
		};
		trigger4.Setters.Add(new Setter(Control.ForegroundProperty, Ink(light)));
		trigger4.Setters.Add(new Setter(Control.BackgroundProperty, Color(light ? "#DFEAFE" : "#344A70")));
		style6.Triggers.Add(trigger4);
		w.Resources[typeof(TabItem)] = style6;
		string xamlText = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type Slider}'><Grid Height='24'><Border Height='4' VerticalAlignment='Center' CornerRadius='2' Background='" + (light ? "#CBD5E6" : "#40516C") + "'/><Track x:Name='PART_Track' Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' Value='{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'><Track.DecreaseRepeatButton><RepeatButton Command='{x:Static Slider.DecreaseLarge}' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Height='4' VerticalAlignment='Center' CornerRadius='2' Background='#6A9BFA'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton><Track.IncreaseRepeatButton><RepeatButton Command='{x:Static Slider.IncreaseLarge}' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton><Track.Thumb><Thumb Width='16' Height='16'><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'><Border CornerRadius='8' Background='White' BorderBrush='#CBD5E6' BorderThickness='1'><Border.Effect><DropShadowEffect BlurRadius='5' ShadowDepth='1' Opacity='0.25'/></Border.Effect></Border></ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track></Grid></ControlTemplate>";
		Style style7 = new Style(typeof(Slider));
		style7.Setters.Add(new Setter(FrameworkElement.HeightProperty, 24.0));
		style7.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(xamlText)));
		w.Resources[typeof(Slider)] = style7;
		string xamlText2 = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type CheckBox}'><StackPanel Orientation='Horizontal' Background='Transparent'><Border Background='Transparent' x:Name='Box' Width='18' Height='18' CornerRadius='5' BorderBrush='#8091AA' BorderThickness='1' VerticalAlignment='Center'><TextBlock x:Name='Mark' IsHitTestVisible='False' Text='✓' Foreground='White' FontSize='13' HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='Collapsed'/></Border><ContentPresenter Margin='10,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True' IsHitTestVisible='False'/></StackPanel><ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='Box' Property='Background' Value='#6A9BFA'/><Setter TargetName='Box' Property='BorderBrush' Value='#6A9BFA'/><Setter TargetName='Mark' Property='Visibility' Value='Visible'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate>";
		Style style8 = new Style(typeof(CheckBox));
		style8.Setters.Add(new Setter(Control.ForegroundProperty, Ink(light)));
		style8.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(xamlText2)));
		w.Resources[typeof(CheckBox)] = style8;
		value = new Dictionary<object, Style>();
		foreach (DictionaryEntry resource in w.Resources)
		{
			Style style9 = resource.Value as Style;
			if (resource.Key is Type && style9 != null)
			{
				style9.Seal();
				value[resource.Key] = style9;
			}
		}
		themes[light] = value;
	}

	public static Border Section(string title, string description, bool light)
	{
		StackPanel stackPanel = new StackPanel();
		stackPanel.Children.Add(new TextBlock
		{
			Text = title,
			Foreground = Ink(light),
			FontSize = 18.0,
			FontWeight = FontWeights.SemiBold
		});
		if (!string.IsNullOrWhiteSpace(description))
		{
			stackPanel.Children.Add(new TextBlock
			{
				Text = description,
				Foreground = Muted(light),
				TextWrapping = TextWrapping.Wrap,
				FontSize = 12.0,
				Margin = new Thickness(0.0, 7.0, 0.0, 14.0)
			});
		}
		return new Border
		{
			CornerRadius = new CornerRadius(18.0),
			Background = Card(light),
			BorderBrush = Line(light),
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(20.0),
			Child = stackPanel,
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
		};
	}

	public static FrameworkElement Icon(string name, Brush color, double size = 20.0)
	{
		string source = name switch
		{
			"sun" => "M 10,5 A 5,5 0 1 1 9.99,5 M 10,1 L 10,3 M 10,17 L 10,19 M 1,10 L 3,10 M 17,10 L 19,10 M 3,3 L 5,5 M 15,15 L 17,17 M 3,17 L 5,15 M 15,5 L 17,3", 
			"settings" => "M 3,5 L 17,5 M 3,10 L 17,10 M 3,15 L 17,15 M 7,2 L 7,8 M 13,7 L 13,13 M 8,12 L 8,18", 
			"close" => "M 5,5 L 15,15 M 15,5 L 5,15", 
			"plus" => "M 10,3 L 10,17 M 3,10 L 17,10", 
			"note" => "M 5,2 L 15,2 L 18,5 L 18,18 L 2,18 L 2,2 Z M 6,8 L 14,8 M 6,12 L 13,12", 
			"repeat" => "M 4,5 L 15,5 L 18,8 M 15,2 L 18,5 L 15,8 M 16,15 L 5,15 L 2,12 M 5,12 L 2,15 L 5,18", 
			"shuffle" => "M 2,5 L 6,5 L 14,15 L 18,15 M 2,15 L 6,15 L 14,5 L 18,5 M 15,2 L 18,5 L 15,8 M 15,12 L 18,15 L 15,18", 
			"music" => "M 9,3 L 17,1 L 17,13 C 17,17 11,18 11,14 C 11,12 14,11 15,12 L 15,5 L 9,7 L 9,15 C 9,19 3,20 3,16 C 3,14 6,13 7,14 L 7,4 Z", 
			"previous" => "M 15.8,4.7 C 16.3,4.4 16.8,4.6 16.8,5.1 L 16.8,14.9 C 16.8,15.4 16.3,15.6 15.8,15.3 L 9.5,10.6 C 9,10.2 9,9.8 9.5,9.4 Z M 3.6,5 C 3.6,4.4 4.2,3.9 4.8,3.9 C 5.4,3.9 6,4.4 6,5 L 6,15 C 6,15.6 5.4,16.1 4.8,16.1 C 4.2,16.1 3.6,15.6 3.6,15 Z", 
			"next" => "M 3.7,4.7 C 3.2,4.4 2.7,4.6 2.7,5.1 L 2.7,14.9 C 2.7,15.4 3.2,15.6 3.7,15.3 L 10,10.6 C 10.5,10.2 10.5,9.8 10,9.4 Z M 13.5,5 C 13.5,4.4 14.1,3.9 14.7,3.9 C 15.3,3.9 15.9,4.4 15.9,5 L 15.9,15 C 15.9,15.6 15.3,16.1 14.7,16.1 C 14.1,16.1 13.5,15.6 13.5,15 Z", 
			"pause" => "M 5.4,4.5 C 5.4,3.8 6,3.2 6.7,3.2 C 7.4,3.2 8,3.8 8,4.5 L 8,15.5 C 8,16.2 7.4,16.8 6.7,16.8 C 6,16.8 5.4,16.2 5.4,15.5 Z M 12,4.5 C 12,3.8 12.6,3.2 13.3,3.2 C 14,3.2 14.6,3.8 14.6,4.5 L 14.6,15.5 C 14.6,16.2 14,16.8 13.3,16.8 C 12.6,16.8 12,16.2 12,15.5 Z", 
			"play" => "M 7.8,5.2 C 7.3,4.9 6.8,5.3 6.8,5.9 L 6.8,14.1 C 6.8,14.7 7.3,15.1 7.8,14.8 L 15.2,10.7 C 15.7,10.4 15.7,9.6 15.2,9.3 Z", 
			"grid" => "M 2,2 L 8,2 L 8,8 L 2,8 Z M 12,2 L 18,2 L 18,8 L 12,8 Z M 2,12 L 8,12 L 8,18 L 2,18 Z M 12,12 L 18,12 L 18,18 L 12,18 Z", 
			"folder" => "M 2,5 L 8,5 L 10,7 L 18,7 L 18,17 L 2,17 Z M 2,9 L 18,9", 
			_ => "M 3,4 L 17,4 L 17,17 L 3,17 Z M 6,1 L 6,7 M 14,1 L 14,7", 
		};
		int num;
		switch (name)
		{
		default:
			num = ((name == "music") ? 1 : 0);
			break;
		case "play":
		case "pause":
		case "next":
		case "previous":
			num = 1;
			break;
		}
		bool flag = (byte)num != 0;
		return new Path
		{
			Data = Geometry.Parse(source),
			Fill = (flag ? color : Brushes.Transparent),
			Stroke = (flag ? null : color),
			StrokeThickness = 1.5,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round,
			Width = size,
			Height = size,
			Stretch = Stretch.Uniform
		};
	}
}
