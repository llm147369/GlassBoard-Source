using System;
using System.IO;

namespace GlassBoard;

public static class EntryPathRules
{
	public static string Normalize(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
		{
			return path;
		}
		string fullPath = Path.GetFullPath(path);
		string pathRoot = Path.GetPathRoot(fullPath);
		if (!string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
		{
			return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		return pathRoot;
	}

	public static string Name(string path)
	{
		if (!Directory.Exists(path))
		{
			if (!DocumentRules.Supported(path))
			{
				return Path.GetFileNameWithoutExtension(path);
			}
			return Path.GetFileName(path);
		}
		DirectoryInfo directoryInfo = new DirectoryInfo(Normalize(path));
		if (!string.IsNullOrEmpty(directoryInfo.Name))
		{
			return directoryInfo.Name;
		}
		return directoryInfo.FullName;
	}
}
