using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard
{
	// 图标视觉统一器：
	// Normalize 把任意来源图标裁掉透明边距、等比缩放至固定画布的 88% 并居中，
	// 让 Dock、菜单、面板图标的视觉大小一致。结果用 DrawingImage 矢量合成
	// （不经过 RenderTargetBitmap 栅格化），渲染走 GPU 合成管线，零软件光栅开销。
	internal static class IconNormalizer
	{
		private const int CanvasSize = 128;
		private const double GlyphFill = 0.88;

		// 原始源 → 统一图形。按引用缓存（图标源本身有全局缓存，引用稳定），
		// 避免重复计算；命中时返回的是规范化后的结果而非原始图。
		private static readonly Dictionary<ImageSource, ImageSource> glyphCache = new Dictionary<ImageSource, ImageSource>();

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
			Prune();
			return result;
		}

		private static void Prune()
		{
			if (glyphCache.Count <= 600)
			{
				return;
			}
			int drop = glyphCache.Count - 500;
			foreach (KeyValuePair<ImageSource, ImageSource> pair in glyphCache)
			{
				glyphCache.Remove(pair.Key);
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
					// 矢量图标（DrawingImage 等）先栅格化到 256×256 再走统一流程（保证放大不糊）
					RenderTargetBitmap raster = new RenderTargetBitmap(256, 256, 96.0, 96.0, PixelFormats.Pbgra32);
					DrawingVisual visual = new DrawingVisual();
					using (DrawingContext dc = visual.RenderOpen())
					{
						dc.DrawImage(source, new Rect(0.0, 0.0, 256.0, 256.0));
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
				// 隔点采样扫描透明边界：4 倍提速，误差 ±1px 视觉不可感知
				int minX = w;
				int minY = h;
				int maxX = -1;
				int maxY = -1;
				for (int y = 0; y < h; y += 2)
				{
					int row = y * stride;
					for (int x = 0; x < w; x += 2)
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
				double dw = bounds.Width * scale;
				double dh = bounds.Height * scale;
				// DrawingImage 矢量合成：缩放由渲染管线完成，无 RTB 软件光栅
				DrawingImage result = new DrawingImage(new ImageDrawing(content, new Rect((CanvasSize - dw) / 2.0, (CanvasSize - dh) / 2.0, dw, dh)));
				result.Freeze();
				return result;
			}
			catch
			{
				return source;
			}
		}
	}
}
