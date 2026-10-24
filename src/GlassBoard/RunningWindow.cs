using System;

namespace GlassBoard;

public sealed class RunningWindow
{
	public IntPtr Handle;

	public uint ProcessId;

	public string Path;

	public string Title;

	// 仅托盘/隐藏态运行的窗口（无可见窗口时的兜底收录）
	public bool Tray;
}
