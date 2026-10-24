using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace GlassBoard;

public sealed class MediaSessionBridge : IDisposable
{
	private readonly bool nativeOnly;

	private readonly bool neteaseOnly;

	public bool UseNative;

	public bool ArtworkOnly;

	private Process process;

	private readonly object gate = new object();

	private bool stopped;

	private DateTime retry = DateTime.MinValue;

	private string lastFailure = "";

	private readonly object errorGate = new object();

	private string stderr = "";

	public MediaSessionBridge(bool native = false, bool netease = false)
	{
		nativeOnly = native;
		neteaseOnly = netease;
	}

	public Task<MediaSnapshot> Send(string command, double seconds = 0.0, string key = null, MediaSnapshot hint = null)
	{
		return Task.Run(() =>
		{
			lock (gate)
			{
				if (!stopped)
				{
					try
					{
						if (process == null || process.HasExited)
						{
							if (DateTime.UtcNow < retry)
							{
								return new MediaSnapshot
								{
									Status = "等待后台读取重试：" + lastFailure
								};
							}
							Start();
						}
						MediaRequest value = new MediaRequest
						{
							Command = command,
							Seconds = seconds,
							Key = key,
							UseNative = UseNative,
							CoverOnly = ArtworkOnly,
							Source = ((hint == null) ? null : hint.Source),
							Title = ((hint == null) ? null : hint.Title),
							Artist = ((hint == null) ? null : hint.Artist),
							Album = ((hint == null) ? null : hint.Album),
							Playing = (hint != null && hint.Playing)
						};
						MediaWireProtocol.Write(process.StandardInput.BaseStream, value);
						Task<string> task = process.StandardOutput.ReadLineAsync();
						if (!task.Wait((nativeOnly || neteaseOnly) ? 7000 : 15000))
						{
							throw new TimeoutException("系统媒体读取超时");
						}
						string result = task.Result;
						if (result == null)
						{
							string text;
							lock (errorGate)
							{
								text = stderr;
							}
							throw new IOException("后台读取进程已退出" + (process.HasExited ? ("（" + process.ExitCode + "）") : "") + (string.IsNullOrWhiteSpace(text) ? "" : ("：" + text.Trim())));
						}
						if (result.Length > 12000000)
						{
							throw new InvalidDataException("封面数据过大");
						}
						using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(result));
						return (MediaSnapshot)new DataContractJsonSerializer(typeof(MediaSnapshot)).ReadObject((Stream)stream);
					}
					catch (Exception ex)
					{
						lastFailure = ex.GetType().Name + "：" + ex.Message;
						Kill();
						retry = DateTime.UtcNow.AddSeconds(5.0);
						return new MediaSnapshot
						{
							Status = lastFailure
						};
					}
				}
				return new MediaSnapshot
				{
					Status = "播放器已关闭"
				};
			}
		});
	}

	private void Start()
	{
		Kill();
		string text = (nativeOnly ? "NativeMediaBridge.ps1" : "MediaBridge.ps1");
		string location = Assembly.GetExecutingAssembly().Location;
		string text2 = "GlassBoard." + text;
		string s;
		using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(text2))
		{
			if (stream != null)
			{
				s = "$a=[Reflection.Assembly]::LoadFile('" + location.Replace("'", "''") + "');$r=New-Object IO.StreamReader($a.GetManifestResourceStream('" + text2 + "'));try{$s=$r.ReadToEnd()}finally{$r.Dispose()};& ([ScriptBlock]::Create($s))";
			}
			else
			{
				string text3 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src", text);
				if (!neteaseOnly && !File.Exists(text3))
				{
					throw new FileNotFoundException("音乐读取模块缺失；主程序内置模块和外部脚本均不可用：" + text3, text3);
				}
				s = "& '" + text3.Replace("'", "''") + "'";
			}
		}
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = (neteaseOnly ? location : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")),
			Arguments = (neteaseOnly ? "--netease-ui-bridge" : ("-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(s)))),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		processStartInfo.EnvironmentVariables["GLASSBOARD_HOME"] = AppDomain.CurrentDomain.BaseDirectory;
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
		Process process = this.process;
		try
		{
			if (process != null && !process.HasExited)
			{
				process.Kill();
			}
		}
		catch
		{
		}
		Task.Run(() =>
		{
			lock (gate)
			{
				Kill();
			}
		});
	}
}
