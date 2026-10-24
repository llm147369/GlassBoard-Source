using System;

namespace GlassBoard;

public static class StartupRules
{
	public static string Command(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || path.IndexOf('"') >= 0 || path.IndexOf('\r') >= 0 || path.IndexOf('\n') >= 0)
		{
			throw new ArgumentException("启动路径无效");
		}
		return "\"" + path + "\" --startup";
	}

	public static bool Enabled(string target, byte[] approved)
	{
		if (!string.IsNullOrWhiteSpace(target))
		{
			if (approved != null && approved.Length != 0)
			{
				if (approved[0] != 3)
				{
					return approved[0] != 7;
				}
				return false;
			}
			return true;
		}
		return false;
	}
}
