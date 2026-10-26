using System;

namespace GlassBoard;

// 系统命名空间入口：此电脑 / 回收站，默认出现在面板中，
// 图标与启动均使用系统官方实现。
public static class SystemPlaces
{
	public const string ComputerClsid = "::{20D04FE0-3AEA-1069-A2D9-08002B30309D}";

	public static bool IsComputer(Entry entry)
	{
		return entry != null && string.Equals(entry.Path, ComputerClsid, StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsComputerPath(string path)
	{
		return string.Equals(path, ComputerClsid, StringComparison.OrdinalIgnoreCase);
	}

	public static Entry CreateComputer()
	{
		return new Entry
		{
			Name = "此电脑",
			Path = ComputerClsid,
			IsFolder = true
		};
	}
}
