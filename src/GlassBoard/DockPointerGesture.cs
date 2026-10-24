using System;

namespace GlassBoard;

public sealed class DockPointerGesture
{
	public readonly string Source;

	private readonly double startX;

	private readonly double startY;

	private readonly double thresholdX;

	private readonly double thresholdY;

	public bool Active { get; private set; }

	public bool Dragging { get; private set; }

	public DockPointerGesture(string source, double x, double y, double minimumX, double minimumY)
	{
		Source = source;
		startX = x;
		startY = y;
		thresholdX = Math.Max(1.0, minimumX);
		thresholdY = Math.Max(1.0, minimumY);
		Active = true;
	}

	public void Move(double x, double y)
	{
		if (Active && !double.IsNaN(x) && !double.IsNaN(y) && !double.IsInfinity(x) && !double.IsInfinity(y) && (Math.Abs(x - startX) >= thresholdX || Math.Abs(y - startY) >= thresholdY))
		{
			Dragging = true;
		}
	}

	public DockPointerResult Release(double x, double y, bool inside, DockDropTarget target, bool allowClick = true)
	{
		if (!Active)
		{
			return new DockPointerResult();
		}
		if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
		{
			Active = false;
			return new DockPointerResult
			{
				Dragged = Dragging
			};
		}
		Move(x, y);
		Active = false;
		return new DockPointerResult
		{
			Click = (!Dragging & allowClick),
			Dragged = Dragging,
			Target = ((Dragging & inside) ? target : null)
		};
	}

	public void Cancel()
	{
		Active = false;
	}
}
