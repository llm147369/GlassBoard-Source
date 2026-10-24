namespace GlassBoard;

public static class DockAnchorRules
{
	public static double Left(double? center, double width, double fallback, double areaLeft, double areaWidth)
	{
		return FeatureRules.Position(center.HasValue ? (center.Value - width / 2.0) : fallback, areaLeft, areaWidth, width);
	}
}
