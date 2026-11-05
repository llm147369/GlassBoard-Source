using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace GlassBoard;

public class GlassWindow : Window
{
	protected Grid Root = new Grid();

	protected Border Surface = new Border();

	protected Grid ContentGrid = new Grid();

	protected bool LightTheme;

	protected double GlassPercent = 72.0;

	// 软件渲染（RenderCapability.Tier < 2）时整窗阴影/背景模糊极贵，自动降级保证动画流畅
	private static readonly bool SlowRender = (RenderCapability.Tier >> 16) < 2;

	// 阴影效果实例复用：每次打开面板不再重建 Effect（重建会强制整窗重新栅格化，造成首帧卡顿）
	private DropShadowEffect surfaceShadow;

	protected BitmapImage cachedBackdrop;

	protected string backdropKey = "";

	private string materialSig = "";

	private double lastGlassPercent = -1.0;

	private bool glassApplied;

	protected bool MotionEnabled = true;

	// 主动隐藏的通行证：游戏模式等主动 Hide() 前先置位；外部广播（显示桌面等）引发的
	// WM_SHOWWINDOW(hide) 没有通行证一律拦截，防止 Dock/组件被系统藏掉。
	internal bool ExternalHideAllowed;

	// 窗口正在关闭（Close 流程会先走 WM_SHOWWINDOW(hide)）；此状态下外部隐藏拦截放行，
	// 否则控制中心这类“关闭即销毁”的面板会关不掉。
	private bool windowClosing;

	internal void HideWithConsent()
	{
		ExternalHideAllowed = true;
		Hide();
	}

	protected double MotionLevel = 1.0;

	protected double BlurRadius = 18.0;

	protected double EdgeStrength = 1.0;

	protected MaterialOptions CurrentMaterial = new MaterialOptions();

	protected int AccentIndex;

	protected double MaterialBlur
	{
		get
		{
			if (CurrentMaterial.Kind != 1)
			{
				if (CurrentMaterial.Kind != 2)
				{
					return BlurRadius;
				}
				return Math.Min(40.0, BlurRadius * 1.3);
			}
			return BlurRadius * 0.25;
		}
	}

	protected System.Windows.Media.Brush TextBrush => new SolidColorBrush(LightTheme ? System.Windows.Media.Color.FromRgb(25, 28, 34) : System.Windows.Media.Color.FromRgb(247, 247, 249));

	protected System.Windows.Media.Brush MutedBrush => new SolidColorBrush(LightTheme ? System.Windows.Media.Color.FromRgb(65, 69, 77) : System.Windows.Media.Color.FromRgb(184, 188, 198));

	protected void ConfigureGlass(DockOptions options, MaterialOptions material = null)
	{
		BlurRadius = options.BlurRadius;
		EdgeStrength = options.EdgeStrength;
		AccentIndex = options.Accent;
		CurrentMaterial = (material ?? options.Material ?? new MaterialOptions()).Copy();
	}

	public void SetMaterial(MaterialOptions material)
	{
		CurrentMaterial = material.Copy();
		SetGlassOpacity(GlassPercent);
		RefreshBackdropStyle();
		// 模糊程度变化 → 已烘焙的底图需要按新半径重捕重烘（旧实现只刷样式，
		// 已有底图上调“模糊程度”完全无效）。
		if (IsVisible)
		{
			ScheduleBackdropRefresh(LightTheme);
		}
	}

	protected static ControlTemplate GripTemplate(bool corner = false)
	{
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(corner ? typeof(TextBlock) : typeof(Border));
		if (corner)
		{
			frameworkElementFactory.SetValue(TextBlock.TextProperty, "◢");
			frameworkElementFactory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(135, 180, 195, 215)));
			frameworkElementFactory.SetValue(TextBlock.FontSizeProperty, 18.0);
		}
		else
		{
			frameworkElementFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3.0));
			frameworkElementFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 180, 195, 215)));
		}
		return new ControlTemplate(typeof(Thumb))
		{
			VisualTree = frameworkElementFactory
		};
	}

	public GlassWindow(double radius)
	{
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Closing += (object s, CancelEventArgs e) => { windowClosing = true; };
		// 透明窗口下 WPF 禁用 ClearType（退化灰度抗锯齿）：全窗口切到 Display 像素对齐
		// 文本模式 + 布局整数舍入，小字号文字不发虚、无像素锯齿感。
		TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
		UseLayoutRounding = true;
		Background = System.Windows.Media.Brushes.Transparent;
		ResizeMode = ResizeMode.NoResize;
		ShowInTaskbar = false;
		Topmost = true;
		Content = Root;
		Root.Margin = new Thickness(14.0);
		Surface.CornerRadius = new CornerRadius(radius);
		Surface.BorderThickness = new Thickness(1.0);
		surfaceShadow = new DropShadowEffect
		{
			BlurRadius = 25.0,
			ShadowDepth = 7.0,
			Opacity = 0.28,
			Color = Colors.Black
		};
		Surface.Effect = surfaceShadow;
		Root.Children.Add(Surface);
		Root.Children.Add(ContentGrid);
		SourceInitialized += (object s, EventArgs e) =>
		{
			if (this is DockWindow || this is WidgetWindow || this is BoardWindow || this is ControlCenterWindow)
			{
				DesktopWindowPolicy.Apply(this);
			}
			// 窗口生命周期诊断：记录首次句柄创建的相对时刻，用于对照黑屏发生前的操作时序。
			Store.Trace("WINDOW init cls=" + GetType().Name + " ms=" + (Environment.TickCount - Program.ProcessStartTick));
			try
			{
				if (!Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0u))
				{
					Store.Log(new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowDisplayAffinity: allow desktop capture"));
				}
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
			// 屏蔽「显示桌面」的最小化广播：Dock 与组件保持可见（与系统任务栏同等待遇）。
			// 不校验 lParam：键盘触发（Win+D / Win+M）时 lParam=0，点击任务栏右侧
			// 「显示桌面」竖条触发时 lParam 为鼠标坐标——两种都要拦，且本窗口没有
			// 最小化按钮，任何外部 SC_MINIMIZE 都是广播而非用户主动操作。
			HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
			{
				if (msg == 274 && ((wp.ToInt64() & 0xFFF0) == 0xF020))
				{
					handled = true;
					return IntPtr.Zero;
				}
				// 拦截外部隐藏：显示桌面若以 ShowWindow(SW_HIDE) 方式收起工具窗口
				// （toolwindow 不参与最小化流程），会在 WM_SHOWWINDOW(wParam=0) 出现。
				// 只有带通行证的主动 Hide()（游戏模式等）放行，其余一律拦下。
				if (msg == 24 && wp == IntPtr.Zero)
				{
					if (ExternalHideAllowed)
					{
						ExternalHideAllowed = false;
					}
					else if (!windowClosing)
					{
						handled = true;
						return IntPtr.Zero;
					}
				}
				if (this is WidgetWindow widget && msg == 22 && wp != IntPtr.Zero)
				{
					widget.MarkSessionEnding();
				}
				// 壁纸切换（SPI_SETDESKWALLPAPER 广播，含 Wallpaper Engine 切换场景/壁纸）、
				// 显示器变化、DPI 变化 → 背景缓存失效并延迟重捕，避免 Dock 上残留旧壁纸底图。
				if (msg == 0x001A && wp.ToInt64() == 0x14)
				{
					Store.Trace("RENDER wallpaper change → backdrop refresh");
					ScheduleBackdropRefresh(LightTheme);
				}
				else if (msg == 0x007E)
				{
					Store.Trace("RENDER display change → backdrop refresh");
					ScheduleBackdropRefresh(LightTheme);
				}
				else if (msg == 0x02E0)
				{
					Store.Trace("RENDER dpi change → backdrop refresh");
					ScheduleBackdropRefresh(LightTheme);
				}
				return IntPtr.Zero;
			});
		};
		StateChanged += (object s, EventArgs e) =>
		{
			// 兜底：若显示桌面仍以其他途径最小化本窗口，立即还原（Dock/组件无最小化需求）。
			// 注意不能加 IsVisible 判断——WPF 中最小化状态的窗口 IsVisible 恒为 false，
			// 加了该条件这段兜底就是死代码（正是此前 Win+D 仍然失效的原因）。
			if (WindowState == WindowState.Minimized)
			{
				WindowState = WindowState.Normal;
			}
		};
	}

	protected Button Button(string label, RoutedEventHandler click, double width = 80.0)
	{
		Button b = new Button
		{
			Content = label,
			Width = width,
			Height = 36.0,
			Margin = new Thickness(4.0),
			Foreground = TextBrush,
			Cursor = Cursors.Hand
		};
		FrameworkElementFactory frameworkElementFactory = new FrameworkElementFactory(typeof(Border));
		frameworkElementFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(12.0));
		frameworkElementFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(LightTheme ? 38 : 25), byte.MaxValue, byte.MaxValue, byte.MaxValue)));
		frameworkElementFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(42, byte.MaxValue, byte.MaxValue, byte.MaxValue)));
		frameworkElementFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
		FrameworkElementFactory frameworkElementFactory2 = new FrameworkElementFactory(typeof(ContentPresenter));
		frameworkElementFactory2.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		frameworkElementFactory2.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		frameworkElementFactory.AppendChild(frameworkElementFactory2);
		b.Template = new ControlTemplate(typeof(Button))
		{
			VisualTree = frameworkElementFactory
		};
		b.MouseEnter += (object s, MouseEventArgs e) =>
		{
			if (MotionEnabled)
			{
				b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.78, TimeSpan.FromMilliseconds(120.0)));
			}
			else
			{
				b.Opacity = 0.78;
			}
		};
		b.MouseLeave += (object s, MouseEventArgs e) =>
		{
			if (MotionEnabled)
			{
				b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150.0)));
			}
			else
			{
				b.Opacity = 1.0;
			}
		};
		b.PreviewMouseLeftButtonDown += (object s, MouseButtonEventArgs e) =>
		{
			b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(MotionEnabled ? 65 : 0)));
		};
		b.PreviewMouseLeftButtonUp += (object s, MouseButtonEventArgs e) =>
		{
			b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(MotionEnabled ? 100 : 0)));
		};
		if (click != null)
		{
			b.Click += click;
		}
		return b;
	}

	public virtual void Backdrop(bool light)
	{
		LightTheme = light;
		SetGlassOpacity(GlassPercent);
		Surface.BorderBrush = ((GlassPercent == 0.0) ? ((System.Windows.Media.Brush)System.Windows.Media.Brushes.Transparent) : ((System.Windows.Media.Brush)new LinearGradientBrush(System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 145.0 * EdgeStrength), byte.MaxValue, byte.MaxValue, byte.MaxValue), System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 35.0 * EdgeStrength), byte.MaxValue, byte.MaxValue, byte.MaxValue), 90.0)));
		if (IsVisible)
		{
			// 可见窗口只在几何、主题或模糊参数稳定后更新底图，保留旧图直到新图就绪。
			bool keyMatches = false;
			try
			{
				keyMatches = cachedBackdrop != null && backdropKey == BackdropKeyOf(out Native.RECT _);
			}
			catch (InvalidOperationException)
			{
				// 窗口句柄已失效（关闭竞态）：放弃本次刷新。
				return;
			}
			if (!keyMatches)
			{
				ScheduleBackdropRefresh(light, 240);
				return;
			}
			RefreshBackdropStyle();
			return;
		}
		if (Width < 40.0 || Height < 40.0 || GlassPercent == 0.0)
		{
			ReleaseBackdrop();
			return;
		}
		IntPtr hwnd;
		try
		{
			hwnd = new WindowInteropHelper(this).EnsureHandle();
		}
		catch (InvalidOperationException)
		{
			// 窗口已被 Close()：WPF 禁止为已关闭窗口重建句柄，直接放弃本次背景捕获。
			// （控制中心/小组件关闭后仍可能有刷新路径触发 Backdrop，属正常竞态。）
			return;
		}
		// 物理像素坐标捕获：不再用 TransformToDevice（主屏 DPI 矩阵在混合 DPI 多显示器下算错区域）。
		// 缓存键用物理矩形 + DPI + 主题 + 烘焙模糊半径，位置/DPI/壁纸/模糊度变化后自然失效重捕。
		string key = BackdropKeyOf(out Native.RECT rect);
		if (key == "" || rect.Right - rect.Left < 56 || rect.Bottom - rect.Top < 56)
		{
			return;
		}
		if (cachedBackdrop != null && backdropKey == key)
		{
			AddBackdrop(cachedBackdrop);
			return;
		}
		CaptureRegion(rect, Native.DpiOf(hwnd));
	}

	// 缓存键：物理矩形 + DPI + 主题 + 烘焙模糊半径（模糊度变化必须重烘底图）。
	protected static string BackdropKeyOfRect(Native.RECT rect, int dpi, bool light, double blur)
	{
		return rect.Left + "_" + rect.Top + "_" + rect.Right + "_" + rect.Bottom + "_" + dpi + "_" + (light ? 1 : 0) + "_" + (int)Math.Round(blur);
	}

	private string BackdropKeyOf(out Native.RECT rect)
	{
		IntPtr hwnd = new WindowInteropHelper(this).Handle;
		if (!Native.GetWindowRect(hwnd, out rect))
		{
			rect = default(Native.RECT);
			return "";
		}
		return BackdropKeyOfRect(rect, Native.DpiOf(hwnd), LightTheme, MaterialBlur);
	}

	private void RemoveBackdropBorders()
	{
		(from b in Root.Children.OfType<Border>()
			where b.Tag as string == "backdrop"
			select b).ToList().ForEach((Border b) =>
		{
			Root.Children.Remove(b);
		});
	}

	// 从壁纸文件或专用壁纸层读取指定区域。失败时不替换缓存或当前画面。
	private bool CaptureRegion(Native.RECT rect, int dpi)
	{
		long captureStart = Environment.TickCount;
		try
		{
			// 捕获源区与显示裁剪统一按 Root.Margin 换算（灵动岛吸附模式 Margin=(14,0,14,14)），
			// 修复硬编码四周 14px 造成的底图整体错位/边缘无底图。
			int ml = (int)Math.Round(Root.Margin.Left * dpi / 96.0);
			int mt = (int)Math.Round(Root.Margin.Top * dpi / 96.0);
			int mr = (int)Math.Round(Root.Margin.Right * dpi / 96.0);
			int mb = (int)Math.Round(Root.Margin.Bottom * dpi / 96.0);
			int num = Math.Max(1, rect.Right - rect.Left - ml - mr);
			int num2 = Math.Max(1, rect.Bottom - rect.Top - mt - mb);
			Native.RECT src = new Native.RECT
			{
				Left = rect.Left + ml,
				Top = rect.Top + mt,
				Right = rect.Left + ml + num,
				Bottom = rect.Top + mt + num2
			};
			Bitmap bitmap = GlassCapture.Acquire(src, out bool fromFile);
			if (bitmap == null)
			{
				return false;
			}
			using (bitmap)
			{
				if (!fromFile && IsBlackFrame(bitmap))
				{
					// 黑帧：壁纸层正处于暂停/过渡（开始菜单、搜索打开时常见）。
					// 不替换现有底图，延迟重试数次；重试耗尽则放弃本次刷新。
					backdropBlackRetries++;
					if (backdropBlackRetries < 4)
					{
						Store.Trace("RENDER capture black → retry #" + backdropBlackRetries + " cls=" + GetType().Name);
						ScheduleBackdropRefresh(LightTheme);
						return false;
					}
					backdropBlackRetries = 0;
					Store.Trace("RENDER capture black → skip (keep current) cls=" + GetType().Name);
					return false;
				}
				backdropBlackRetries = 0;
				cachedBackdrop = GlassMaterials.Bake(bitmap, MaterialBlur, MaterialBlur / 8.0);
				backdropKey = BackdropKeyOfRect(rect, dpi, LightTheme, MaterialBlur);
				// 不先移除旧层：AddBackdrop 优先原地替换现有 ImageBrush（去掉先删后加造成的闪帧），
				// 只有图层缺失时才新建。
				AddBackdrop(cachedBackdrop);
				if (RenderDiagnostics.Verbose)
				{
					Store.Trace("RENDER capture cls=" + GetType().Name + " rect=" + rect.Left + "," + rect.Top + " " + num + "x" + num2 + " ms=" + (Environment.TickCount - captureStart) + " rel=" + (Environment.TickCount - Program.ProcessStartTick));
				}
				return true;
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			return false;
		}
	}

	private System.Windows.Threading.DispatcherTimer backdropRefreshTimer;

	private int backdropBlackRetries;

	// 均匀采样判定近黑帧：开始菜单/搜索打开期间（尤其 Wallpaper Engine 暂停渲染、
	// DWM 过渡瞬间）壁纸层常渲染成纯黑/近黑区域，直接采纳会把 Dock/面板涂黑。
	// 阈值 4：只拒真正纯黑（显示器关闭/锁屏），深色壁纸（实测亮度≈6）必须放行，
	// 否则深色壁纸区域永远不更新底图。
	private static bool IsBlackFrame(Bitmap bitmap)
	{
		int num = bitmap.Width - 1;
		int num2 = bitmap.Height - 1;
		for (int i = 0; i < 8; i++)
		{
			for (int j = 0; j < 8; j++)
			{
				System.Drawing.Color color = bitmap.GetPixel(num * i / 7, num2 * j / 7);
				if (color.R >= 4 || color.G >= 4 || color.B >= 4)
				{
					return false;
				}
			}
		}
		return true;
	}

	// 壁纸/显示器/DPI 变化后的重捕调度：合并连续广播，保持现有底图直到新图就绪。
	public void ScheduleBackdropRefresh(bool light, int delayMs = 1200)
	{
		if (backdropRefreshTimer == null)
		{
			backdropRefreshTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs))
			};
			backdropRefreshTimer.Tick += delegate
			{
				backdropRefreshTimer.Stop();
				RecaptureBackdrop(LightTheme);
			};
		}
		backdropRefreshTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
		backdropRefreshTimer.Stop();
		backdropRefreshTimer.Start();
	}

	// 可见状态下只尝试干净壁纸源。动态壁纸或 API 不可用时保留旧底图，绝不移窗或截取屏幕。
	public virtual void RecaptureBackdrop(bool light)
	{
		if (Width < 40.0 || Height < 40.0 || GlassPercent == 0.0)
		{
			cachedBackdrop = null;
			backdropKey = "";
			ReleaseBackdrop();
			return;
		}
		if (!IsVisible)
		{
			Backdrop(light);
			return;
		}
		try
		{
			IntPtr hwnd = new WindowInteropHelper(this).Handle;
			if (!Native.GetWindowRect(hwnd, out Native.RECT rect) || rect.Right - rect.Left < 56 || rect.Bottom - rect.Top < 56)
			{
				return;
			}
			int dpi = Native.DpiOf(hwnd);
			CaptureRegion(rect, dpi);
			RefreshBackdropStyle();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	private void AddBackdrop(BitmapImage bitmapImage)
	{
		Border element = Root.Children.OfType<Border>().FirstOrDefault((Border b) => b.Tag as string == "backdrop" && b.Child is Border);
		if (element != null && element.Child is Border existing)
		{
			Geometry existingClip = BackdropClip();
			existing.Background = new ImageBrush(bitmapImage) { Stretch = Stretch.Fill };
			existing.CornerRadius = Surface.CornerRadius;
			existing.Opacity = LightTheme ? 0.8 : 0.66;
			existing.Clip = existingClip;
			element.Clip = existingClip;
			element.Opacity = GlassPercent / 100.0;
			foreach (Border duplicate in Root.Children.OfType<Border>().Where((Border b) => b.Tag as string == "backdrop" && b != element).ToList())
			{
				Root.Children.Remove(duplicate);
			}
			return;
		}
		RemoveBackdropBorders();
		Border border = new Border
		{
			Tag = "backdrop",
			CornerRadius = Surface.CornerRadius,
			Background = new ImageBrush(bitmapImage)
			{
				Stretch = Stretch.Fill
			},
			Opacity = (LightTheme ? 0.8 : 0.66)
		};
		// 模糊已烘进底图（见 CaptureRegion），运行时不再挂 BlurEffect。
		border.Clip = BackdropClip();
		Border wrapper = new Border
		{
			Tag = "backdrop",
			Clip = border.Clip,
			Child = border,
			Opacity = GlassPercent / 100.0
		};
		Root.Children.Insert(0, wrapper);
	}

	// 底图/材质层统一的显示裁剪区：与捕获源区同源（Root.Margin 内容区），
	// 四角独立圆角（灵动岛吸附模式上下圆角不同）。
	protected Geometry BackdropClip()
	{
		try
		{
			double w = Math.Max(1.0, Width - Root.Margin.Left - Root.Margin.Right);
			double h = Math.Max(1.0, Height - Root.Margin.Top - Root.Margin.Bottom);
			if (double.IsNaN(w) || double.IsInfinity(w) || double.IsNaN(h) || double.IsInfinity(h) || w <= 0.0 || h <= 0.0)
			{
				return null;
			}
			CornerRadius c = Surface.CornerRadius;
			return IslandChrome.RoundedRect(w, h, c.TopLeft, c.TopRight, c.BottomRight, c.BottomLeft);
		}
		catch
		{
			return null;
		}
	}

	protected void RefreshBackdropStyle()
	{
		Geometry clip = BackdropClip();
		foreach (Border item in from b in Root.Children.OfType<Border>()
			where b.Tag as string == "backdrop"
			select b)
		{
			item.Clip = clip;
			if (item.Child is Border border)
			{
				border.Clip = clip;
				border.CornerRadius = Surface.CornerRadius;
				border.Opacity = (LightTheme ? 0.8 : 0.66);
			}
		}
		// 材质层（gloss/grain）同步裁剪：Border 的 CornerRadius 不会裁剪其背景内容，
		// 圆角外的直角纹理残影也是"污点"来源之一。
		Border mat = Root.Children.OfType<Border>().FirstOrDefault((Border b) => b.Tag as string == "material");
		if (mat != null)
		{
			mat.Clip = BackdropClip();
		}
	}

	protected void ReleaseBackdrop()
	{
		(from b in Root.Children.OfType<Border>()
			where b.Tag as string == "backdrop"
			select b).ToList().ForEach((Border b) =>
		{
			Root.Children.Remove(b);
		});
	}

	public void SetGlassOpacity(double percent)
	{
		GlassPercent = LayoutRules.GlassOpacity(percent);
		string sig = (LightTheme ? 1 : 0) + "|" + CurrentMaterial.Kind + "|" + CurrentMaterial.Gloss + "|" + CurrentMaterial.Grain + "|" + CurrentMaterial.Tint + "|" + AccentIndex + "|" + CurrentMaterial.Shadow;
		bool materialChanged = !glassApplied || sig != materialSig;
		if (!materialChanged && Math.Abs(GlassPercent - lastGlassPercent) < 0.001)
		{
			return;
		}
		lastGlassPercent = GlassPercent;
		if (materialChanged)
		{
			materialSig = sig;
			glassApplied = true;
		}
		if (surfaceShadow == null)
		{
			surfaceShadow = new DropShadowEffect
			{
				BlurRadius = 25.0,
				ShadowDepth = 7.0,
				Opacity = 0.28,
				Color = Colors.Black
			};
		}
		Surface.Background = GlassMaterials.Fill(CurrentMaterial, LightTheme, GlassPercent, AccentIndex);
		Surface.BorderBrush = ((GlassPercent == 0.0) ? System.Windows.Media.Brushes.Transparent : Polish.Line(LightTheme));
		Surface.Effect = ((GlassPercent == 0.0 || !CurrentMaterial.Shadow || SlowRender) ? null : surfaceShadow);
		foreach (Border item in from b in Root.Children.OfType<Border>()
			where b.Tag as string == "backdrop"
			select b)
		{
			item.Opacity = GlassPercent / 100.0;
		}
		Border border = Root.Children.OfType<Border>().FirstOrDefault((Border b) => b.Tag as string == "material");
		if (border == null)
		{
			border = new Border
			{
				Tag = "material",
				IsHitTestVisible = false
			};
			Root.Children.Insert(Math.Min(1, Root.Children.Count), border);
		}
		border.CornerRadius = Surface.CornerRadius;
		// 材质层裁剪：圆角外的直角 gloss/grain 纹理残影是"污点"来源之一
		border.Clip = BackdropClip();
		border.Opacity = GlassPercent / 100.0;
		if (!materialChanged && border.Child != null)
		{
			return;
		}
		Grid grid = (Grid)(border.Child = new Grid());
		if (CurrentMaterial.Gloss > 0.0)
		{
			grid.Children.Add(new Border
			{
				CornerRadius = Surface.CornerRadius,
				Background = new LinearGradientBrush(System.Windows.Media.Color.FromArgb((byte)Math.Round(CurrentMaterial.Gloss * 90.0), byte.MaxValue, byte.MaxValue, byte.MaxValue), Colors.Transparent, 90.0)
			});
		}
		if (CurrentMaterial.Grain > 0.0)
		{
			grid.Children.Add(new Border
			{
				CornerRadius = Surface.CornerRadius,
				Background = new ImageBrush(GlassMaterials.Grain())
				{
					TileMode = TileMode.Tile,
					ViewportUnits = BrushMappingMode.Absolute,
					Viewport = new Rect(0.0, 0.0, 32.0, 32.0)
				},
				Opacity = CurrentMaterial.Grain
			});
		}
	}

	protected void AnimateOpen()
	{
		BeginAnimation(UIElement.OpacityProperty, null);
		Root.RenderTransform = Transform.Identity;
		Opacity = 1.0;
		if (!MotionEnabled)
		{
			return;
		}
		double level = Math.Max(0.15, MotionLevel);
		Root.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		ScaleTransform scaleTransform = new ScaleTransform(1.0 - 0.035 * level, 1.0 - 0.035 * level);
		TranslateTransform translateTransform = new TranslateTransform(0.0, 14.0 * level);
		TransformGroup transformGroup = new TransformGroup();
		transformGroup.Children.Add(scaleTransform);
		transformGroup.Children.Add(translateTransform);
		Root.RenderTransform = transformGroup;
		TimeSpan duration = TimeSpan.FromMilliseconds(260.0);
		CubicEase easing = new CubicEase
		{
			EasingMode = EasingMode.EaseOut
		};
		BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, duration)
		{
			EasingFunction = easing
		});
		DoubleAnimation scaleAnimation = new DoubleAnimation(1.0, duration)
		{
			EasingFunction = easing
		};
		scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
		scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
		DoubleAnimation liftAnimation = new DoubleAnimation(0.0, duration)
		{
			EasingFunction = easing
		};
		translateTransform.BeginAnimation(TranslateTransform.YProperty, liftAnimation);
	}
}
