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

	// 从 Run 键的启动命令行里解析出目标 exe 路径（命令格式 "\"path\" --startup"）。
	// 解析失败返回 null，调用方据此跳过自愈。
	public static string TargetOf(string command)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return null;
		}
		string text = command.Trim();
		if (text.Length >= 2 && text[0] == '"')
		{
			int endQuote = text.IndexOf('"', 1);
			if (endQuote > 1)
			{
				return text.Substring(1, endQuote - 1);
			}
			return null;
		}
		int space = text.IndexOf(' ');
		return (space > 0) ? text.Substring(0, space) : text;
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
