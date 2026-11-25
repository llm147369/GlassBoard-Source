using System;
using NAudio.Wave;
using NAudio.Dsp;

namespace GlassBoard;

// 音乐跳动条数据源：WASAPI 回环（loopback）采集系统扬声器实际输出——覆盖所有音乐软件，
// 不依赖任何播放器插件。FFT 拆成 24 个对数间隔频段（低音分辨率高、高音平滑），带峰值
// 自适应归一与快攻慢衰包络；事件在采集线程触发，UI 侧自行切线程。不同跳动条样式
// （柱状/峰值帽/弹簧/频谱曲线）都由同一份频谱数据驱动，节奏一致、形态各异。
// 依赖 NAudio 2.2.1（MIT，net462 目标兼容 net48），仅此一个用途，不引入其他依赖。
public static class IslandAudio
{
	// 24 档能量 0..1（低→高），采集线程触发；数组为快照拷贝，可安全跨线程持有。
	public const int BandCount = 24;

	// 24 个频段的起始 bin 边界（对数等分 + 严格递增），类型初始化时一次算好。
	// 旧公式 (int)(24*Log(i/2)/Log(127.5)) 在低频 bin 稀疏处会跳频段：
	// bin2→band0、bin3→band2，band1 永远无 bin 落入 → 柱状条第 2/3 根柱子恒死。
	private static readonly int[] bandStart = BuildBandStart();

	private static int[] BuildBandStart()
	{
		int[] start = new int[BandCount + 1];
		for (int b = 0; b <= BandCount; b++)
		{
			int v = (int)Math.Ceiling(2.0 * Math.Pow(128.0, b / (double)BandCount));
			if (b > 0 && v <= start[b - 1])
			{
				v = start[b - 1] + 1;
			}
			start[b] = Math.Min(v, 256);
		}
		return start;
	}

	public static event Action<double[]> AudioLevels;

	private static WasapiLoopbackCapture capture;
	private static readonly object gate = new object();
	private static bool running;

	// 包络（快攻慢衰，避免条跳动闪烁）与峰值自适应
	private static readonly double[] env = new double[BandCount];
	private static readonly double[] peak = new double[BandCount];
	private static readonly double[] sums = new double[BandCount];
	private static readonly int[] counts = new int[BandCount];

	private const int fftLength = 1024;
	// 延迟分配：类型初始化不触碰 NAudio 类型——发布包缺 NAudio.Core.dll 时律动静默
	// 降级（Start 内 try/catch 兜底），而不是整个灵动岛随类型初始化一起崩。
	private static Complex[] fftBuf;
	private static readonly float[] samples = new float[fftLength];

	static IslandAudio()
	{
		for (int i = 0; i < BandCount; i++)
		{
			peak[i] = 1e-3;
		}
	}

	private static void EnsureFft()
	{
		if (fftBuf == null)
		{
			fftBuf = new Complex[fftLength];
		}
	}

	public static bool IsRunning
	{
		get { lock (gate) { return running; } }
	}

	// 引用计数：灵动岛与桌面律动组件共用同一份采集。计数归零才真正停止，
	// 避免任一方退出时把另一方的数据源停掉。
	private static int users;
	private static int restartAttempts;
	private static DateTime lastDataUtc = DateTime.UtcNow;
	private static System.Threading.Timer stallWatch;
	private static bool stallBusy;

	// 采集静默看门狗：WASAPI 共享流偶发静默停发（无 RecordingStopped 事件），
	// 每 2s 检查一次，数据断流 >2.5s 且仍有使用者时强制重建采集。
	private static void OnStallCheck(object state)
	{
		bool needRestart = false;
		lock (gate)
		{
			needRestart = running && users > 0 && !stallBusy && (DateTime.UtcNow - lastDataUtc).TotalSeconds > 2.5;
			if (needRestart)
			{
				stallBusy = true;
			}
		}
		if (!needRestart)
		{
			return;
		}
		try
		{
			WasapiLoopbackCapture old;
			lock (gate)
			{
				old = capture;
				capture = null;
			}
			try
			{
				old?.StopRecording();
			}
			catch
			{
			}
			try
			{
				old?.Dispose();
			}
			catch
			{
			}
			Store.Trace("AUDIO stall restart");
			Start();
		}
		finally
		{
			lock (gate)
			{
				stallBusy = false;
			}
		}
	}

	// 设备意外停采（默认格式切换/设备重枚举）后的自愈重启：延迟 600ms 再拉，最多连试
	// 10 次（约 6 秒）；期间 OnData 正常到达即清零计数。避免与设备的重枚举风暴对撞。
	private static void ScheduleRestart()
	{
		System.Threading.ThreadPool.QueueUserWorkItem(delegate
		{
			System.Threading.Thread.Sleep(600);
			lock (gate)
			{
				if (!running || users <= 0 || restartAttempts >= 10)
				{
					return;
				}
			}
			restartAttempts++;
			Start();
		});
	}

	public static void Acquire()
	{
		lock (gate)
		{
			users++;
		}
		Start();
	}

	public static void Release()
	{
		bool stop = false;
		lock (gate)
		{
			if (users > 0)
			{
				users--;
			}
			stop = (users == 0);
		}
		if (stop)
		{
			Stop();
		}
	}

	public static void Start()
	{
		WasapiLoopbackCapture cap;
		lock (gate)
		{
			if (running && capture != null)
			{
				return;
			}
			running = true;
			cap = capture;
		}
		try
		{
			if (cap == null)
			{
				cap = new WasapiLoopbackCapture();
				cap.DataAvailable += OnData;
				cap.RecordingStopped += (object s, StoppedEventArgs e) =>
				{
					bool restart = false;
					lock (gate)
					{
						if (ReferenceEquals(capture, cap))
						{
							capture = null;
						}
						restart = running && users > 0;
					}
					Store.Trace("AUDIO stopped ex=" + (e.Exception != null ? e.Exception.GetType().Name : "null") + " restart=" + restart);
					if (restart)
					{
						ScheduleRestart();
					}
					else
					{
						restartAttempts = 0;
					}
				};
				capture = cap;
			}
			// 用局部引用启动：RecordingStopped 回调可能在锁释放后恰好清掉字段（设备自行失效），
			// 此时字段二次读取会抛空引用并被 catch 静默吞掉，导致采集永久假死、跳动条冻结。
			cap.StartRecording();
			lastDataUtc = DateTime.UtcNow;
			if (stallWatch == null)
			{
				stallWatch = new System.Threading.Timer(OnStallCheck, null, 2000, 2000);
			}
		}
		catch (Exception ex)
		{
			lock (gate)
			{
				running = false;
				// 仅当字段仍指向本实例才清除，避免误清 Stop 后又新建的新实例
				if (ReferenceEquals(capture, cap))
				{
					capture = null;
				}
			}
			// 没有音频设备/被安全策略拒绝时静默降级：跳动条保持静止，不影响小岛。
			Store.Trace("AUDIO loopback start failed: " + ex.GetType().Name + " " + ex.Message);
		}
	}

	public static void Stop()
	{
		WasapiLoopbackCapture cap;
		lock (gate)
		{
			running = false;
			cap = capture;
			capture = null;
		}
		try
		{
			cap?.StopRecording();
		}
		catch
		{
		}
		try
		{
			cap?.Dispose();
		}
		catch
		{
		}
		for (int i = 0; i < env.Length; i++)
		{
			env[i] = 0.0;
			peak[i] = 1e-3;
		}
	}

	private static void OnData(object sender, WaveInEventArgs e)
	{
		try
		{
			WasapiLoopbackCapture cap = capture;
			if (cap == null)
			{
				return;
			}
			restartAttempts = 0;
			lastDataUtc = DateTime.UtcNow;
			int channels = cap.WaveFormat != null && cap.WaveFormat.Channels > 0 ? cap.WaveFormat.Channels : 2;
			int floatCount = e.BytesRecorded / 4;
			if (floatCount < fftLength)
			{
				return;
			}
			EnsureFft();
			// 数据为 IEEE float32 交错声道，取最新 1024 帧（左右声道平均）。
			int n = fftLength;
			for (int i = 0; i < n; i++)
			{
				int sampleIndex = (floatCount - n + i) * 4;
				float left = BitConverter.ToSingle(e.Buffer, sampleIndex);
				float right = channels > 1 ? BitConverter.ToSingle(e.Buffer, sampleIndex + 4) : left;
				samples[i] = (left + right) * 0.5f;
			}
			// 汉宁窗
			for (int i = 0; i < n; i++)
			{
				double w = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1)));
				fftBuf[i].X = (float)(samples[i] * w);
				fftBuf[i].Y = 0f;
			}
			FastFourierTransform.FFT(true, 10, fftBuf);

			// 24 个对数间隔频段：bin 落入 [bandStart[b], bandStart[b+1])，边界表保证每个频段
			// 至少覆盖 1 个 bin（低音分辨细、高音平滑）。
			Array.Clear(sums, 0, BandCount);
			Array.Clear(counts, 0, BandCount);
			int half = n / 2;
			int hi = Math.Min(bandStart[BandCount], half);
			for (int i = bandStart[0]; i < hi; i++)
			{
				double mag = Math.Sqrt(fftBuf[i].X * fftBuf[i].X + fftBuf[i].Y * fftBuf[i].Y);
				int b = 0;
				while (b < BandCount - 1 && i >= bandStart[b + 1])
				{
					b++;
				}
				sums[b] += mag;
				counts[b]++;
			}

			// 峰值自适应归一（快跟随、快回缩）：先更新各频段峰值，再用“共享峰值”混合
			// 归一（shared 占 35% + 各带峰值占 65%）——各频段独立摆动更明显，跳动感更强，
			// 同时 shared 项兜底防止个别频段被静音段落钉死。
			double shared = 1e-3;
			double[] raw = new double[BandCount];
			for (int b = 0; b < BandCount; b++)
			{
				double avg = counts[b] > 0 ? sums[b] / counts[b] : 0.0;
				peak[b] = Math.Max(avg * 0.9, Math.Max(avg, peak[b] * 0.988));
				shared = Math.Max(shared, peak[b]);
				raw[b] = avg;
			}
			for (int b = 0; b < BandCount; b++)
			{
				double level = Math.Min(1.0, raw[b] / Math.Max(1e-3, 0.35 * shared + 0.65 * peak[b]));
				// 快攻快衰包络：鼓点出现即起跳、落下更快，跟随节奏的频率明显提高。
				double attack = 0.95, decay = 0.74;
				env[b] = Math.Max(level * attack, env[b] * decay);
			}

			AudioLevels?.Invoke((double[])env.Clone());
		}
		catch
		{
			// 采集/FFT 单帧失败直接丢弃，不影响下一帧。
		}
	}
}
