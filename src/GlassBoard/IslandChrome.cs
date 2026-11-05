using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace GlassBoard;

// 灵动岛外观渲染共享逻辑 + 设置页实时预览。
// IslandWindow 与设置页预览共用 IslandChrome.Apply，保证所见即所得。
public static class IslandChrome
{
	// 把 IslandOptions 应用到 Surface Border（背景、边框、阴影、圆角、背景图）。
	// 不设置 Width/Height/位置——由调用方决定。
	public static void Apply(Border surface, IslandOptions o, bool light)
	{
		if (o == null)
		{
			o = new IslandOptions();
		}
		Brush bg = null;
		if (!string.IsNullOrEmpty(o.CustomImage))
		{
			string path = o.CustomImage;
			try
			{
				path = Path.GetFullPath(path);
				string root = Path.GetFullPath(Store.Folder);
				if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					path = ""; // 越界路径不读取
				}
			}
			catch
			{
				path = "";
			}
			if (path.Length > 0 && File.Exists(path))
			{
				try
				{
					BitmapImage img = new BitmapImage(new Uri(path));
					Stretch stretch = o.ImageCrop == 0 ? Stretch.UniformToFill : o.ImageCrop == 1 ? Stretch.Uniform : Stretch.Fill;
					// 背景图 + 遮罩浓度合成进同一个画刷，不触碰 surface.Child（内容是调用方的）
					DrawingGroup group = new DrawingGroup();
					group.Children.Add(new ImageDrawing(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight)));
					double darken = Math.Max(0.0, Math.Min(1.0, o.ImageDarken));
					if (darken > 0.01)
					{
						Brush veil = GlassMaterials.Fill(o.Material, light, o.OpacityPercent, o.Accent);
						veil.Opacity = darken;
						group.Children.Add(new GeometryDrawing(veil, null, new RectangleGeometry(new Rect(0, 0, img.PixelWidth, img.PixelHeight))));
					}
					bg = new DrawingBrush
					{
						Drawing = group,
						Viewbox = new Rect(0, 0, img.PixelWidth, img.PixelHeight),
						ViewboxUnits = BrushMappingMode.Absolute,
						Stretch = stretch,
						TileMode = TileMode.None
					};
				}
				catch
				{
					bg = null;
				}
			}
		}
		surface.Background = bg ?? SolidFill(o, light);
		surface.CornerRadius = new CornerRadius(o.CornerRadius);
		Color bc = ParseColor(o.BorderColor, light ? "#33000000" : "#33FFFFFF");
		bc.A = (byte)Math.Round(255 * Math.Max(0.0, Math.Min(1.0, o.BorderOpacity)));
		surface.BorderBrush = new SolidColorBrush(bc);
		surface.BorderThickness = new Thickness(1);
		double shadow = Math.Max(0.0, Math.Min(1.0, o.ShadowStrength));
		surface.Effect = Shadow(o);
	}

	// 阴影单独抽出：过渡动画期间临时摘除 Surface.Effect（DropShadowEffect 每帧都要
	// 把子树光栅化进中间缓冲再模糊——它是 WPF 动画卡顿的经典来源），动画结束再装回。
	public static Effect Shadow(IslandOptions o)
	{
		double shadow = o == null ? 0.0 : Math.Max(0.0, Math.Min(1.0, o.ShadowStrength));
		if (shadow <= 0.01)
		{
			return null;
		}
		return new DropShadowEffect
		{
			BlurRadius = 10 + 22 * shadow,
			ShadowDepth = 2 + 6 * shadow,
			Opacity = 0.18 + 0.22 * shadow,
			Color = Colors.Black
		};
	}

	// 灵动岛只用一层扁平底色：黑白配色 + 不透明度，不叠渐变/噪点。
	// 模糊由窗口背景截图层提供（“模糊程度”控制其 BlurEffect 半径）。
	private static Brush SolidFill(IslandOptions o, bool light)
	{
		Color c = light ? Color.FromRgb(234, 237, 244) : Color.FromRgb(24, 26, 32);
		if (TryParseColor(o.BackgroundColor, out Color custom))
		{
			c = custom; // 兼容旧预设皮肤写入的背景色
		}
		c.A = (byte)Math.Round(Math.Max(0.0, Math.Min(100.0, o.OpacityPercent)) * 255.0 / 100.0);
		return new SolidColorBrush(c);
	}

	private static bool TryParseColor(string hex, out Color c)
	{
		try
		{
			string s = (hex ?? "").Trim();
			if (s.Length == 7 && s[0] == '#')
			{
				c = Color.FromRgb(Convert.ToByte(s.Substring(1, 2), 16), Convert.ToByte(s.Substring(3, 2), 16), Convert.ToByte(s.Substring(5, 2), 16));
				return true;
			}
		}
		catch
		{
		}
		c = default;
		return false;
	}

	public static Color ParseColor(string hex, string fallback)
	{
		try
		{
			string h = (hex ?? "").Trim();
			if (h.Length == 0)
			{
				h = fallback;
			}
			if (h.StartsWith("#"))
			{
				h = h.Substring(1);
			}
			if (h.Length == 8)
			{
				return Color.FromArgb(Convert.ToByte(h.Substring(0, 2), 16), Convert.ToByte(h.Substring(2, 2), 16), Convert.ToByte(h.Substring(4, 2), 16), Convert.ToByte(h.Substring(6, 2), 16));
			}
			if (h.Length == 6)
			{
				return Color.FromRgb(Convert.ToByte(h.Substring(0, 2), 16), Convert.ToByte(h.Substring(2, 2), 16), Convert.ToByte(h.Substring(4, 2), 16));
			}
		}
		catch
		{
		}
		return ParseColor(fallback, "#FFFFFFFF");
	}

	public static Brush TextBrush(bool light)
	{
		return new SolidColorBrush(light ? Color.FromArgb(230, 15, 15, 20) : Color.FromArgb(235, 245, 245, 250));
	}

	public static Brush MutedTextBrush(bool light)
	{
		return new SolidColorBrush(light ? Color.FromArgb(170, 60, 60, 70) : Color.FromArgb(160, 190, 195, 210));
	}

	// 预设皮肤主题色是否算“跟随主题”：预览与窗口都只用 options.Light（由调用方传入面板明暗）。
	// FollowSystemTheme 语义 = 跟随全局面板主题（dock/大面板的“使用浅色玻璃”），而非 Windows 系统主题。
	public static bool EffectiveLight(IslandOptions o, bool panelLight)
	{
		return o.FollowSystemTheme ? panelLight : o.Light;
	}

	// 四角独立圆角的圆角矩形几何（用于窗口内容裁切与背景层）。顶部吸附模式下
	// 顶角为 0、底角大圆角，呈现“两侧波浪滑下、连接底部”的悬挂横幅形态。
	public static Geometry RoundedRect(double w, double h, double tl, double tr, double br, double bl)
	{
		try
		{
			w = Math.Max(1.0, w);
			h = Math.Max(1.0, h);
			double maxR = Math.Min(w, h) / 2.0;
			tl = Math.Max(0.0, Math.Min(tl, maxR));
			tr = Math.Max(0.0, Math.Min(tr, maxR));
			br = Math.Max(0.0, Math.Min(br, maxR));
			bl = Math.Max(0.0, Math.Min(bl, maxR));
			StreamGeometry g = new StreamGeometry();
			using (StreamGeometryContext c = g.Open())
			{
				c.BeginFigure(new Point(tl, 0.0), true, true);
				c.LineTo(new Point(w - tr, 0.0), true, false);
				if (tr > 0.5) { c.ArcTo(new Point(w, tr), new Size(tr, tr), 0.0, false, SweepDirection.Clockwise, true, false); }
				else { c.LineTo(new Point(w, 0.0), true, false); }
				c.LineTo(new Point(w, h - br), true, false);
				if (br > 0.5) { c.ArcTo(new Point(w - br, h), new Size(br, br), 0.0, false, SweepDirection.Clockwise, true, false); }
				else { c.LineTo(new Point(w, h), true, false); }
				c.LineTo(new Point(bl, h), true, false);
				if (bl > 0.5) { c.ArcTo(new Point(0.0, h - bl), new Size(bl, bl), 0.0, false, SweepDirection.Clockwise, true, false); }
				else { c.LineTo(new Point(0.0, h), true, false); }
				c.LineTo(new Point(0.0, tl), true, false);
				if (tl > 0.5) { c.ArcTo(new Point(tl, 0.0), new Size(tl, tl), 0.0, false, SweepDirection.Clockwise, true, false); }
				else { c.LineTo(new Point(0.0, 0.0), true, false); }
			}
			g.Freeze();
			return g;
		}
		catch
		{
			return new RectangleGeometry(new Rect(0, 0, Math.Max(1.0, w), Math.Max(1.0, h)));
		}
	}
}

// 系统明暗主题跟随：注册表读取 + 系统主题变化事件。
public static class IslandTheme
{
	private static volatile bool systemLight = true;
	private static bool hooked;
	private static IntPtr hook;

	public static bool SystemLight => systemLight;

	public static event Action Changed;

	public static void Start()
	{
		if (hooked)
		{
			return;
		}
		hooked = true;
		ReadSystemTheme();
		// Windows 10/11 设置里切换深/浅色只广播 WM_SETTINGCHANGE("ImmersiveColorSet")，
		// SystemEvents.UserPreferenceChanged 不会触发；这里用消息窗口监听 WM_SETTINGCHANGE(0x001A)
		// 与 WM_DWMCOLORIZATIONCOLORCHANGED(0x0320)，颜色值变化才发 Changed。
		SystemEvents.UserPreferenceChanged += OnUserPreference;
		try
		{
			System.Windows.Interop.HwndSourceParameters p = new System.Windows.Interop.HwndSourceParameters("IslandThemeListener")
			{
				Width = 0,
				Height = 0,
				PositionX = 0,
				PositionY = 0,
				ParentWindow = new IntPtr(-3), // HWND_MESSAGE：消息窗口，不占任务栏
				WindowStyle = 0,
				ExtendedWindowStyle = 0
			};
			messageSource = new System.Windows.Interop.HwndSource(p);
			messageSource.AddHook(MessageHook);
		}
		catch
		{
		}
	}

	public static void Stop()
	{
		SystemEvents.UserPreferenceChanged -= OnUserPreference;
		if (messageSource != null)
		{
			try
			{
				messageSource.RemoveHook(MessageHook);
				messageSource.Dispose();
			}
			catch
			{
			}
			messageSource = null;
		}
		hooked = false;
		hook = IntPtr.Zero;
	}

	private static System.Windows.Interop.HwndSource messageSource;

	private static IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == 0x001A || msg == 0x0320)
		{
			ReadSystemTheme();
		}
		return IntPtr.Zero;
	}

	private static void OnUserPreference(object sender, UserPreferenceChangedEventArgs e)
	{
		ReadSystemTheme();
	}

	public static void ReadSystemTheme()
	{
		try
		{
			bool light = true;
			using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
			{
				object v = key?.GetValue("AppsUseLightTheme");
				if (v != null)
				{
					light = Convert.ToInt32(v) != 0;
				}
			}
			if (light != systemLight)
			{
				systemLight = light;
				try
				{
					System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(delegate
					{
						Changed?.Invoke();
					}));
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}
}
