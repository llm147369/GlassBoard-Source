using System;
using System.IO;
using System.Windows;

namespace GlassBoard;

// 卸载器：自动检索 GlassBoard 的数据位置与开机启动项；按用户选择清理。
// 自删方案：纯 ASCII 批处理 + 环境变量注入路径（中文路径安全，与在线更新重启脚本同方案）。
public static class Uninstaller
{
	public static string InstallDir
	{
		get
		{
			string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
			return dir;
		}
	}

	public static string DataDir
	{
		get
		{
			return Store.Folder;
		}
	}

	public static bool HasRunEntry()
	{
		try
		{
			using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
			{
				return key != null && key.GetValue("GlassBoard") != null;
			}
		}
		catch
		{
			return false;
		}
	}

	public static string StartupShortcut()
	{
		try
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "GlassBoard.lnk");
			return File.Exists(path) ? path : null;
		}
		catch
		{
			return null;
		}
	}

	// 检索结果摘要（设置页卸载区块展示）
	public static string Summary()
	{
		string startup = HasRunEntry() ? "注册表 Run 键" : "未启用开机启动";
		string lnk = StartupShortcut();
		if (lnk != null)
		{
			startup += " · 启动文件夹快捷方式";
		}
		return "程序位置：" + InstallDir + "\n数据位置：" + DataDir + "\n开机启动：" + startup;
	}

	// 执行卸载：deleteData=删除配置数据；deleteInstall=删除程序目录
	public static void Run(bool deleteData, bool deleteInstall)
	{
		try
		{
			string script = Path.Combine(Path.GetTempPath(), "gb-uninstall-" + Environment.TickCount + ".cmd");
			string body = "@echo off\r\n" +
				"ping 127.0.0.1 -n 2 > nul\r\n" +
				"taskkill /f /im GlassBoard.exe > nul 2>&1\r\n" +
				"reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v GlassBoard /f > nul 2>&1\r\n" +
				"if not \"%GB_STARTUP%\"==\" \" del \"%GB_STARTUP%\" > nul 2>&1\r\n" +
				"if \"%GB_DELDATA%\"==\"1\" rd /s /q \"%GB_DATA%\"\r\n" +
				"if \"%GB_DELINSTALL%\"==\"1\" rd /s /q \"%GB_INSTALL%\"\r\n" +
				"del \"%~f0\"\r\n";
			File.WriteAllText(script, body, System.Text.Encoding.ASCII);
			System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
			};
			psi.EnvironmentVariables["GB_DELDATA"] = deleteData ? "1" : "0";
			psi.EnvironmentVariables["GB_DELINSTALL"] = deleteInstall ? "1" : "0";
			psi.EnvironmentVariables["GB_DATA"] = DataDir;
			psi.EnvironmentVariables["GB_INSTALL"] = InstallDir;
			psi.EnvironmentVariables["GB_STARTUP"] = StartupShortcut() ?? " ";
			System.Diagnostics.Process.Start(psi);
			Application.Current.Shutdown();
		}
		catch (Exception ex)
		{
			MessageBox.Show("启动卸载失败：" + ex.Message, "GlassBoard");
		}
	}
}
