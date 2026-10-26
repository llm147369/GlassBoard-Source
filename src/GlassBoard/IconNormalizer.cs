using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard
{
	// 图标视觉统一器（Apple Dock 风格）：
	// 1) Normalize：把任意来源图标裁掉透明边距、等比缩放至固定画布的 88% 并居中，
	//    让 Dock、菜单、面板图标的视觉大小一致；
	// 2) WithPlate：在统一图形的基础上套一层圆角底板（macOS Dock 式样），
	//    面板磁贴所有图标看起来是完全等同的圆角方块。
	internal static class IconNormalizer
	{
		private const int CanvasSize = 128;
		private const double GlyphFill = 0.88;
		private const double PlateGlyphScale = 0.70;
		private const double PlateRadius = 24.0;

		// 原始源 → 统一图形；统一图形 → 带底板结果。按引用缓存（图标源本身有全局缓存，
		// 引用稳定），避免重复计算；命中时返回的是规范化后的结果而非原始图。
		private static readonly Dictionary<ImageSource, ImageSource> glyphCache = new Dictionary<ImageSource, ImageSource>();
		private static readonly Dictionary<ImageSource, ImageSource> plateCache = new Dictionary<ImageSource, ImageSource>();

		internal static ImageSource Normalize(ImageSource source)
		{
			if (source == null)
			{
				return null;
			}
			if (glyphCache.TryGetValue(source, out ImageSource cached))
			{
				return cached;
			}
			ImageSource result = DoGlyph(source);
			glyphCache.Add(source, result);
			Prune(glyphCache);
			return result;
		}

		internal static ImageSource WithPlate(ImageSource source, bool lightTheme)
		{
			if (source == null)
			{
				return null;
			}
			ImageSource glyph = Normalize(source);
			if (plateCache.TryGetValue(glyph, out ImageSource cached))
			{
				return cached;
			}
			ImageSource result = DoPlate(glyph, lightTheme);
			plateCache.Add(glyph, result);
			Prune(plateCache);
			return result;
		}

		private static void Prune(Dictionary<ImageSource, ImageSource> cache)
		{
			if (cache.Count <= 600)
			{
				return;
			}
			int drop = cache.Count - 500;
			foreach (KeyValuePair<ImageSource, ImageSource> pair in cache)
			{
				cache.Remove(pair.Key);
				if (--drop <= 0)
				{
					break;
				}
			}
		}

		// 统一图形：裁透明边距 → 等比缩放至画布 88% → 居中。矢量源先栅格化。
		private static ImageSource DoGlyph(ImageSource source)
		{
			try
			{
				BitmapSource src;
				if (source is BitmapSource bitmap)
				{
					src = bitmap;
				}
				else
				{
					// 矢量图标（DrawingImage 等）先栅格化到 96×96 再走统一流程
					RenderTargetBitmap raster = new RenderTargetBitmap(96, 96, 96.0, 96.0, PixelFormats.Pbgra32);
					DrawingVisual visual = new DrawingVisual();
					using (DrawingContext dc = visual.RenderOpen())
					{
						dc.DrawImage(source, new Rect(0.0, 0.0, 96.0, 96.0));
					}
					raster.Render(visual);
					src = raster;
				}
				if (src.Format != PixelFormats.Pbgra32)
				{
					src = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
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
					return source;
				}
				Int32Rect bounds = new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
				BitmapSource content = new CroppedBitmap(src, bounds);
				double scale = Math.Min(CanvasSize * GlyphFill / bounds.Width, CanvasSize * GlyphFill / bounds.Height);
				int dw = Math.Max(1, (int)Math.Round(bounds.Width * scale));
				int dh = Math.Max(1, (int)Math.Round(bounds.Height * scale));
				RenderTargetBitmap canvasBmp = new RenderTargetBitmap(CanvasSize, CanvasSize, 96.0, 96.0, PixelFormats.Pbgra32);
				DrawingVisual visual2 = new DrawingVisual();
				using (DrawingContext dc2 = visual2.RenderOpen())
				{
					dc2.DrawImage(content, new Rect((CanvasSize - dw) / 2.0, (CanvasSize - dh) / 2.0, dw, dh));
				}
				canvasBmp.Render(visual2);
				canvasBmp.Freeze();
				return canvasBmp;
			}
			catch
			{
				return source;
			}
		}

		// macOS Dock 式圆角底板 + 居中图形（图形画布按 70% 等比缩放，内容约占底板 62%）。
		private static ImageSource DoPlate(ImageSource glyph, bool lightTheme)
		{
			try
			{
				double plate = CanvasSize - 4.0;
				double glyphBox = CanvasSize * PlateGlyphScale;
				RenderTargetBitmap canvasBmp = new RenderTargetBitmap(CanvasSize, CanvasSize, 96.0, 96.0, PixelFormats.Pbgra32);
				DrawingVisual visual = new DrawingVisual();
				using (DrawingContext dc = visual.RenderOpen())
				{
					Color fill = lightTheme ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(38, 255, 255, 255);
					Color border = lightTheme ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255);
					dc.DrawRoundedRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(border), 1.0), new Rect(2.0, 2.0, plate, plate), PlateRadius, PlateRadius);
					dc.DrawImage(glyph, new Rect((CanvasSize - glyphBox) / 2.0, (CanvasSize - glyphBox) / 2.0, glyphBox, glyphBox));
				}
				canvasBmp.Render(visual);
				canvasBmp.Freeze();
				return canvasBmp;
			}
			catch
			{
				return glyph;
			}
		}
	}
}
