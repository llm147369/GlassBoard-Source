using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace GlassBoard;

public static class StartupService
{
	private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	private const string Approved = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";

	private const string Name = "GlassBoard";

	// 启动方式：0=注册表 Run（登录后立即启动，最快）；1=启动文件夹（桌面就绪后启动，稍晚）
	public static int Mode
	{
		get
		{
			if (!string.IsNullOrEmpty(RunValue()))
			{
				return 0;
			}
			if (File.Exists(ShortcutPath()))
			{
				return 1;
			}
			return 0;
		}
	}

	public static bool Enabled
	{
		get
		{
			if (!string.IsNullOrEmpty(RunValue()))
			{
				using RegistryKey approved = Registry.CurrentUser.OpenSubKey(Approved);
				return StartupRules.Enabled(RunValue(), (approved == null) ? null : (approved.GetValue(Name) as byte[]));
			}
			return File.Exists(ShortcutPath());
		}
	}

	public static string Target
	{
		get
		{
			string value = RunValue();
			if (!string.IsNullOrEmpty(value))
			{
				return value;
			}
			if (File.Exists(ShortcutPath()))
			{
				return "启动文件夹快捷方式";
			}
			return null;
		}
	}

	private static string RunValue()
	{
		using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
		return (registryKey == null) ? null : (registryKey.GetValue(Name) as string);
	}

	private static string StartupFolder()
	{
		return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
	}

	private static string ShortcutPath()
	{
		return Path.Combine(StartupFolder(), "GlassBoard.lnk");
	}

	public static void Set(bool enabled)
	{
		Set(enabled, Mode);
	}

	public static void Set(bool enabled, int mode)
	{
		string exe = Assembly.GetExecutingAssembly().Location;
		// 先清掉两种方式的旧启动项，避免双入口同时启动（开机重复实例提示的根源之一）。
		using (RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(RunKeyPath))
		{
			registryKey.DeleteValue(Name, throwOnMissingValue: false);
		}
		using RegistryKey registryKey2 = Registry.CurrentUser.OpenSubKey(Approved, writable: true);
		registryKey2?.DeleteValue(Name, throwOnMissingValue: false);
		try
		{
			File.Delete(ShortcutPath());
		}
		catch
		{
		}
		if (!enabled)
		{
			return;
		}
		if (mode == 1)
		{
			// 启动文件夹方式：桌面加载完成后启动，避免与登录初期的系统负载争抢。
			try
			{
				Type shellType = Type.GetTypeFromProgID("WScript.Shell");
				if (shellType == null)
				{
					throw new InvalidOperationException("WScript.Shell 不可用");
				}
				object shell = Activator.CreateInstance(shellType);
				object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[1] { ShortcutPath() });
				if (shortcut == null)
				{
					throw new InvalidOperationException("无法创建快捷方式");
				}
				Type shortcutType = shortcut.GetType();
				shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[1] { exe });
				shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[1] { "--startup" });
				shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[1] { Path.GetDirectoryName(exe) });
				shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
			}
			catch (Exception ex)
			{
				// 创建启动文件夹快捷方式失败时退回注册表 Run（保证自启动仍然生效）。
				using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
				registryKey.SetValue(Name, StartupRules.Command(exe), RegistryValueKind.String);
				Store.Log(ex);
			}
			return;
		}
		using (RegistryKey registryKey3 = Registry.CurrentUser.CreateSubKey(RunKeyPath))
		{
			registryKey3.SetValue(Name, StartupRules.Command(exe), RegistryValueKind.String);
		}
	}
}
