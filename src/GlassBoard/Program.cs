using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace GlassBoard;

public static class Program
{
	// 进程启动时刻（Environment.TickCount）：窗口初始化/背景捕获等诊断日志用它换算相对耗时。
	internal static readonly long ProcessStartTick = Environment.TickCount;
	private static void CleanLegacyUpdater()
	{
		try
		{
			string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBoard");
			string updates = System.IO.Path.Combine(folder, "updates");
			if (System.IO.Directory.Exists(updates))
			{
				try
				{
					System.IO.Directory.Delete(updates, recursive: true);
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
			}
			string updater = System.IO.Path.Combine(folder, "GlassBoard.Updater.exe");
			if (System.IO.File.Exists(updater))
			{
				try
				{
					System.IO.File.Delete(updater);
				}
				catch (Exception ex2)
				{
					Store.Log(ex2);
				}
			}
		}
		catch
		{
		}
	}

	[STAThread]
	public static int Main(string[] args)
	{
		if (args.Length != 0 && args[0] == "--netease-ui-bridge")
		{
			return NeteaseUiReader.Run();
		}
		if (args.Length != 0 && args[0] == "--self-test")
		{
			return SelfTest.Run();
		}
		if (args.Length != 0 && args[0] == "--restore-shell")
		{
			try
			{
				if (Native.RequestRestore())
				{
					return 0;
				}
				try
				{
					TaskbarAutoHide.ShowAndClear();
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
				DesktopVisibility.ShowAll();
				MessageBox.Show("已执行任务栏和桌面图标恢复。", "GlassBoard-Source");
				return 0;
			}
			catch (Exception ex2)
			{
				MessageBox.Show(ex2.Message, "GlassBoard-Source");
				return 1;
			}
		}
		// 每次启动自动清除旧版在线更新器残留（%LOCALAPPDATA%\GlassBoard\updates 与
		// GlassBoard.Updater.exe）。旧版卸载只删安装目录，更新器缓存里的旧版程序会在
		// 重启后被旧计划任务恢复出来——"装了新版、重启又变回 0.9.17"的元凶。
		CleanLegacyUpdater();
		bool createdNew;
		using (new Mutex(initiallyOwned: true, "Local\\GlassBoard.Desktop.v1", out createdNew))
		{
			if (!createdNew)
			{
				// 已有实例在运行：先判断它是不是旧版（0.9.18 之前的二改前版本）。
				// 若是，直接结束旧版并重启本进程接管——否则旧版抢先自启（计划任务/Run 键
				// 指向旧目录）时，新版本每次都会被"已有实例"顶掉，用户看到的永远是旧程序。
				int takeover = 0;
				for (int ai = 1; ai < args.Length; ai++)
				{
					if (args[ai].StartsWith("--takeover=", StringComparison.Ordinal))
					{
						int.TryParse(args[ai].Substring("--takeover=".Length), out takeover);
					}
				}
				// 找出所有 GlassBoard 进程里"非本机路径"的实例：旧版可能藏在别的目录
				// 或更新器缓存里，读 FileVersion 会因权限/WOW64 失败——比较进程路径更可靠，
				// 路径不同（或读不到路径）即视为外来实例，一律结束并接管。
				string selfPath = Assembly.GetExecutingAssembly().Location.ToLowerInvariant();
				System.Collections.Generic.List<System.Diagnostics.Process> foreign = new System.Collections.Generic.List<System.Diagnostics.Process>();
				try
				{
					foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("GlassBoard"))
					{
						if (p.Id == System.Diagnostics.Process.GetCurrentProcess().Id) continue;
						try
						{
							string pPath = (p.MainModule?.FileName ?? "").ToLowerInvariant();
							if (pPath.Length == 0 || pPath != selfPath)
								foreign.Add(p);
						}
						catch
						{
							foreign.Add(p);
						}
					}
				}
				catch
				{
				}
				if (foreign.Count > 0 && takeover < 2)
				{
					foreach (System.Diagnostics.Process p in foreign)
					{
						try
						{
							p.Kill();
							p.WaitForExit(3000);
						}
						catch
						{
						}
					}
					Thread.Sleep(400);
					try
					{
						System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--takeover=" + (takeover + 1))
						{
							UseShellExecute = true
						});
					}
					catch
					{
					}
					return 0;
				}
				// 唤起已有实例的 Dock 后静默退出。
				// 不再弹"已有 GlassBoard 在运行"提示框——开机自启动、重复点击 EXE
				// 都不应打扰用户，第二个实例直接把请求转发给第一个实例。
				try
				{
					Native.RequestRestore();
				}
				catch (Exception ex)
				{
					Store.Log(ex);
				}
				return 0;
			}
			try
			{
				Store.Trace("START phase=enter exe=" + Assembly.GetExecutingAssembly().Location + " OS=" + Environment.OSVersion?.ToString() + " CLR=" + Environment.Version);
				try
				{
					// 崩溃恢复前先读偏好：新会话本就要隐藏任务栏/图标时不做"还原再重隐"，
					// 避免两次桌面级变更导致 Wallpaper Engine 等动态壁纸瞬间黑屏。
					Preferences recoveryPrefs = null;
					try
					{
						recoveryPrefs = Store.Load(mergeJournal: false);
					}
					catch
					{
					}
					DesktopVisibility.Recover(keepIconsHidden: recoveryPrefs?.HideDesktopIcons == true, keepTaskbarHidden: recoveryPrefs?.HideTaskbar == true);
					Store.Trace("START phase=recover ms=" + (Environment.TickCount - ProcessStartTick));
				}
				catch (Exception ex3)
				{
					Store.Log(ex3);
				}
				try
				{
					TaskbarAutoHide.Restore();
					Store.Trace("START phase=taskbar-restore ms=" + (Environment.TickCount - ProcessStartTick));
				}
				catch (Exception ex4)
				{
					Store.Log(ex4);
				}
				ConfigImport.TryRun();
				Store.Trace("START phase=config-import ms=" + (Environment.TickCount - ProcessStartTick));
				HealthMonitor.Start();
				Application application = new Application();
				application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
				Controller controller = null;
				application.Exit += (object s, ExitEventArgs e) =>
				{
					HealthMonitor.Shutdown();
					try
					{
						// 隐藏桌面图标是持久偏好：退出时保留隐藏状态，下次启动免广播、
						// 免重新隐藏（Wallpaper Engine 兼容修复的一部分）。
						DesktopVisibility.RestoreOriginal(keepIconsHidden: controller != null && controller.Prefs.HideDesktopIcons);
					}
					catch (Exception ex6)
					{
						Store.Log(ex6);
					}
					try
					{
						TaskbarAutoHide.Restore();
					}
					catch (Exception ex7)
					{
						Store.Log(ex7);
					}
				};
				application.DispatcherUnhandledException += (object s, DispatcherUnhandledExceptionEventArgs e) =>
				{
					HealthMonitor.Report("界面异常", e.Exception?.ToString() ?? "未知");
					Store.Log(e.Exception);
					if (controller != null)
					{
						try
						{
							Store.Save(controller.Prefs);
						}
						catch (Exception ex6)
						{
							Store.Log(ex6);
						}
					}
					MessageBox.Show("操作失败，详情已记录在配置文件夹 error.log。\n" + e.Exception.Message, "GlassBoard-Source");
					e.Handled = true;
				};
				controller = new Controller();
				Store.Trace("START phase=controller ms=" + (Environment.TickCount - ProcessStartTick));
				if (args.Any((string a) => a == "--recover-widgets"))
				{
					controller.RecoverWidgets();
				}
				application.Run();
				GC.KeepAlive(controller);
				return 0;
			}
			catch (Exception ex5)
			{
				HealthMonitor.Report("启动失败", ex5.ToString());
				Store.Log(ex5);
				MessageBox.Show("启动失败：" + ex5.Message, "GlassBoard-Source");
				return 1;
			}
		}
	}
}
