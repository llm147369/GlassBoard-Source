using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

public static class GlassMaterials
{
	private static ImageSource grain;

	public static Brush Fill(MaterialOptions options, bool light, double percent, int accent = -1)
	{
		if (percent <= 0.0)
		{
			return Brushes.Transparent;
		}
		Color color;
		if (string.IsNullOrEmpty(options.Tint))
		{
			color = (light ? Color.FromRgb(234, 237, 244) : Color.FromRgb(24, 26, 32));
			// 强调色 12% 掺入玻璃底色：仅大面板等保留区域使用；Dock / 组件背景传 -1 不掺色
			if (accent >= 0)
			{
				Color accentColor = IconPacks.AccentColor(accent);
				color = Color.FromRgb((byte)((double)color.R + ((double)(int)accentColor.R - (double)color.R) * 0.12), (byte)((double)color.G + ((double)(int)accentColor.G - (double)color.G) * 0.12), (byte)((double)color.B + ((double)(int)accentColor.B - (double)color.B) * 0.12));
			}
		}
		else
		{
			color = (Color)ColorConverter.ConvertFromString(options.Tint);
		}
		// Apple 玻璃色调叠加：在底色上按 TintAlpha 叠一层主题自适应色调（浅色叠白、深色叠深蓝灰），
		// 旧材质 TintBlend=false 保持"Tint 整体替换底色"的原行为不变。
		if (options.TintBlend && options.TintAlpha > 0.0)
		{
			Color overlay = (light ? Color.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue) : Color.FromRgb(16, 19, 26));
			double a = Math.Min(options.TintAlpha, 0.30);
			color = Color.FromRgb((byte)((double)(int)color.R + ((double)(int)overlay.R - (double)(int)color.R) * a), (byte)((double)(int)color.G + ((double)(int)overlay.G - (double)(int)color.G) * a), (byte)((double)(int)color.B + ((double)(int)overlay.B - (double)(int)color.B) * a));
		}
		color.A = (byte)Math.Round(percent * 255.0 / 100.0);
		// 实时背景模糊（Apple 玻璃）柔光：底色降到低浓度（顶部 30% / 底部 24%），
		// 底图保持通透。
		if (options.LiveBlur)
		{
			Color top = color;
			top.A = (byte)Math.Round((double)(int)color.A * 0.30);
			Color bottom = color;
			bottom.A = (byte)Math.Round((double)(int)color.A * 0.24);
			return new LinearGradientBrush(top, bottom, 90.0);
		}
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

	// 深色调 tint 直接压玻璃底色时，浅色主题的深色文字会不可读：按色调亮度判定"深色表面"，
	// 供 GlassWindow.DarkSurface（文字刷）与 MenuTheme（菜单配色）共用，自动切浅色文字。
	public static bool IsDarkTint(string tint)
	{
		if (string.IsNullOrWhiteSpace(tint))
		{
			return false;
		}
		try
		{
			Color c = (Color)ColorConverter.ConvertFromString(tint);
			return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 < 0.5;
		}
		catch
		{
			return false;
		}
	}

	public static bool DarkSurface(MaterialOptions options, bool light)
	{
		return IsDarkTint(options?.Tint) || !light;
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

	// 饱和度增强（Apple 玻璃的"浓郁感"）：对每个像素按亮度轴缩放饱和度，
	// 与 CSS saturate() 同公式。只在捕获烘焙时跑一次，运行时不参与。
	public static void Saturate(System.Drawing.Bitmap bmp, double saturation)
	{
		if (saturation <= 1.001)
		{
			return;
		}
		try
		{
			int w = bmp.Width;
			int h = bmp.Height;
			System.Drawing.Imaging.BitmapData data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			try
			{
				int stride = data.Stride;
				byte[] buf = new byte[stride * h];
				System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
				double s = saturation;
				for (int i = 0; i < buf.Length; i += 4)
				{
					double r = buf[i + 2];
					double g = buf[i + 1];
					double b = buf[i];
					double l = 0.299 * r + 0.587 * g + 0.114 * b;
					buf[i + 2] = (byte)Math.Max(0.0, Math.Min(255.0, l + (r - l) * s));
					buf[i + 1] = (byte)Math.Max(0.0, Math.Min(255.0, l + (g - l) * s));
					buf[i] = (byte)Math.Max(0.0, Math.Min(255.0, l + (b - l) * s));
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

	// 亮度缩放（小米玻璃柔光 bgClrSatBri 的亮度分量）：模糊后整体提亮，
	// 让雾光层比原始桌面更亮一档，玻璃感立刻拉开。
	public static void Brighten(System.Drawing.Bitmap bmp, double gain)
	{
		if (gain <= 0.0 || Math.Abs(gain - 1.0) < 0.001)
		{
			return;
		}
		try
		{
			int w = bmp.Width;
			int h = bmp.Height;
			System.Drawing.Imaging.BitmapData data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			try
			{
				int stride = data.Stride;
				byte[] buf = new byte[stride * h];
				System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
				for (int i = 0; i < buf.Length; i += 4)
				{
					buf[i] = (byte)Math.Min(255.0, buf[i] * gain);
					buf[i + 1] = (byte)Math.Min(255.0, buf[i + 1] * gain);
					buf[i + 2] = (byte)Math.Min(255.0, buf[i + 2] * gain);
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

	// 双图 50/50 混合：dst = dst*(1-t) + src*t。多尺度模糊合成用。
	private static void Blend(System.Drawing.Bitmap dst, System.Drawing.Bitmap src, double t)
	{
		try
		{
			int w = Math.Min(dst.Width, src.Width);
			int h = Math.Min(dst.Height, src.Height);
			System.Drawing.Imaging.BitmapData d = dst.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			System.Drawing.Imaging.BitmapData s = src.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			try
			{
				int stride = d.Stride;
				byte[] db = new byte[stride * h];
				byte[] sb = new byte[stride * h];
				System.Runtime.InteropServices.Marshal.Copy(d.Scan0, db, 0, db.Length);
				System.Runtime.InteropServices.Marshal.Copy(s.Scan0, sb, 0, sb.Length);
				for (int i = 0; i < db.Length; i++)
				{
					db[i] = (byte)(db[i] * (1.0 - t) + sb[i] * t);
				}
				System.Runtime.InteropServices.Marshal.Copy(db, 0, d.Scan0, db.Length);
			}
			finally
			{
				dst.UnlockBits(d);
				src.UnlockBits(s);
			}
		}
		catch
		{
		}
	}

	// 双线性采样（RGBA 独立插值），折射重采样用。
	private static System.Drawing.Color Bilinear(byte[] buf, int stride, int w, int h, double x, double y)
	{
		int x0 = (int)Math.Floor(x);
		int y0 = (int)Math.Floor(y);
		int x1 = Math.Min(x0 + 1, w - 1);
		int y1 = Math.Min(y0 + 1, h - 1);
		x0 = Math.Max(0, Math.Min(x0, w - 1));
		y0 = Math.Max(0, Math.Min(y0, h - 1));
		double fx = x - Math.Floor(x);
		double fy = y - Math.Floor(y);
		if (fx < 0.0)
		{
			fx = 0.0;
		}
		if (fy < 0.0)
		{
			fy = 0.0;
		}
		int i00 = y0 * stride + x0 * 4;
		int i10 = y0 * stride + x1 * 4;
		int i01 = y1 * stride + x0 * 4;
		int i11 = y1 * stride + x1 * 4;
		double b = (1.0 - fx) * (1.0 - fy);
		double t = fx * (1.0 - fy);
		double l = (1.0 - fx) * fy;
		double r = fx * fy;
		return System.Drawing.Color.FromArgb((byte)(buf[i00 + 3] * b + buf[i10 + 3] * t + buf[i01 + 3] * l + buf[i11 + 3] * r), (byte)(buf[i00 + 2] * b + buf[i10 + 2] * t + buf[i01 + 2] * l + buf[i11 + 2] * r), (byte)(buf[i00 + 1] * b + buf[i10 + 1] * t + buf[i01 + 1] * l + buf[i11 + 1] * r), (byte)(buf[i00] * b + buf[i10] * t + buf[i01] * l + buf[i11] * r));
	}

	// 静态透镜折射 + 色散（Apple Liquid Glass 的 refraction + chromatic aberration）：
	// 凸透镜把背景向中心收缩——中心不动，越靠边位移越大（strength 为最大像素位移）。
	// R 通道向位移方向 +ca、B 通道 -ca 采样错位，边缘处出现 RGB 分离的色散边。
	private static void Refract(System.Drawing.Bitmap bmp, double strength, double ca)
	{
		if (strength <= 0.0 || bmp.Width < 4 || bmp.Height < 4)
		{
			return;
		}
		try
		{
			int w = bmp.Width;
			int h = bmp.Height;
			System.Drawing.Bitmap src = (System.Drawing.Bitmap)bmp.Clone();
			try
			{
				System.Drawing.Imaging.BitmapData d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
				System.Drawing.Imaging.BitmapData s = src.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
				try
				{
					int stride = d.Stride;
					byte[] dstBuf = new byte[stride * h];
					byte[] srcBuf = new byte[stride * h];
					System.Runtime.InteropServices.Marshal.Copy(s.Scan0, srcBuf, 0, srcBuf.Length);
					double cx = (w - 1) / 2.0;
					double cy = (h - 1) / 2.0;
					double maxR = Math.Sqrt(cx * cx + cy * cy);
					for (int y = 0; y < h; y++)
					{
						for (int x = 0; x < w; x++)
						{
							double dx = x - cx;
							double dy = y - cy;
							double dist = Math.Sqrt(dx * dx + dy * dy);
							double k = dist / Math.Max(1.0, maxR);
							double pull = strength * k * k;
							double ux = cx + dx * Math.Max(0.0, 1.0 - pull / Math.Max(1.0, dist));
							double uy = cy + dy * Math.Max(0.0, 1.0 - pull / Math.Max(1.0, dist));
							System.Drawing.Color cr = Bilinear(srcBuf, stride, w, h, ux + ca, uy);
							System.Drawing.Color cg = Bilinear(srcBuf, stride, w, h, ux, uy);
							System.Drawing.Color cb = Bilinear(srcBuf, stride, w, h, ux - ca, uy);
							int i = y * stride + x * 4;
							dstBuf[i] = cb.B;
							dstBuf[i + 1] = cg.G;
							dstBuf[i + 2] = cr.R;
							dstBuf[i + 3] = cg.A;
						}
					}
					System.Runtime.InteropServices.Marshal.Copy(dstBuf, 0, d.Scan0, dstBuf.Length);
				}
				finally
				{
					bmp.UnlockBits(d);
					src.UnlockBits(s);
				}
			}
			finally
			{
				src.Dispose();
			}
		}
		catch
		{
		}
	}

	// Apple 玻璃冷调（cool glass tint）：RGB 乘 (0.97, 1.0, 1.05)，20% 强度的蓝移。
	private static void CoolTint(System.Drawing.Bitmap bmp)
	{
		try
		{
			int w = bmp.Width;
			int h = bmp.Height;
			System.Drawing.Imaging.BitmapData data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			try
			{
				int stride = data.Stride;
				byte[] buf = new byte[stride * h];
				System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
				for (int i = 0; i < buf.Length; i += 4)
				{
					buf[i] = (byte)Math.Min(255.0, buf[i] * 1.02);
					buf[i + 1] = (byte)Math.Min(255.0, buf[i + 1] * 1.005);
					buf[i + 2] = (byte)Math.Min(255.0, buf[i + 2] * 0.985);
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
	// 任意窗口“设置的样式”与实际显示完全一致。saturation>1 时先做饱和度增强。
	public static BitmapImage Bake(System.Drawing.Bitmap bitmap, double blurRadius, double blurPx, double saturation = 1.0)
	{
		using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
		{
			if (blurRadius < 6.0)
			{
				Saturate(bitmap, saturation);
				bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
			}
			else
			{
				using (System.Drawing.Bitmap small = Downsample(bitmap, Math.Max(1, bitmap.Width / 8), Math.Max(1, bitmap.Height / 8)))
				{
					Saturate(small, saturation);
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

	// 实时背景烘焙（Apple 玻璃 LiveBlur 专用）：1/4 分辨率 + 多尺度双半径模糊
	// （小米玻璃柔光配方思路：小半径保边缘细节 + 大半径铺开雾光，50/50 混合），
	// 后接液体玻璃三件套：径向折射（含 0.7px 色散错位）→ 冷调（cool glass tint）→
	// 轻微饱和度 → 提亮。
	public static BitmapImage BakeLive(System.Drawing.Bitmap bitmap, double blurRadius, double blurPx, double saturation)
	{
		using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
		{
			using (System.Drawing.Bitmap small = Downsample(bitmap, Math.Max(1, bitmap.Width / 4), Math.Max(1, bitmap.Height / 4)))
			{
				if (blurPx >= 1.0)
				{
					int rWide = Math.Min(64, Math.Max(2, (int)Math.Round(blurPx * 2.0)));
					BoxBlur(small, rWide);
					BoxBlur(small, rWide);
					using (System.Drawing.Bitmap fine = (System.Drawing.Bitmap)small.Clone())
					{
						int rFine = Math.Min(16, Math.Max(1, (int)Math.Round(blurPx * 0.6)));
						BoxBlur(fine, rFine);
						BoxBlur(fine, rFine);
						Blend(small, fine, 0.5);
					}
				}
				Refract(small, Math.Max(0.5, blurPx * 0.35), 0.7);
				CoolTint(small);
				Saturate(small, saturation);
				Brighten(small, 1.05);
				small.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
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
