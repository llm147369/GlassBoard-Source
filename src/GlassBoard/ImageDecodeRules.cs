using System;

namespace GlassBoard;

public static class ImageDecodeRules
{
	public static int[] Fit(int width, int height, int limit)
	{
		if (width < 1 || height < 1 || limit < 1)
		{
			throw new ArgumentOutOfRangeException();
		}
		double num = Math.Min(1.0, (double)limit / (double)Math.Max(width, height));
		return new int[2]
		{
			Math.Max(1, (int)Math.Round((double)width * num)),
			Math.Max(1, (int)Math.Round((double)height * num))
		};
	}
}
