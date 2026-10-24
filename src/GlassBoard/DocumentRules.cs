using System;
using System.Collections.Generic;
using System.IO;

namespace GlassBoard;

public static class DocumentRules
{
	private static readonly HashSet<string> extensions = new HashSet<string>(new string[38]
	{
		".doc", ".docx", ".docm", ".dot", ".dotx", ".wps", ".wpt", ".xls", ".xlsx", ".xlsm",
		".xlsb", ".xlt", ".xltx", ".et", ".ett", ".csv", ".ppt", ".pptx", ".pptm", ".dps",
		".dpt", ".pdf", ".txt", ".md", ".rtf", ".odt", ".ods", ".odp", ".jpg", ".jpeg",
		".png", ".webp", ".svg", ".mp4", ".mp3", ".zip", ".rar", ".7z"
	}, StringComparer.OrdinalIgnoreCase);

	public static bool Supported(string path)
	{
		return extensions.Contains(Path.GetExtension(path ?? ""));
	}
}
