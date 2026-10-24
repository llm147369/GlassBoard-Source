using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

public static class GlassMaterials
{
	private static ImageSource grain;

	public static Brush Fill(MaterialOptions options, bool light, double percent)
	{
		if (percent <= 0.0)
		{
			return Brushes.Transparent;
		}
		Color color;
		if (string.IsNullOrEmpty(options.Tint))
		{
			color = (light ? Color.FromRgb(234, 237, 244) : Color.FromRgb(24, 26, 32));
		}
		else
		{
			color = (Color)ColorConverter.ConvertFromString(options.Tint);
		}
		color.A = (byte)Math.Round(percent * 255.0 / 100.0);
		if (options.Kind != 3 && options.Kind != 1)
		{
			return new SolidColorBrush(color);
		}
		Color startColor = color;
		startColor.R = (byte)Math.Min(255, startColor.R + 35);
		startColor.G = (byte)Math.Min(255, startColor.G + 35);
		startColor.B = (byte)Math.Min(255, startColor.B + 35);
		Color endColor = color;
		if (options.Kind == 1)
		{
			startColor.A = (byte)((double)(int)color.A * 0.65);
			endColor.A = (byte)((double)(int)color.A * 0.25);
		}
		return new LinearGradientBrush(startColor, endColor, 65.0);
	}

	public static ImageSource Grain()
	{
		if (grain != null)
		{
			return grain;
		}
		byte[] array = new byte[4096];
		uint num = 912367u;
		for (int i = 0; i < array.Length; i += 4)
		{
			num = num * 1664525 + 1013904223;
			byte b = (byte)(num >> 24);
			array[i] = (array[i + 1] = (array[i + 2] = b));
			array[i + 3] = 60;
		}
		BitmapSource bitmapSource = BitmapSource.Create(32, 32, 96.0, 96.0, PixelFormats.Bgra32, null, array, 128);
		bitmapSource.Freeze();
		grain = bitmapSource;
		return bitmapSource;
	}
}
