using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBoard;

/// <summary>把通知的来源解析成真实的应用图标（exe 关联图标 / Dock 入口图标），失败回退到首字。</summary>
public static class AppIconResolver
{
	private static readonly object gate = new object();
	private static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(IntPtr hObject);

	public static ImageSource Resolve(string aumid, string appName)
	{
		string key = (aumid ?? "").ToLowerInvariant();
		if (key.Length == 0)
		{
			key = "name:" + (appName ?? "");
		}
		lock (gate)
		{
			if (cache.TryGetValue(key, out ImageSource hit))
			{
				return hit;
			}
		}
		ImageSource result = null;
		try
		{
			if (key.StartsWith("popup:"))
			{
				result = FromExeName(key.Substring(6));
			}
			else if (!key.StartsWith("name:"))
			{
				string path = NotificationCenter.ResolvePath(aumid);
				result = path == null ? null : Native.IconCached(path);
				if (result == null)
				{
					result = FromAumidExe(aumid);
				}
			}
		}
		catch
		{
		}
		lock (gate)
		{
			cache[key] = result; // 允许缓存 null：失败时调用方回退首字
		}
		return result;
	}

	private static ImageSource FromAumidExe(string aumid)
	{
		// AUMID 没有 Dock 入口时，从尾段提取可执行文件名再取图标
		try
		{
			int i = aumid.LastIndexOf('\\');
			string tail = i >= 0 ? aumid.Substring(i + 1) : aumid;
			int j = tail.IndexOf('!');
			string file = j >= 0 ? tail.Substring(0, j) : tail;
			if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				file += ".exe";
			}
			int k = file.LastIndexOf('.');
			if (k > 0)
			{
				file = file.Substring(0, k);
			}
			return FromExeName(file);
		}
		catch
		{
			return null;
		}
	}

	private static ImageSource FromExeName(string processName)
	{
		try
		{
			string[] names = { processName };
			if (processName.Equals("wechat", StringComparison.OrdinalIgnoreCase) || processName.Equals("weixin", StringComparison.OrdinalIgnoreCase))
			{
				names = new[] { "Weixin", "WeChat" }; // 微信新旧两代进程名
			}
			foreach (string name in names)
			{
				Process[] all = Process.GetProcessesByName(name);
				if (all == null || all.Length == 0)
				{
					continue;
				}
				foreach (Process p in all)
				{
					try
					{
						string exe = p.MainModule == null ? null : p.MainModule.FileName;
						if (string.IsNullOrEmpty(exe))
						{
							continue;
						}
						ImageSource src = FromPath(exe);
						if (src != null)
						{
							return src;
						}
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static ImageSource FromPath(string exe)
	{
		try
		{
			using (Icon icon = Icon.ExtractAssociatedIcon(exe))
			{
				if (icon == null)
				{
					return null;
				}
				using (Bitmap bmp = icon.ToBitmap())
				{
					IntPtr hbmp = bmp.GetHbitmap();
					try
					{
						ImageSource src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
						src.Freeze();
						return src;
					}
					finally
					{
						DeleteObject(hbmp);
					}
				}
			}
		}
		catch
		{
			return null;
		}
	}
}
