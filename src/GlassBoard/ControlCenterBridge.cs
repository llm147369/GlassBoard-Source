using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace GlassBoard;

// 与内置 ControlCenter.ps1 的 JSON 行协议桥接：无线电开关、蓝牙配对列表、取消配对。
[DataContract]
public sealed class CcRequest
{
	[DataMember(EmitDefaultValue = false)]
	public string Action;

	[DataMember(EmitDefaultValue = false)]
	public string Arg1;

	[DataMember(EmitDefaultValue = false)]
	public string Arg2;
}

[DataContract]
public sealed class CcReply
{
	[DataMember]
	public bool Ok;

	[DataMember]
	public string Error = "";

	[DataMember]
	public string Value = "";
}

public sealed class ControlCenterBridge : IDisposable
{
	private Process process;

	private readonly object gate = new object();

	private bool stopped;

	private DateTime retry = DateTime.MinValue;

	private string lastFailure = "";

	private readonly object errorGate = new object();

	private string stderr = "";

	private CcReply Send(string action, string arg1 = null, string arg2 = null)
	{
		lock (gate)
		{
			if (stopped)
			{
				return new CcReply
				{
					Error = "桥接已关闭"
				};
			}
			try
			{
				if (process == null || process.HasExited)
				{
					if (DateTime.UtcNow < retry)
					{
						return new CcReply
						{
							Error = "等待后台重试：" + lastFailure
						};
					}
					Start();
				}
				using MemoryStream stream = new MemoryStream();
				new DataContractJsonSerializer(typeof(CcRequest)).WriteObject(stream, new CcRequest
				{
					Action = action,
					Arg1 = arg1,
					Arg2 = arg2
				});
				byte[] array = stream.ToArray();
				process.StandardInput.BaseStream.Write(array, 0, array.Length);
				process.StandardInput.BaseStream.WriteByte(10);
				process.StandardInput.BaseStream.Flush();
				string result = process.StandardOutput.ReadLine();
				if (result == null)
				{
					string text;
					lock (errorGate)
					{
						text = stderr;
					}
					throw new IOException("后台进程已退出" + (process.HasExited ? ("（" + process.ExitCode + "）") : "") + (string.IsNullOrWhiteSpace(text) ? "" : ("：" + text.Trim())));
				}
				using MemoryStream stream2 = new MemoryStream(Encoding.UTF8.GetBytes(result));
				return (CcReply)new DataContractJsonSerializer(typeof(CcReply)).ReadObject(stream2);
			}
			catch (Exception ex)
			{
				lastFailure = ex.GetType().Name + "：" + ex.Message;
				Kill();
				retry = DateTime.UtcNow.AddSeconds(5.0);
				return new CcReply
				{
					Error = lastFailure
				};
			}
		}
	}

	private void Start()
	{
		Kill();
		string location = Assembly.GetExecutingAssembly().Location;
		string resourceName = "GlassBoard.ControlCenter.ps1";
		string s;
		using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
		{
			if (stream != null)
			{
				s = "$a=[Reflection.Assembly]::LoadFile('" + location.Replace("'", "''") + "');$r=New-Object IO.StreamReader($a.GetManifestResourceStream('" + resourceName + "'));try{$s=$r.ReadToEnd()}finally{$r.Dispose()};& ([ScriptBlock]::Create($s))";
			}
			else
			{
				string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src", "GlassBoard", "ControlCenter.ps1");
				if (!File.Exists(file))
				{
					throw new FileNotFoundException("控制中心模块缺失；主程序内置模块和外部脚本均不可用：" + file, file);
				}
				s = "& '" + file.Replace("'", "''") + "'";
			}
		}
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
			Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(s)),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		lock (errorGate)
		{
			stderr = "";
		}
		process = Process.Start(processStartInfo);
		process.ErrorDataReceived += (object obj, DataReceivedEventArgs e) =>
		{
			if (!string.IsNullOrEmpty(e.Data))
			{
				lock (errorGate)
				{
					stderr = stderr + e.Data + "\n";
					if (stderr.Length > 2000)
					{
						stderr = stderr.Substring(stderr.Length - 2000);
					}
				}
			}
		};
		process.BeginErrorReadLine();
	}

	private void Kill()
	{
		Process process = this.process;
		this.process = null;
		if (process == null)
		{
			return;
		}
		try
		{
			if (!process.HasExited)
			{
				process.Kill();
			}
		}
		catch
		{
		}
		process.Dispose();
	}

	public void Dispose()
	{
		stopped = true;
		Kill();
	}

	// 无线状态：Value = "wifi:on|bt:off"（absent 表示系统无该设备）
	public CcReply State()
	{
		return Send("state");
	}

	public CcReply SetRadio(string kind, bool on)
	{
		return Send("radio", kind, on ? "on" : "off");
	}

	// Value = 每行 "id\t名称\t是否已连接"
	public CcReply BluetoothDevices()
	{
		return Send("bluetooth");
	}

	public CcReply Unpair(string deviceId)
	{
		return Send("unpair", deviceId);
	}
}
