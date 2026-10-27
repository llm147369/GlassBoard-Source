using System;

namespace GlassBoard;

public sealed class RunningWindow
{
	public IntPtr Handle;

	public uint ProcessId;

	public string Path;

	public string Title;

	// 应用标识（AppUserModelID）：UWP/商店应用与启动器场景的窗口归组依据
	public string AppUserModelId;

	// 仅托盘/隐藏态运行的窗口（无可见窗口时的兜底收录）
	public bool Tray;
}
