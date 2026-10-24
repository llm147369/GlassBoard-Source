using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

public static class ShellIconImages
{
	[ComImport]
	[Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IImageList
	{
		[PreserveSig]
		int Add(IntPtr image, IntPtr mask, out int index);

		[PreserveSig]
		int ReplaceIcon(int index, IntPtr icon, out int result);

		[PreserveSig]
		int SetOverlayImage(int index, int overlay);

		[PreserveSig]
		int Replace(int index, IntPtr image, IntPtr mask);

		[PreserveSig]
		int AddMasked(IntPtr image, uint color, out int index);

		[PreserveSig]
		int Draw(IntPtr parameters);

		[PreserveSig]
		int Remove(int index);

		[PreserveSig]
		int GetIcon(int index, uint flags, out IntPtr icon);
	}

	[DllImport("shell32.dll")]
	private static extern int SHGetImageList(int size, ref Guid id, out IImageList list);

	[DllImport("user32.dll")]
	private static extern bool DestroyIcon(IntPtr icon);

	public static BitmapSource Read(int index, uint overlayMask = 0u)
	{
		int[] array = new int[2] { 4, 2 };
		foreach (int num in array)
		{
			IImageList list = null;
			IntPtr icon = IntPtr.Zero;
			try
			{
				Guid id = typeof(IImageList).GUID;
				if (SHGetImageList(num, ref id, out list) >= 0 && list != null && list.GetIcon(index, 1 | overlayMask, out icon) >= 0 && !(icon == IntPtr.Zero))
				{
					BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
					bitmapSource.Freeze();
					if (num != 4 || !TinyFootprint(bitmapSource))
					{
						return Limit(bitmapSource, 128);
					}
				}
			}
			catch
			{
			}
			finally
			{
				if (icon != IntPtr.Zero)
				{
					DestroyIcon(icon);
				}
				if (list != null && Marshal.IsComObject(list))
				{
					Marshal.ReleaseComObject(list);
				}
			}
		}
		return null;
	}

	private static bool TinyFootprint(BitmapSource source)
	{
		if (source.PixelWidth < 192 || source.PixelHeight < 192)
		{
			return false;
		}
		FormatConvertedBitmap formatConvertedBitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0.0);
		int pixelWidth = formatConvertedBitmap.PixelWidth;
		int pixelHeight = formatConvertedBitmap.PixelHeight;
		int num = pixelWidth * 4;
		byte[] array = new byte[num * pixelHeight];
		formatConvertedBitmap.CopyPixels(array, num, 0);
		int num2 = pixelWidth;
		int num3 = pixelHeight;
		int num4 = -1;
		int num5 = -1;
		for (int i = 0; i < pixelHeight; i++)
		{
			for (int j = 0; j < pixelWidth; j++)
			{
				if (array[i * num + j * 4 + 3] > 16)
				{
					num2 = Math.Min(num2, j);
					num4 = Math.Max(num4, j);
					num3 = Math.Min(num3, i);
					num5 = Math.Max(num5, i);
				}
			}
		}
		if (num4 >= 0)
		{
			return Math.Max(num4 - num2 + 1, num5 - num3 + 1) < 128;
		}
		return true;
	}

	private static BitmapSource Limit(BitmapSource source, int maximum)
	{
		if (Math.Max(source.PixelWidth, source.PixelHeight) <= maximum)
		{
			return source;
		}
		double num = (double)maximum / (double)Math.Max(source.PixelWidth, source.PixelHeight);
		int num2 = Math.Max(1, (int)Math.Round((double)source.PixelWidth * num));
		int num3 = Math.Max(1, (int)Math.Round((double)source.PixelHeight * num));
		DrawingVisual drawingVisual = new DrawingVisual();
		RenderOptions.SetBitmapScalingMode(drawingVisual, BitmapScalingMode.HighQuality);
		using (DrawingContext drawingContext = drawingVisual.RenderOpen())
		{
			drawingContext.DrawImage(source, new Rect(0.0, 0.0, num2, num3));
		}
		RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(num2, num3, 96.0, 96.0, PixelFormats.Pbgra32);
		renderTargetBitmap.Render(drawingVisual);
		renderTargetBitmap.Freeze();
		return renderTargetBitmap;
	}
}
