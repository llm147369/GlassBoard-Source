namespace GlassBoard;

public static class GameModeRules
{
	public static bool Suppressed(bool manual, bool automatic, bool fullscreen, bool desktop)
	{
		if (!manual)
		{
			if (automatic & fullscreen)
			{
				return !desktop;
			}
			return false;
		}
		return true;
	}
}
