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
	private static readonly Complex[] fftBuf = new Complex[fftLength];
	private static readonly float[] samples = new float[fftLength];

	static IslandAudio()
	{
		for (int i = 0; i < BandCount; i++)
		{
			peak[i] = 1e-3;
		}
	}

	public static bool IsRunning
	{
		get { lock (gate) { return running; } }
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
					lock (gate)
					{
						if (ReferenceEquals(capture, cap))
						{
							capture = null;
						}
					}
				};
				capture = cap;
			}
			// 用局部引用启动：RecordingStopped 回调可能在锁释放后恰好清掉字段（设备自行失效），
			// 此时字段二次读取会抛空引用并被 catch 静默吞掉，导致采集永久假死、跳动条冻结。
			cap.StartRecording();
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
			int channels = cap.WaveFormat != null && cap.WaveFormat.Channels > 0 ? cap.WaveFormat.Channels : 2;
			int floatCount = e.BytesRecorded / 4;
			if (floatCount < fftLength)
			{
				return;
			}
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

			// 24 个对数间隔频段（bin 2..255 → 指数映射，低音分辨细、高音平滑）
			Array.Clear(sums, 0, BandCount);
			Array.Clear(counts, 0, BandCount);
			int half = n / 2;
			for (int i = 2; i < 256 && i < half; i++)
			{
				double mag = Math.Sqrt(fftBuf[i].X * fftBuf[i].X + fftBuf[i].Y * fftBuf[i].Y);
				int b = (int)(BandCount * Math.Log(i / 2.0) / Math.Log(127.5));
				if (b < 0)
				{
					b = 0;
				}
				else if (b >= BandCount)
				{
					b = BandCount - 1;
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
