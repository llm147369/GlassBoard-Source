using System;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace GlassBoard;

// 虚拟回收站入口：出现在面板/Dock 中的回收站磁贴，
// 拖入文件即送入系统回收站（可恢复），单击打开系统回收站窗口。
public static class RecycleBin
{
	// 回收站的 Shell 命名空间 GUID 路径；SHGetFileInfo 可直接解析出系统回收站图标
	public const string Clsid = "::{645FF040-5081-101B-9F08-00AA002F954E}";

	public static bool IsEntry(Entry entry)
	{
		return entry != null && string.Equals(entry.Path, Clsid, StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsPath(string path)
	{
		return string.Equals(path, Clsid, StringComparison.OrdinalIgnoreCase);
	}

	public static Entry Create()
	{
		return new Entry
		{
			Name = "回收站",
			Path = Clsid,
			IsFolder = true
		};
	}

	// 把文件/文件夹移入系统回收站（可恢复），返回成功处理的数量
	public static int Delete(string[] paths)
	{
		int num = 0;
		if (paths == null)
		{
			return 0;
		}
		foreach (string path in paths)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(path))
				{
					continue;
				}
				if (Directory.Exists(path))
				{
					FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
				}
				else if (File.Exists(path))
				{
					FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
				}
				else
				{
					continue;
				}
				num++;
			}
			catch
			{
			}
		}
		return num;
	}
}
