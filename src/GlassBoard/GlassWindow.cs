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

	// 阴影/毛玻璃效果实例复用：每次打开面板不再重建 Effect（重建会强制整窗重新栅格化，造成首帧卡顿）
	private DropShadowEffect surfaceShadow;

	private System.Windows.Media.Effects.BlurEffect backdropBlur;

	private BitmapImage cachedBackdrop;

	private string backdropKey = "";

	private string materialSig = "";

	private double lastGlassPercent = -1.0;

	private bool glassApplied;

	protected bool MotionEnabled = true;

	// 主动隐藏的通行证：游戏模式等主动 Hide() 前先置位；外部广播（显示桌面等）引发的
	// WM_SHOWWINDOW(hide) 没有通行证一律拦截，防止 Dock/组件被系统藏掉。
	internal bool ExternalHideAllowed;

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
			if (this is DockWindow || this is WidgetWindow)
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
				if ((this is DockWindow || this is WidgetWindow) && msg == 24 && wp == IntPtr.Zero)
				{
					if (ExternalHideAllowed)
					{
						ExternalHideAllowed = false;
					}
					else
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

	public void Backdrop(bool light)
	{
		LightTheme = light;
		SetGlassOpacity(GlassPercent);
		Surface.BorderBrush = ((GlassPercent == 0.0) ? ((System.Windows.Media.Brush)System.Windows.Media.Brushes.Transparent) : ((System.Windows.Media.Brush)new LinearGradientBrush(System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 145.0 * EdgeStrength), byte.MaxValue, byte.MaxValue, byte.MaxValue), System.Windows.Media.Color.FromArgb((byte)Math.Min(255.0, 35.0 * EdgeStrength), byte.MaxValue, byte.MaxValue, byte.MaxValue), 90.0)));
		if (IsVisible)
		{
			RefreshBackdropStyle();
			return;
		}
		(from b in Root.Children.OfType<Border>()
			where b.Tag as string == "backdrop"
			select b).ToList().ForEach((Border b) =>
		{
			Root.Children.Remove(b);
		});
		if (Width < 40.0 || Height < 40.0 || GlassPercent == 0.0)
		{
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
		if (!Native.GetWindowRect(hwnd, out Native.RECT rect) || rect.Right - rect.Left < 56 || rect.Bottom - rect.Top < 56)
		{
			return;
		}
		// 物理像素坐标捕获：不再用 TransformToDevice（主屏 DPI 矩阵在混合 DPI 多显示器下算错区域）。
		// 缓存键用物理矩形 + DPI，显示器/DPI/壁纸变化后自然失效重捕。
		int dpi = Native.DpiOf(hwnd);
		string key = rect.Left + "_" + rect.Top + "_" + rect.Right + "_" + rect.Bottom + "_" + dpi + "_" + (LightTheme ? 1 : 0);
		if (cachedBackdrop != null && backdropKey == key)
		{
			AddBackdrop(cachedBackdrop);
			return;
		}
		CaptureRegion(rect, dpi);
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

	// 捕获指定物理矩形区域作为毛玻璃底图（1/8 缩放 PNG 往返，写入 cachedBackdrop/backdropKey 并挂载）。
	private void CaptureRegion(Native.RECT rect, int dpi)
	{
		long captureStart = Environment.TickCount;
		try
		{
			int margin = (int)Math.Round(14.0 * dpi / 96.0);
			int sourceX = rect.Left + margin;
			int sourceY = rect.Top + margin;
			int num = Math.Max(1, rect.Right - rect.Left - 2 * margin);
			int num2 = Math.Max(1, rect.Bottom - rect.Top - 2 * margin);
			using Bitmap bitmap = new Bitmap(num, num2);
			using (Graphics graphics = Graphics.FromImage(bitmap))
			{
				graphics.CopyFromScreen(sourceX, sourceY, 0, 0, new System.Drawing.Size(num, num2));
			}
			using Bitmap bitmap2 = new Bitmap(bitmap, Math.Max(1, num / 8), Math.Max(1, num2 / 8));
			using MemoryStream memoryStream = new MemoryStream();
			bitmap2.Save(memoryStream, ImageFormat.Png);
			memoryStream.Position = 0L;
			BitmapImage bitmapImage = new BitmapImage();
			bitmapImage.BeginInit();
			bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
			bitmapImage.StreamSource = memoryStream;
			bitmapImage.EndInit();
			bitmapImage.Freeze();
			cachedBackdrop = bitmapImage;
			backdropKey = rect.Left + "_" + rect.Top + "_" + rect.Right + "_" + rect.Bottom + "_" + dpi + "_" + (LightTheme ? 1 : 0);
			AddBackdrop(cachedBackdrop);
			if (RenderDiagnostics.Verbose)
			{
				Store.Trace("RENDER capture cls=" + GetType().Name + " rect=" + rect.Left + "," + rect.Top + " " + num + "x" + num2 + " ms=" + (Environment.TickCount - captureStart) + " rel=" + (Environment.TickCount - Program.ProcessStartTick));
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	private System.Windows.Threading.DispatcherTimer backdropRefreshTimer;

	// 壁纸/显示器/DPI 变化后的重捕调度：1.2 秒合并窗口，多个广播（切换壁纸会连发多条）只捕一次。
	public void ScheduleBackdropRefresh(bool light)
	{
		if (backdropRefreshTimer == null)
		{
			backdropRefreshTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(1200.0)
			};
			backdropRefreshTimer.Tick += delegate
			{
				backdropRefreshTimer.Stop();
				RecaptureBackdrop(LightTheme);
			};
		}
		backdropRefreshTimer.Stop();
		backdropRefreshTimer.Start();
	}

	// 可见状态下重捕：临时把窗口移出屏幕（避免 CopyFromScreen 捕到自身形成反馈），
	// 按原物理矩形捕获后再移回并刷新画刷。仅在壁纸/显示变化这类低频事件触发，不会高频闪动。
	public void RecaptureBackdrop(bool light)
	{
		if (Width < 40.0 || Height < 40.0 || GlassPercent == 0.0)
		{
			cachedBackdrop = null;
			backdropKey = "";
			return;
		}
		if (!IsVisible)
		{
			cachedBackdrop = null;
			backdropKey = "";
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
			double oldLeft = Left;
			double oldTop = Top;
			Left = -20000.0;
			Top = -20000.0;
			cachedBackdrop = null;
			backdropKey = "";
			RemoveBackdropBorders();
			CaptureRegion(rect, dpi);
			Left = oldLeft;
			Top = oldTop;
			RefreshBackdropStyle();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	private void AddBackdrop(BitmapImage bitmapImage)
	{
		if (!SlowRender && backdropBlur == null)
		{
			backdropBlur = new BlurEffect
			{
				Radius = MaterialBlur,
				RenderingBias = RenderingBias.Performance
			};
		}
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
		border.Clip = new RectangleGeometry(new Rect(0.0, 0.0, Width - 28.0, Height - 28.0), Surface.CornerRadius.TopLeft, Surface.CornerRadius.TopLeft);
		border.Effect = backdropBlur;
		Border element = new Border
		{
			Tag = "backdrop",
			Clip = border.Clip,
			Child = border,
			Opacity = GlassPercent / 100.0
		};
		Root.Children.Insert(0, element);
	}

	private void RefreshBackdropStyle()
	{
		if (!SlowRender && backdropBlur == null)
		{
			backdropBlur = new BlurEffect
			{
				Radius = MaterialBlur,
				RenderingBias = RenderingBias.Performance
			};
		}
		foreach (Border item in from b in Root.Children.OfType<Border>()
			where b.Tag as string == "backdrop"
			select b)
		{
			RectangleGeometry clip = (RectangleGeometry)(item.Clip = new RectangleGeometry(new Rect(0.0, 0.0, Math.Max(1.0, Width - 28.0), Math.Max(1.0, Height - 28.0)), Surface.CornerRadius.TopLeft, Surface.CornerRadius.TopLeft));
			if (item.Child is Border border)
			{
				border.Clip = clip;
				border.CornerRadius = Surface.CornerRadius;
				border.Opacity = (LightTheme ? 0.8 : 0.66);
				border.Effect = backdropBlur;
			}
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
		if (glassApplied && sig == materialSig && Math.Abs(GlassPercent - lastGlassPercent) < 0.001)
		{
			return;
		}
		lastGlassPercent = GlassPercent;
		materialSig = sig;
		glassApplied = true;
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
		Grid grid = (Grid)(border.Child = new Grid());
		border.Opacity = GlassPercent / 100.0;
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
