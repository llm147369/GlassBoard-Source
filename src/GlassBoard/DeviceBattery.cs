using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace GlassBoard;

[DataContract]
public class DeviceBatteryInfo
{
	[DataMember(Name = "key")] public string Key = "";
	[DataMember(Name = "name")] public string Name = "";
	[DataMember(Name = "via")] public string Via = "";
	[DataMember(Name = "kind")] public string Kind = "";
	[DataMember(Name = "percent")] public int Percent;
	[DataMember(Name = "charging")] public bool Charging;
	[DataMember(Name = "asleep")] public bool Asleep;
	[DataMember(Name = "connected")] public bool Connected = true;
}

// 设备电量组件引擎（widget 侧），双源合并：
// - 原生：BatteryCore（电脑电池 + HID 标准电池服务鼠标/键盘 + 蓝牙 LE），仓库内已有、
//   与灵动岛同源，无需任何外部程序；灵动岛关闭时引擎会自愈重启它。
// - 扩展：探测 BatteryHub.exe（krisgfx9/Wireless-Device-Battery-Monitor），每 60 秒
//   后台执行 `BatteryHub.exe --json`，补充耳机/手柄/罗技 2.4G 接收器等原生协议覆盖
//   不到的设备。exe 缺失时静默跳过，不影响原生部分。
// 轮询全部在后台线程；结果经 UI Dispatcher 推给组件，失败静默降级。
public sealed class DeviceBattery : IDisposable
{
	public readonly Controller C;

	public string ExePath = "";

	public bool BatteryHubFound => ExePath.Length > 0;

	public List<DeviceBatteryInfo> Devices = new List<DeviceBatteryInfo>();

	public string Message = "";

	public event Action Changed;

	private Timer timer;

	private bool busy;

	private bool closed;

	private List<DeviceBatteryInfo> nativeRows = new List<DeviceBatteryInfo>();

	private List<DeviceBatteryInfo> hubRows = new List<DeviceBatteryInfo>();

	public DeviceBattery(Controller c)
	{
		C = c;
	}

	public void SetEnabled(bool on)
	{
		if (on)
		{
			Start();
		}
		else
		{
			Stop();
		}
	}

	private void Start()
	{
		if (timer != null || closed)
		{
			return;
		}
		timer = new Timer(OnTick, null, 1000, -1);
	}

	private void Stop()
	{
		Timer timer2 = timer;
		timer = null;
		if (timer2 != null)
		{
			timer2.Dispose();
		}
	}

	private void OnTick(object state)
	{
		if (closed || timer == null)
		{
			return;
		}
		try
		{
			RebuildNative();
			if (ExePath.Length == 0)
			{
				Probe();
			}
			else
			{
				PollHub();
			}
		}
		catch (Exception ex)
		{
			Message = "设备电量异常：" + ex.Message;
		}
		finally
		{
			Rebuild();
			Notify();
			try
			{
				timer.Change(60000, -1);
			}
			catch
			{
			}
		}
	}

	// 原生部分：直接读 BatteryCore 快照；若灵动岛关闭时把核心停掉，这里自愈重启。
	private void RebuildNative()
	{
		Application current = Application.Current;
		if (current != null)
		{
			BatteryCore.Start(current.Dispatcher);
		}
		BatteryCore.BatteryState s = BatteryCore.Current;
		List<DeviceBatteryInfo> list = new List<DeviceBatteryInfo>();
		if (s.PcLevel.HasValue)
		{
			list.Add(new DeviceBatteryInfo
			{
				Key = "pc",
				Name = "电脑",
				Kind = "pc",
				Percent = (int)s.PcLevel.Value,
				Charging = (s.PcCharging == true),
				Via = "内置电池"
			});
		}
		if (s.MouseLevel.HasValue)
		{
			list.Add(new DeviceBatteryInfo
			{
				Key = "hid-mouse",
				Name = string.IsNullOrWhiteSpace(s.MouseName) ? "鼠标" : s.MouseName,
				Kind = "mouse",
				Percent = (int)s.MouseLevel.Value
			});
		}
		if (s.KeyboardLevel.HasValue)
		{
			list.Add(new DeviceBatteryInfo
			{
				Key = "hid-keyboard",
				Name = string.IsNullOrWhiteSpace(s.KeyboardName) ? "键盘" : s.KeyboardName,
				Kind = "keyboard",
				Percent = (int)s.KeyboardLevel.Value
			});
		}
		nativeRows = list;
	}

	private void Probe()
	{
		string exePath = FindExe();
		if (exePath.Length == 0)
		{
			return;
		}
		ExePath = exePath;
		Store.Trace("BATTERY hub found at=" + exePath);
		PollHub();
	}

	private void PollHub()
	{
		if (busy)
		{
			return;
		}
		busy = true;
		Task.Run((Action)RunOnce);
	}

	private void RunOnce()
	{
		try
		{
			string json = Invoke();
			hubRows = ((json == null) ? new List<DeviceBatteryInfo>() : Parse(json));
		}
		catch (Exception ex)
		{
			hubRows = new List<DeviceBatteryInfo>();
			Message = "读取失败：" + ex.Message;
		}
		finally
		{
			busy = false;
			Rebuild();
			Notify();
		}
	}

	// 返回 null 表示超时/失败（Message 已写）。
	private string Invoke()
	{
		ProcessStartInfo psi = new ProcessStartInfo
		{
			FileName = ExePath,
			Arguments = "--json",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8
		};
		using (Process p = new Process { StartInfo = psi })
		{
			p.Start();
			Task<string> read = Task.Run(() => p.StandardOutput.ReadToEnd());
			if (!p.WaitForExit(8000))
			{
				try
				{
					p.Kill();
				}
				catch
				{
				}
				Message = "读取超时";
				return null;
			}
			if (p.ExitCode != 0)
			{
				Message = "读取失败（退出码 " + p.ExitCode + "）";
				return null;
			}
			return read.Result;
		}
	}

	private List<DeviceBatteryInfo> Parse(string json)
	{
		try
		{
			using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
			{
				List<DeviceBatteryInfo> list = (List<DeviceBatteryInfo>)new DataContractJsonSerializer(typeof(List<DeviceBatteryInfo>)).ReadObject(ms);
				Message = "";
				return list ?? new List<DeviceBatteryInfo>();
			}
		}
		catch (Exception ex)
		{
			Message = "解析失败：" + ex.Message;
			return new List<DeviceBatteryInfo>();
		}
	}

	// 组合显示列表：原生 + BatteryHub（Hub 的鼠标/键盘与原生重复时让位原生）。
	private void Rebuild()
	{
		BatteryCore.BatteryState s = BatteryCore.Current;
		List<DeviceBatteryInfo> list = new List<DeviceBatteryInfo>();
		list.AddRange(nativeRows);
		foreach (DeviceBatteryInfo d in hubRows)
		{
			if ((d.Kind == "mouse" && s.MouseLevel.HasValue) || (d.Kind == "keyboard" && s.KeyboardLevel.HasValue))
			{
				continue;
			}
			list.Add(d);
		}
		Devices = list;
	}

	private void Notify()
	{
		if (closed)
		{
			return;
		}
		Application current = Application.Current;
		if (current == null)
		{
			return;
		}
		current.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
		{
			if (!closed)
			{
				Changed?.Invoke();
			}
		}));
	}

	// 探测顺序：本程序目录（根/tools/BatteryHub）→ PATH → 常见安装位置。
	public static string FindExe()
	{
		string appDir = AppDomain.CurrentDomain.BaseDirectory;
		List<string> list = new List<string>();
		AddCandidates(list, appDir);
		AddCandidates(list, Path.Combine(appDir, "tools"));
		AddCandidates(list, Path.Combine(appDir, "BatteryHub"));
		string path = Environment.GetEnvironmentVariable("PATH") ?? "";
		string[] dirs = path.Split(';');
		foreach (string dir in dirs)
		{
			if (dir.Length > 0)
			{
				AddCandidates(list, dir);
			}
		}
		AddCandidates(list, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WirelessBattery"));
		AddCandidates(list, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WirelessBattery"));
		AddCandidates(list, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WirelessBattery"));
		foreach (string candidate in list)
		{
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}
		return "";
	}

	private static void AddCandidates(List<string> list, string dir)
	{
		list.Add(Path.Combine(dir, "BatteryHub.exe"));
		list.Add(Path.Combine(dir, "WirelessBattery.exe"));
	}

	public void Dispose()
	{
		closed = true;
		Timer timer2 = timer;
		timer = null;
		if (timer2 != null)
		{
			timer2.Dispose();
		}
	}
}
