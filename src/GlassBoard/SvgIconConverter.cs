using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace GlassBoard;

// 把矢量 SVG 图标栅格化为 PNG（图标库下载的多为 .svg，WPF 原生不能解码 SVG）
public static class SvgIconConverter
{
	public static string LastError;

	public static string TryConvert(string svgPath, int size = 128)
	{
		try
		{
			XDocument doc = XDocument.Load(svgPath);
			XElement root = doc.Root;
			if (root == null || root.Name.LocalName != "svg")
			{
				return null;
			}
			double vbX = 0.0;
			double vbY = 0.0;
			double vbW = 0.0;
			double vbH = 0.0;
			string viewBox = (string)root.Attribute("viewBox");
			if (!string.IsNullOrWhiteSpace(viewBox))
			{
				string[] parts = viewBox.Split(new char[3] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 4)
				{
					vbX = Parse(parts[0]);
					vbY = Parse(parts[1]);
					vbW = Parse(parts[2]);
					vbH = Parse(parts[3]);
				}
			}
			if (vbW <= 0.0 || vbH <= 0.0)
			{
				vbW = Parse((string)root.Attribute("width"));
				vbH = Parse((string)root.Attribute("height"));
			}
			if (vbW <= 0.0 || vbH <= 0.0)
			{
				return null;
			}
			double scale = Math.Min(size / vbW, size / vbH);
			double offsetX = (size - vbW * scale) / 2.0;
			double offsetY = (size - vbH * scale) / 2.0;
			Brush defaultFill = new SolidColorBrush(Color.FromRgb(60, 60, 60));
			defaultFill.Freeze();
			DrawingGroup group = new DrawingGroup();
			using (DrawingContext dc = group.Open())
			{
				DrawChildren(dc, root, null, vbX, vbY, scale, offsetX, offsetY, defaultFill);
			}
			DrawingVisual visual = new DrawingVisual();
			using (DrawingContext vc = visual.RenderOpen())
			{
				vc.DrawDrawing(group);
			}
			RenderTargetBitmap bitmap = new RenderTargetBitmap(size, size, 96.0, 96.0, PixelFormats.Pbgra32);
			bitmap.Render(visual);
			PngBitmapEncoder encoder = new PngBitmapEncoder();
			encoder.Frames.Add(BitmapFrame.Create(bitmap));
			string directory = Path.Combine(Store.Folder, "icons");
			Directory.CreateDirectory(directory);
			string outPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
			using FileStream stream = File.Create(outPath);
			encoder.Save(stream);
			return outPath;
		}
		catch (Exception ex)
		{
			LastError = ex.GetType().FullName + ": " + ex.Message;
			return null;
		}
	}

	private static double Parse(string value)
	{
		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
		{
			return result;
		}
		return 0.0;
	}

	private static void DrawChildren(DrawingContext dc, XElement parent, Transform parentTransform, double vbX, double vbY, double scale, double offsetX, double offsetY, Brush defaultFill)
	{
		foreach (XElement child in parent.Elements())
		{
			Transform transform = parentTransform;
			string transformAttr = (string)child.Attribute("transform");
			if (!string.IsNullOrWhiteSpace(transformAttr))
			{
				transform = Combine(transform, ParseTransform(transformAttr));
			}
			string name = child.Name.LocalName;
			if (name == "g")
			{
				DrawChildren(dc, child, transform, vbX, vbY, scale, offsetX, offsetY, defaultFill);
				continue;
			}
			Geometry geometry = ShapeGeometry(child);
			if (geometry == null)
			{
				continue;
			}
			// Geometry.Parse 返回冻结实例，设 Transform 前必须 Clone
			if (geometry.IsFrozen)
			{
				geometry = geometry.Clone();
			}
			TransformGroup group2 = new TransformGroup();
			if (transform != null)
			{
				group2.Children.Add(transform);
			}
			group2.Children.Add(new ScaleTransform(scale, scale));
			group2.Children.Add(new TranslateTransform(offsetX - vbX * scale, offsetY - vbY * scale));
			geometry.Transform = group2;
			geometry.Freeze();
			Brush fill = BrushFrom(child.Attribute("fill"), defaultFill);
			string strokeAttr = (string)child.Attribute("stroke");
			string strokeWidth = (string)child.Attribute("stroke-width");
			bool hasStroke = !string.IsNullOrWhiteSpace(strokeAttr) && !string.Equals(strokeAttr, "none", StringComparison.OrdinalIgnoreCase);
			if (fill != null)
			{
				dc.DrawGeometry(fill, null, geometry);
			}
			if (hasStroke)
			{
				Brush stroke = BrushFrom(child.Attribute("stroke"), defaultFill);
				double width = Math.Max(1.0, Parse(strokeWidth) * scale);
				Pen pen = new Pen(stroke, width)
				{
					StartLineCap = PenLineCap.Round,
					EndLineCap = PenLineCap.Round,
					LineJoin = PenLineJoin.Round
				};
				pen.Freeze();
				dc.DrawGeometry(null, pen, geometry);
			}
		}
	}

	private static Geometry ShapeGeometry(XElement element)
	{
		string name = element.Name.LocalName;
		if (name == "path")
		{
			string d = (string)element.Attribute("d");
			if (string.IsNullOrWhiteSpace(d))
			{
				return null;
			}
			try
			{
				return Geometry.Parse(d);
			}
			catch
			{
				return null;
			}
		}
		if (name == "rect")
		{
			double x = Parse((string)element.Attribute("x"));
			double y = Parse((string)element.Attribute("y"));
			double w = Parse((string)element.Attribute("width"));
			double h = Parse((string)element.Attribute("height"));
			double rx = Parse((string)element.Attribute("rx"));
			if (w <= 0.0 || h <= 0.0)
			{
				return null;
			}
			return new RectangleGeometry(new Rect(x, y, w, h), rx, rx);
		}
		if (name == "circle")
		{
			double cx = Parse((string)element.Attribute("cx"));
			double cy = Parse((string)element.Attribute("cy"));
			double r = Parse((string)element.Attribute("r"));
			if (r <= 0.0)
			{
				return null;
			}
			return new EllipseGeometry(new Point(cx, cy), r, r);
		}
		if (name == "line")
		{
			double x1 = Parse((string)element.Attribute("x1"));
			double y1 = Parse((string)element.Attribute("y1"));
			double x2 = Parse((string)element.Attribute("x2"));
			double y2 = Parse((string)element.Attribute("y2"));
			return new LineGeometry(new Point(x1, y1), new Point(x2, y2));
		}
		if (name == "polygon" || name == "polyline")
		{
			string points = (string)element.Attribute("points");
			if (string.IsNullOrWhiteSpace(points))
			{
				return null;
			}
			string[] pairs = points.Split(new char[3] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
			if (pairs.Length < 4)
			{
				return null;
			}
			Point start = new Point(Parse(pairs[0]), Parse(pairs[1]));
			PathFigure figure = new PathFigure
			{
				StartPoint = start,
				IsClosed = (name == "polygon"),
				IsFilled = true
			};
			for (int i = 2; i + 1 < pairs.Length; i += 2)
			{
				figure.Segments.Add(new LineSegment(new Point(Parse(pairs[i]), Parse(pairs[i + 1])), true));
			}
			PathGeometry pathGeometry = new PathGeometry();
			pathGeometry.Figures.Add(figure);
			return pathGeometry;
		}
		return null;
	}

	private static Brush BrushFrom(XAttribute attr, Brush fallback)
	{
		string text = (attr == null) ? null : attr.Value;
		if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "currentColor", StringComparison.OrdinalIgnoreCase))
		{
			return fallback;
		}
		if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "transparent", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		try
		{
			Brush brush = (Brush)new BrushConverter().ConvertFromString(text);
			if (brush != null && brush.CanFreeze)
			{
				brush.Freeze();
			}
			return brush;
		}
		catch
		{
			return fallback;
		}
	}

	private static Transform ParseTransform(string text)
	{
		try
		{
			text = text.Trim();
			if (text.StartsWith("matrix", StringComparison.OrdinalIgnoreCase))
			{
				string[] numbers = text.Substring(text.IndexOf('(') + 1).TrimEnd(')').Split(new char[2] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
				if (numbers.Length >= 6)
				{
					Matrix matrix = new Matrix(Parse(numbers[0]), Parse(numbers[1]), Parse(numbers[2]), Parse(numbers[3]), Parse(numbers[4]), Parse(numbers[5]));
					return new MatrixTransform(matrix);
				}
			}
			else if (text.StartsWith("translate", StringComparison.OrdinalIgnoreCase))
			{
				string[] numbers = text.Substring(text.IndexOf('(') + 1).TrimEnd(')').Split(new char[2] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
				if (numbers.Length >= 2)
				{
					return new TranslateTransform(Parse(numbers[0]), Parse(numbers[1]));
				}
				if (numbers.Length == 1)
				{
					return new TranslateTransform(Parse(numbers[0]), 0.0);
				}
			}
			else if (text.StartsWith("scale", StringComparison.OrdinalIgnoreCase))
			{
				string[] numbers = text.Substring(text.IndexOf('(') + 1).TrimEnd(')').Split(new char[2] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
				if (numbers.Length == 1)
				{
					return new ScaleTransform(Parse(numbers[0]), Parse(numbers[0]));
				}
				if (numbers.Length >= 2)
				{
					return new ScaleTransform(Parse(numbers[0]), Parse(numbers[1]));
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static Transform Combine(Transform first, Transform second)
	{
		if (first == null)
		{
			return second;
		}
		if (second == null)
		{
			return first;
		}
		TransformGroup group = new TransformGroup();
		group.Children.Add(first);
		group.Children.Add(second);
		return group;
	}
}
