using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace GlassBoard;

/// <summary>首次启动时检测安装器附带的推荐配置（DefaultConfig.json），询问用户是否导入。</summary>
public static class ConfigImport
{
	public static void TryRun()
	{
		string defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DefaultConfig.json");
		if (!File.Exists(defaultPath))
		{
			return;
		}
		try
		{
			// 覆盖安装会重新解包 DefaultConfig.json。若本机此前已经询问过（存在 .done 标记），
			// 直接静默跳过——升级/重装绝不能再次覆盖用户自己的 dock 与面板配置。
			if (File.Exists(defaultPath + ".done"))
			{
				return;
			}
			MessageBoxResult choice = MessageBox.Show("检测到作者推荐的配置。\n\n是否使用推荐配置？\n· 会覆盖当前设置（已有配置将自动备份）\n· 不会导入任何图标文件\n· 应用列表仅保留你电脑上实际存在的程序", "GlassBoard-Source · 推荐配置", MessageBoxButton.YesNo, MessageBoxImage.Question);
			if (choice == MessageBoxResult.Yes)
			{
				Preferences p = Store.ReadLayout(File.ReadAllBytes(defaultPath));
				if (p != null)
				{
					StripIcons(p);
					FilterApps(p);
					// 推荐配置里 Imported=true 会让对方机器跳过桌面自动导入（"桌面程序不进面板"的根因）。
					// 置 false 后，Controller 启动流程会自动执行一次桌面导入，把对方自己的桌面程序收进大面板。
					p.Imported = false;
					p.Normalize();
					if (File.Exists(Store.FileName))
					{
						try
						{
							File.Copy(Store.FileName, Store.FileName + ".preconfig.bak", overwrite: true);
						}
						catch
						{
						}
					}
					Store.Save(p);
				}
			}
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
		finally
		{
			// 无论是否采纳，只询问一次。
			try
			{
				File.Move(defaultPath, defaultPath + ".done");
			}
			catch
			{
				try
				{
					File.Delete(defaultPath);
				}
				catch
				{
				}
			}
		}
	}

	private static void StripIcons(Preferences p)
	{
		foreach (Entry a in p.Apps ?? new List<Entry>())
		{
			if (a != null)
			{
				a.IconPath = "";
			}
		}
		if (p.SystemIconImages != null)
		{
			p.SystemIconImages.Clear();
		}
	}

	private static void FilterApps(Preferences p)
	{
		if (p.Apps == null)
		{
			p.Apps = new List<Entry>();
			return;
		}
		p.Apps = p.Apps.Where(Keepable).ToList();
	}

	private static bool Keepable(Entry a)
	{
		if (a == null)
		{
			return false;
		}
		string path = a.Path ?? "";
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		if (path.Contains("://") || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) || path.StartsWith("::", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return File.Exists(path) || Directory.Exists(path);
	}
}
