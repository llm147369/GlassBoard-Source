using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

// 用户定制图标：启动台 PNG（原样显示）与大面板设置 SVG（解析为矢量路径，跟随主题着色）。
// 两个文件都以嵌入资源形式内置（Assets\launchpad.png / Assets\settings.svg），无需外部文件。
public static class CustomIcons
{
	private static BitmapImage launchpad;
	private static BitmapImage running;
	private static GeometryGroup settingsGeometry;
	private static readonly object Sync = new object();

	public static ImageSource Launchpad()
	{
		if (launchpad != null)
		{
			return launchpad;
		}
		lock (Sync)
		{
			if (launchpad == null)
			{
				try
				{
					using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GlassBoard.Assets.launchpad.png"))
					{
						if (stream == null)
						{
							return null;
						}
						BitmapImage bitmap = new BitmapImage();
						bitmap.BeginInit();
						bitmap.CacheOption = BitmapCacheOption.OnLoad;
						bitmap.StreamSource = stream;
						bitmap.EndInit();
						bitmap.Freeze();
						launchpad = bitmap;
					}
				}
				catch
				{
					return null;
				}
			}
		}
		return launchpad;
	}

	// 运行列表入口默认图标（用户提供的 2.5d 图标）：嵌入资源加载，无外部依赖。
	// 切换图标风格包不影响它（它不属于任何风格包）；用户主动更换/上传后以用户覆盖为准。
	public static ImageSource Running()
	{
		if (running != null)
		{
			return running;
		}
		lock (Sync)
		{
			if (running == null)
			{
				try
				{
					using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GlassBoard.Assets.running.png"))
					{
						if (stream == null)
						{
							return null;
						}
						BitmapImage bitmap = new BitmapImage();
						bitmap.BeginInit();
						bitmap.CacheOption = BitmapCacheOption.OnLoad;
						bitmap.StreamSource = stream;
						bitmap.EndInit();
						bitmap.Freeze();
						running = bitmap;
					}
				}
				catch
				{
					return null;
				}
			}
		}
		return running;
	}

	// 大面板右上角设置图标：解析内嵌 SVG 的 <path d="..."> 为 WPF 几何，按主题色填充。
	// 几何缓存，元素每次新建（避免同一元素被多个父容器复用，且画刷跟随当前主题）。
	public static FrameworkElement Settings(double size, Brush brush)
	{
		lock (Sync)
		{
			if (settingsGeometry == null)
			{
				GeometryGroup group = new GeometryGroup
				{
					FillRule = FillRule.EvenOdd
				};
				string svg = ReadText("GlassBoard.Assets.settings.svg");
				if (!string.IsNullOrEmpty(svg))
				{
					foreach (Match match in Regex.Matches(svg, "<path[^>]*\\bd=\"([^\"]*)\"", RegexOptions.IgnoreCase))
					{
						try
						{
							Geometry geometry = Geometry.Parse(match.Groups[1].Value);
							geometry.Freeze();
							group.Children.Add(geometry);
						}
						catch
						{
							// 忽略无法解析的路径段，尽量保留其余部分
						}
					}
				}
				group.Freeze();
				settingsGeometry = group;
			}
			return new System.Windows.Shapes.Path
			{
				Data = settingsGeometry,
				Fill = brush,
				Stretch = Stretch.Uniform,
				Width = size,
				Height = size
			};
		}
	}

	private static string ReadText(string resource)
	{
		try
		{
			using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
			{
				if (stream == null)
				{
					return "";
				}
				using (StreamReader reader = new StreamReader(stream, System.Text.Encoding.UTF8))
				{
					return reader.ReadToEnd();
				}
			}
		}
		catch
		{
			return "";
		}
	}
}
