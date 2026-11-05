using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

public static class GlassMaterials
{
	private static ImageSource grain;

	public static Brush Fill(MaterialOptions options, bool light, double percent, int accent = 0)
	{
		if (percent <= 0.0)
		{
			return Brushes.Transparent;
		}
		Color color;
		if (string.IsNullOrEmpty(options.Tint))
		{
			color = (light ? Color.FromRgb(234, 237, 244) : Color.FromRgb(24, 26, 32));
			// "跟随强调色"：把强调色以 12% 掺入玻璃底色（与菜单同源思路），切换强调色时玻璃可见变化
			Color accentColor = IconPacks.AccentColor(accent);
			color = Color.FromRgb((byte)((double)color.R + ((double)(int)accentColor.R - (double)color.R) * 0.12), (byte)((double)color.G + ((double)(int)accentColor.G - (double)color.G) * 0.12), (byte)((double)color.B + ((double)(int)accentColor.B - (double)color.B) * 0.12));
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
		// 先按 LCG 生成原始灰阶噪声，再做一次 3x3 盒式模糊让噪点变成柔和细屑：
		// 修复“更新后 Dock/组件/面板出现脏白点”——旧噪点 0-255 全灰度且 alpha=60，
		// 高对比硬颗粒在玻璃表面读起来就是脏点。
		int size = 32;
		byte[] src = new byte[size * size * 4];
		uint num = 912367u;
		for (int i = 0; i < src.Length; i += 4)
		{
			num = num * 1664525 + 1013904223;
			byte b = (byte)(num >> 24);
			src[i] = (src[i + 1] = (src[i + 2] = b));
			src[i + 3] = 255;
		}
		byte[] array = new byte[size * size * 4];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				int sum = 0;
				for (int dy = -1; dy <= 1; dy++)
				{
					for (int dx = -1; dx <= 1; dx++)
					{
						sum += src[((y + dy + size) % size * size + (x + dx + size) % size) * 4];
					}
				}
				int v = sum / 9;
				byte b = (byte)(208 + v % 48); // 收窄到 208-255：浅色细屑，深色玻璃上是细腻霜感、浅色玻璃上几乎不可见
				int o = (y * size + x) * 4;
				array[o] = (array[o + 1] = (array[o + 2] = b));
				array[o + 3] = (byte)(14 + v % 14); // alpha 14-27，约为旧值(60)的一半以下
			}
		}
		BitmapSource bitmapSource = BitmapSource.Create(size, size, 96.0, 96.0, PixelFormats.Bgra32, null, array, size * 4);
		bitmapSource.Freeze();
		grain = bitmapSource;
		return bitmapSource;
	}

	// 高质量缩小：先按 2 倍步长降到不超过目标尺寸 4 倍，再一步到位（HighQualityBicubic）。
	// 修复“底图污斑”：单步 new Bitmap(src,w,h) 使用默认 Bilinear，8 倍缩小会把图标/文字
	// 等高频边缘混叠成条纹，Stretch.Fill 拉回放大并叠加模糊后读作脏斑。
	public static System.Drawing.Bitmap Downsample(System.Drawing.Bitmap src, int w, int h)
	{
		int cw = src.Width;
		int ch = src.Height;
		System.Drawing.Bitmap cur = src;
		bool owned = false;
		try
		{
			while (cw > w * 4 || ch > h * 4)
			{
				int nw = Math.Max(1, cw / 2);
				int nh = Math.Max(1, ch / 2);
				System.Drawing.Bitmap half = new System.Drawing.Bitmap(nw, nh);
				using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(half))
				{
					g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
					g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
					g.DrawImage(cur, 0, 0, nw, nh);
				}
				if (owned)
				{
					cur.Dispose();
				}
				cur = half;
				owned = true;
				cw = nw;
				ch = nh;
			}
			System.Drawing.Bitmap result = new System.Drawing.Bitmap(w, h);
			using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(result))
			{
				g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
				g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
				g.DrawImage(cur, 0, 0, w, h);
			}
			return result;
		}
		finally
		{
			if (owned)
			{
				cur.Dispose();
			}
		}
	}

	// 捕获时一次性烘焙的可分离盒式模糊（两遍）：视觉等价高斯模糊，
	// 把开销从“每帧”挪到“捕获一次”（原灵动岛私有实现，提升为 Dock/面板共用）。
	public static void BoxBlur(System.Drawing.Bitmap bmp, int radius)
	{
		try
		{
			int w = bmp.Width;
			int h = bmp.Height;
			if (w <= 2 || h <= 2)
			{
				return;
			}
			System.Drawing.Imaging.BitmapData data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			try
			{
				int stride = data.Stride;
				byte[] buf = new byte[stride * h];
				System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
				byte[] tmp = new byte[buf.Length];
				int div = 2 * radius + 1;
				for (int y = 0; y < h; y++)
				{
					int row = y * stride;
					for (int x = 0; x < w; x++)
					{
						int r = 0;
						int g = 0;
						int b = 0;
						for (int k = -radius; k <= radius; k++)
						{
							int xx = Math.Max(0, Math.Min(w - 1, x + k));
							int p = row + xx * 4;
							b += buf[p];
							g += buf[p + 1];
							r += buf[p + 2];
						}
						int q = row + x * 4;
						tmp[q] = (byte)(b / div);
						tmp[q + 1] = (byte)(g / div);
						tmp[q + 2] = (byte)(r / div);
						tmp[q + 3] = buf[q + 3];
					}
				}
				for (int y = 0; y < h; y++)
				{
					int row = y * stride;
					for (int x = 0; x < w; x++)
					{
						int r = 0;
						int g = 0;
						int b = 0;
						for (int k = -radius; k <= radius; k++)
						{
							int yy = Math.Max(0, Math.Min(h - 1, y + k));
							int p = yy * stride + x * 4;
							b += tmp[p];
							g += tmp[p + 1];
							r += tmp[p + 2];
						}
						int q = row + x * 4;
						buf[q] = (byte)(b / div);
						buf[q + 1] = (byte)(g / div);
						buf[q + 2] = (byte)(r / div);
					}
				}
				System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
			}
			finally
			{
				bmp.UnlockBits(data);
			}
		}
		catch
		{
		}
	}

	// 统一背景烘焙：模糊关闭/很小时保留原分辨率；否则 1/8 高质量缩小 + 盒式模糊，
	// PNG 往返产出可冻结的 BitmapImage。运行时图层不再挂 BlurEffect（透明窗上的 GPU
	// 高斯模糊整层重渲染：既拖动画又产生 banding 伪影）。基类与灵动岛共用，保证
	// 任意窗口“设置的样式”与实际显示完全一致。
	public static BitmapImage Bake(System.Drawing.Bitmap bitmap, double blurRadius, double blurPx)
	{
		using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
		{
			if (blurRadius < 6.0)
			{
				bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
			}
			else
			{
				using (System.Drawing.Bitmap small = Downsample(bitmap, Math.Max(1, bitmap.Width / 8), Math.Max(1, bitmap.Height / 8)))
				{
					if (blurPx >= 1.0)
					{
						BoxBlur(small, Math.Min(8, Math.Max(1, (int)Math.Round(blurPx))));
					}
					small.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
				}
			}
			ms.Position = 0L;
			BitmapImage img = new BitmapImage();
			img.BeginInit();
			img.CacheOption = BitmapCacheOption.OnLoad;
			img.StreamSource = ms;
			img.EndInit();
			img.Freeze();
			return img;
		}
	}
}
