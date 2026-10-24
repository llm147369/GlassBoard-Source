using System;
using System.Management;

namespace GlassBoard;

// 显示器亮度（WMI WmiMonitorBrightness / WmiMonitorBrightnessMethods），无需管理员权限。
// 台式机或旧显卡驱动可能不支持，调用方需处理 null 返回值。
public static class BrightnessCore
{
	// 0-100；不支持时返回 null
	public static int? TryGet()
	{
		try
		{
			using ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
			foreach (ManagementObject item in searcher.Get())
			{
				try
				{
					object value = item["CurrentBrightness"];
					if (value != null)
					{
						return Math.Max(0, Math.Min(100, Convert.ToInt32(value)));
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return null;
	}

	public static bool TrySet(int value)
	{
		try
		{
			using ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
			foreach (ManagementObject item in searcher.Get())
			{
				try
				{
					item.InvokeMethod("WmiSetBrightness", new object[2] { (uint)1, (byte)Math.Max(0, Math.Min(100, value)) });
					return true;
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return false;
	}
}
