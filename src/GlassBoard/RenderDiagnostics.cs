using System;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media;

namespace GlassBoard;

// 渲染环境诊断：启动时后台采集一次 OS / CLR / 渲染层级 / 显示器 / DPI / GPU，
// 用于排查不同设备上的渲染与层级兼容问题（如动态壁纸冲突、混合 DPI 错位）。
// 仅记录系统环境信息，不含任何用户内容。详细日志默认关闭：
// 在 %LOCALAPPDATA%\GlassBoard 下创建一个名为 diag.on 的空文件即开启（删除即关闭）。
public static class RenderDiagnostics
{
	private static bool? verbose;

	public static bool Verbose
	{
		get
		{
			if (!verbose.HasValue)
			{
				try
				{
					verbose = File.Exists(Path.Combine(Store.Folder, "diag.on"));
				}
				catch
				{
					verbose = false;
				}
			}
			return verbose.Value;
		}
	}

	public static void Trace(string message)
	{
		if (Verbose)
		{
			Store.Trace("RENDER " + message);
		}
	}

	[DllImport("shcore.dll")]
	private static extern int GetProcessDpiAwareness(IntPtr hprocess, out int value);

	[DllImport("shcore.dll")]
	private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint dwFlags);

	public static void DumpOnce()
	{
		try
		{
			// 基础环境行始终记录（无隐私内容），用于快速区分"部分设备异常"的环境差异
			Store.Trace("RENDER env os=" + Environment.OSVersion.VersionString + " clr=" + Environment.Version + " tier=" + RenderCapability.Tier + " 64bit=" + Environment.Is64BitProcess);
			try
			{
				if (GetProcessDpiAwareness(IntPtr.Zero, out int awareness) == 0)
				{
					Store.Trace("RENDER dpi awareness=" + awareness + " (0=unaware 1=system 2=per-monitor)");
				}
			}
			catch
			{
			}
			try
			{
				int index = 0;
				foreach (Screen s in Screen.AllScreens)
				{
					IntPtr monitor = MonitorFromPoint(new System.Drawing.Point(s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2), 2u);
					uint dpiX = 0u;
					uint dpiY = 0u;
					try
					{
						GetDpiForMonitor(monitor, 0, out dpiX, out dpiY);
					}
					catch
					{
					}
					Store.Trace("RENDER screen " + index + " bounds=" + s.Bounds + " primary=" + s.Primary + " dpi=" + dpiX + "x" + dpiY);
					index++;
				}
			}
			catch
			{
			}
			try
			{
				using ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, VideoModeDescription FROM Win32_VideoController");
				int n = 0;
				foreach (ManagementObject obj in searcher.Get())
				{
					if (n >= 4)
					{
						break;
					}
					Store.Trace("RENDER gpu " + n + " name=" + (obj["Name"] as string ?? "") + " driver=" + (obj["DriverVersion"] as string ?? "") + " mode=" + (obj["VideoModeDescription"] as string ?? ""));
					n++;
				}
			}
			catch
			{
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}
}
