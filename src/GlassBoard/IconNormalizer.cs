using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard
{
	// 图标视觉统一器：不同来源的图标（exe 图标、图片缩略图、自定义图标）
	// 原始画布与留白各不相同，直接缩放会让磁贴看起来大小不一。
	// Normalize 裁掉透明边距、把内容等比缩放至固定画布的 82% 并居中，
	// 保证所有图标在面板/Dock 上视觉尺寸一致。
	internal static class IconNormalizer
	{
		private const int CanvasSize = 128;
		private const double FillRatio = 0.82;

		// 已规范化过的源按引用去重：缓存图片会被复用，避免二次缩小。
		private static readonly HashSet<ImageSource> Done = new HashSet<ImageSource>();

		internal static ImageSource Normalize(ImageSource source)
		{
			if (source == null || Done.Contains(source))
			{
				return source;
			}
			if (!(source is BitmapSource bitmap))
			{
				// 矢量图标（如文件夹 DrawingImage）本身设计一致，跳过
				Done.Add(source);
				return source;
			}
			try
			{
				BitmapSource src = bitmap;
				if (bitmap.Format != PixelFormats.Pbgra32)
				{
					src = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
				}
				int w = src.PixelWidth;
				int h = src.PixelHeight;
				int stride = w * 4;
				byte[] pixels = new byte[stride * h];
				src.CopyPixels(pixels, stride, 0);
				int minX = w;
				int minY = h;
				int maxX = -1;
				int maxY = -1;
				for (int y = 0; y < h; y++)
				{
					int row = y * stride;
					for (int x = 0; x < w; x++)
					{
						if (pixels[row + x * 4 + 3] > 0)
						{
							if (x < minX)
							{
								minX = x;
							}
							if (x > maxX)
							{
								maxX = x;
							}
							if (y < minY)
							{
								minY = y;
							}
							if (y > maxY)
							{
								maxY = y;
							}
						}
					}
				}
				if (maxX < minX || maxY < minY)
				{
					Done.Add(source);
					return source;
				}
				Int32Rect bounds = new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
				BitmapSource content = new CroppedBitmap(src, bounds);
				double scale = Math.Min(CanvasSize * FillRatio / bounds.Width, CanvasSize * FillRatio / bounds.Height);
				int dw = Math.Max(1, (int)Math.Round(bounds.Width * scale));
				int dh = Math.Max(1, (int)Math.Round(bounds.Height * scale));
				RenderTargetBitmap canvasBmp = new RenderTargetBitmap(CanvasSize, CanvasSize, 96.0, 96.0, PixelFormats.Pbgra32);
				DrawingVisual visual = new DrawingVisual();
				using (DrawingContext dc = visual.RenderOpen())
				{
					dc.DrawImage(content, new Rect((CanvasSize - dw) / 2.0, (CanvasSize - dh) / 2.0, dw, dh));
				}
				canvasBmp.Render(visual);
				canvasBmp.Freeze();
				Done.Add(source);
				return canvasBmp;
			}
			catch
			{
				return source;
			}
		}
	}
}
