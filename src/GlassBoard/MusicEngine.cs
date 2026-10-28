using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GlassBoard;

public sealed class MusicEngine : IDisposable
{
	private readonly Controller C;

	private MediaPlayer player;

	private readonly MediaSessionBridge bridge = new MediaSessionBridge
	{
		ArtworkOnly = true
	};

	private readonly MediaSessionBridge nativeBridge = new MediaSessionBridge(native: true)
	{
		ArtworkOnly = true
	};

	private MediaSnapshot nativeCache;

	private DateTime nativeAt = DateTime.MinValue;

	private DateTime lastNative = DateTime.MinValue;

	private string nativeStatus = "尚未尝试";

	private string primaryStatus = "尚未尝试";

	private MediaSnapshot discovered;

	private DateTime discoveredAt = DateTime.MinValue;

	private DateTime lastDiscovery = DateTime.MinValue;

	private DateTime nextPrimary = DateTime.MinValue;

	private readonly DispatcherTimer timer = new DispatcherTimer
	{
		Interval = TimeSpan.FromSeconds(1.0)
	};

	private readonly Random random = new Random();

	private bool stopped;

	private bool polling;

	private bool opened;

	private bool playing;

	private int metadataVersion;

	private DateTime lastPoll = DateTime.MinValue;

	private string systemKey = "";

	private string loadedPath = "";

	private bool loadedLocal;

	private DateTime sessionAt = DateTime.UtcNow;

	private DateTime seekUntil = DateTime.MinValue;

	private double? seekTarget;

	private readonly MediaSeekQueue seeks = new MediaSeekQueue();

	private string iconSource = "";

	private string runningIconKey = "";

	private string lastAppPath = "";

	public string Title = "等待音乐";

	public string Artist = "选择本地音乐，或在播放器中开始播放";

	public string Album = "";

	public string Status = "";

	public ImageSource Artwork;

	public ImageSource AppIcon;

	private readonly TrackArtworkCache<ImageSource> artworkCache = new TrackArtworkCache<ImageSource>();

	private DateTime lastAvailable = DateTime.MinValue;

	private DateTime stableMetadataAt = DateTime.MinValue;

	private MediaSnapshot stableMetadata;

	private string lastCoverFingerprint = "";

	public MediaSnapshot Session = new MediaSnapshot();

	private DateTime lastArtworkPoll = DateTime.MinValue;

	public bool Local => C.Prefs.Widgets.MusicMode == "local";

	public bool Playing
	{
		get
		{
			if (!Local)
			{
				return Session.Playing;
			}
			return playing;
		}
	}

	public double Duration
	{
		get
		{
			if (!Local)
			{
				return Session.Duration;
			}
			if (player == null || !opened || !player.NaturalDuration.HasTimeSpan)
			{
				return 0.0;
			}
			return player.NaturalDuration.TimeSpan.TotalSeconds;
		}
	}

	public double Position
	{
		get
		{
			if (Local)
			{
				if (player == null || !opened)
				{
					return 0.0;
				}
				return player.Position.TotalSeconds;
			}
			if (seekTarget.HasValue && DateTime.UtcNow < seekUntil)
			{
				return MediaTimeline.ClampSeek(seekTarget.Value, Session.MinSeek, Session.MaxSeek, Duration);
			}
			return MediaTimeline.Position(Session.Position, Session.Duration, Session.Playing, Session.Rate, Session.Age + (DateTime.UtcNow - sessionAt).TotalSeconds);
		}
	}

	public bool CanSeek
	{
		get
		{
			if (!Local)
			{
				if (Session.Available && Session.CanSeek)
				{
					return Session.Duration > 0.0;
				}
				return false;
			}
			if (player != null && opened)
			{
				return player.NaturalDuration.HasTimeSpan;
			}
			return false;
		}
	}

	public event Action Changed;

	public MusicEngine(Controller c)
	{
		C = c;
		timer.Tick += (object s, EventArgs e) =>
		{
			if (!stopped)
			{
				timer.Interval = TimeSpan.FromSeconds(C.MusicVisible ? 1 : 5);
				if (!Local && (DateTime.UtcNow - lastPoll).TotalSeconds >= BackgroundWorkRules.MediaPollSeconds(C.MusicVisible, Session.Available, Session.Playing))
				{
					Poll();
				}
				DrainSeek();
			}
		};
	}

	public void Wake()
	{
		if (!stopped)
		{
			timer.Interval = TimeSpan.FromSeconds(1.0);
			if (!Local)
			{
				Poll();
			}
		}
	}

	public void Start()
	{
		if (!stopped && !timer.IsEnabled)
		{
			timer.Start();
			if (Local)
			{
				Load(autoplay: false);
			}
			else
			{
				Poll();
			}
		}
	}

	private void EnsurePlayer()
	{
		if (player != null)
		{
			return;
		}
		MediaPlayer mediaPlayer = new MediaPlayer();
		try
		{
			mediaPlayer.Volume = C.Prefs.Widgets.Volume;
			mediaPlayer.MediaOpened += (object s, EventArgs e) =>
			{
				if (!stopped)
				{
					opened = true;
					Notify();
				}
			};
			mediaPlayer.MediaFailed += (object s, ExceptionEventArgs e) =>
			{
				opened = false;
				playing = false;
				Status = "此文件暂时无法播放，请检查音频格式";
				Notify();
			};
			mediaPlayer.MediaEnded += (object s, EventArgs e) =>
			{
				if (!stopped && Local)
				{
					Advance(ended: true);
				}
			};
			player = mediaPlayer;
		}
		catch
		{
			mediaPlayer.Close();
			throw;
		}
	}

	private void Notify()
	{
		if (!stopped && Changed != null)
		{
			Changed();
		}
	}

	public void Update()
	{
		bridge.UseNative = false;
		if (!C.Prefs.Widgets.AutoNativeMediaCompatibility && !C.Prefs.Widgets.NativeMediaCompatibility)
		{
			nativeCache = null;
			lastNative = DateTime.MinValue;
		}
		if (player != null)
		{
			player.Volume = C.Prefs.Widgets.Volume;
		}
		WidgetOptions widgets = C.Prefs.Widgets;
		string text = ((widgets.Playlist.Count == 0) ? "" : widgets.Playlist[widgets.TrackIndex]);
		if (loadedLocal != Local)
		{
			metadataVersion++;
			if (player != null)
			{
				player.Pause();
			}
			playing = false;
			loadedLocal = Local;
			Artwork = null;
			if (Local)
			{
				Load(autoplay: false);
			}
			else
			{
				systemKey = "";
				stableMetadata = null;
				lastCoverFingerprint = "";
				Poll();
			}
		}
		else if (Local && loadedPath != text)
		{
			Load(autoplay: false);
		}
		Notify();
	}

	private async void Load(bool autoplay)
	{
		int version = ++metadataVersion;
		WidgetOptions widgets = C.Prefs.Widgets;
		loadedLocal = true;
		loadedPath = ((widgets.Playlist.Count == 0) ? "" : widgets.Playlist[widgets.TrackIndex]);
		if (widgets.Playlist.Count == 0)
		{
			if (player != null)
			{
				player.Close();
			}
			Title = "选择本地音乐";
			Artist = "支持批量添加播放列表";
			Artwork = null;
			playing = false;
			opened = false;
			Notify();
			return;
		}
		string path = widgets.Playlist[widgets.TrackIndex];
		try
		{
			opened = false;
			EnsurePlayer();
			player.Open(new Uri(path));
			playing = autoplay;
			if (autoplay)
			{
				player.Play();
			}
			Title = Path.GetFileNameWithoutExtension(path);
			Artist = "本地音乐";
			Album = "";
			Artwork = null;
			Status = "";
			Notify();
			TrackMetadata trackMetadata = await Task.Run(() => TrackMetadata.Read(path));
			if (!stopped && Local && version == metadataVersion)
			{
				Title = trackMetadata.Title;
				Artist = trackMetadata.Artist;
				Album = trackMetadata.Album;
				Artwork = Image(trackMetadata.Cover);
				Notify();
			}
		}
		catch (Exception ex)
		{
			playing = false;
			Status = ex.Message;
			Notify();
		}
	}

	public void Pick(Window owner, bool append = false)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Title = (append ? "添加到播放列表" : "选择音乐，可多选"),
			Multiselect = true,
			Filter = "音频|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac|所有文件|*.*"
		};
		if (openFileDialog.ShowDialog(owner) == true)
		{
			WidgetOptions widgets = C.Prefs.Widgets;
			bool num = append && Local && widgets.Playlist.Count > 0;
			if (append)
			{
				widgets.Playlist = widgets.Playlist.Concat(openFileDialog.FileNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			}
			else
			{
				widgets.Playlist = openFileDialog.FileNames.ToList();
				widgets.TrackIndex = 0;
			}
			widgets.MusicMode = "local";
			widgets.Normalize();
			C.Save();
			if (num)
			{
				Notify();
			}
			else
			{
				Load(autoplay: true);
			}
		}
	}

	public void Mode(bool local)
	{
		if (Local != local)
		{
			seeks.Clear();
			seekTarget = null;
			metadataVersion++;
			if (player != null)
			{
				player.Pause();
			}
			playing = false;
			C.Prefs.Widgets.MusicMode = (local ? "local" : "system");
			loadedLocal = local;
			C.Save();
			Artwork = null;
			if (local)
			{
				Load(autoplay: false);
			}
			else
			{
				Title = "等待系统音乐";
				Artist = "在支持媒体会话的播放器中开始播放";
				systemKey = "";
				stableMetadata = null;
				lastCoverFingerprint = "";
				Poll();
			}
			Notify();
		}
	}

	public async void Action(string action)
	{
		if (stopped)
		{
			return;
		}
		if (action != "toggle")
		{
			seeks.Clear();
			seekTarget = null;
		}
		// 乐观切换：点击播放/暂停瞬间先翻转图标状态，系统会话的确认随后续轮询修正
		bool wasPlaying = Session.Playing;
		bool optimisticToggle = action == "toggle" && !Local && Session.Available;
		if (optimisticToggle)
		{
			Session.Playing = !wasPlaying;
			sessionAt = DateTime.UtcNow;
			Notify();
		}
		try
		{
			if (Local)
			{
				if (action == "toggle")
				{
					if (!opened)
					{
						Load(autoplay: true);
						return;
					}
					if (playing)
					{
						player.Pause();
					}
					else
					{
						player.Play();
					}
					playing = !playing;
				}
				else if (action == "previous")
				{
					WidgetOptions widgets = C.Prefs.Widgets;
					if (widgets.Playlist.Count == 0)
					{
						return;
					}
					widgets.TrackIndex = (widgets.TrackIndex - 1 + widgets.Playlist.Count) % widgets.Playlist.Count;
					C.Save();
					Load(autoplay: true);
				}
				else
				{
					Advance(ended: false);
				}
				Notify();
				return;
			}
			while (polling && !stopped && !Local)
			{
				await Task.Delay(50);
			}
			if (stopped || Local)
			{
				return;
			}
			polling = true;
			try
			{
				bool direct = Session.Backend == "NPSMLib direct";
				MediaSnapshot mediaSnapshot = ((!direct) ? (await bridge.Send(action)) : (await nativeBridge.Send(action, 0.0, Session.Key, Session)));
				MediaSnapshot mediaSnapshot2 = mediaSnapshot;
				if (stopped || Local)
				{
					return;
				}
				if (direct)
				{
					lastDiscovery = DateTime.MinValue;
				}
				if (optimisticToggle && mediaSnapshot2 != null && mediaSnapshot2.Available && mediaSnapshot2.Playing == wasPlaying)
				{
					// 系统会话尚未传播新状态：保留乐观翻转，避免图标闪回旧状态
					mediaSnapshot2.Playing = !wasPlaying;
					mediaSnapshot2.Age = 0.0;
				}
				Accept(mediaSnapshot2);
				if (!mediaSnapshot2.Available && !direct)
				{
					// 无音乐会话时绝不发全局媒体键——媒体键由系统路由，可能落在浏览器视频会话上。
					if (action == "toggle")
					{
						// 音乐软件已经打开（只是还没播放）：不再重复拉起（避免"多次尝试启动"弹窗），
						// 只提示用户开始播放；完全没有音乐软件在运行时才唤起上次识别的应用。
						string runningKey2 = MusicPlatforms.RunningPlatformKey();
						if (runningKey2.Length > 0)
						{
							Status = "已检测到 " + MusicPlatforms.FriendlyName(runningKey2) + "，开始播放后即可控制";
						}
						else if (lastAppPath.Length > 0)
						{
							try
							{
								Process.Start(lastAppPath);
								Status = "已为你启动音乐应用";
							}
							catch (Exception ex)
							{
								Status = "启动音乐应用失败：" + ex.Message;
							}
						}
						else
						{
							Status = "未检测到音乐会话，请先在音乐应用中播放";
						}
					}
					else
					{
						Status = "未检测到音乐会话，请先在音乐应用中播放";
					}
				}
			}
			finally
			{
				polling = false;
				DrainSeek();
			}
			Notify();
		}
		catch (Exception ex)
		{
			Status = ex.Message;
			Notify();
		}
	}

	private void Advance(bool ended)
	{
		WidgetOptions widgets = C.Prefs.Widgets;
		int num = WidgetRules.NextTrack(widgets.TrackIndex, widgets.Playlist.Count, widgets.RepeatMode, widgets.Shuffle, random.Next(), ended);
		if (num < 0)
		{
			playing = false;
			if (player != null)
			{
				player.Stop();
			}
			Notify();
		}
		else
		{
			widgets.TrackIndex = num;
			C.Save();
			Load(autoplay: true);
		}
	}

	public void Select(int index)
	{
		WidgetOptions widgets = C.Prefs.Widgets;
		if (index >= 0 && index < widgets.Playlist.Count)
		{
			widgets.TrackIndex = index;
			widgets.MusicMode = "local";
			C.Save();
			Load(autoplay: true);
		}
	}

	public void Remove(int index)
	{
		WidgetOptions widgets = C.Prefs.Widgets;
		if (index >= 0 && index < widgets.Playlist.Count)
		{
			bool flag = index == widgets.TrackIndex;
			widgets.Playlist.RemoveAt(index);
			if (index < widgets.TrackIndex)
			{
				widgets.TrackIndex--;
			}
			widgets.Normalize();
			C.Save();
			if (flag && Local)
			{
				Load(autoplay: false);
			}
			Notify();
		}
	}

	public void Seek(double seconds)
	{
		if (!CanSeek)
		{
			Status = ((Duration > 0.0) ? "播放器未开放进度调整" : "播放器未提供时间轴");
			Notify();
			return;
		}
		seconds = (Local ? LayoutRules.Clamp(seconds, 0.0, Duration, 0.0) : MediaTimeline.ClampSeek(seconds, Session.MinSeek, Session.MaxSeek, Duration));
		if (Local)
		{
			try
			{
				player.Position = TimeSpan.FromSeconds(seconds);
			}
			catch (Exception ex)
			{
				Status = "无法调整播放进度：" + ex.Message;
			}
			Notify();
		}
		else
		{
			seeks.Put(seconds, Session.Key);
			seekTarget = seconds;
			seekUntil = DateTime.UtcNow.AddSeconds(5.0);
			Notify();
			DrainSeek();
		}
	}

	private async void DrainSeek()
	{
		if (stopped || Local || polling || !seeks.Take(Session.Key, out var seconds))
		{
			return;
		}
		string key = Session.Key;
		polling = true;
		try
		{
			bool isolated = Session.Backend == "NPSMLib isolated";
			MediaSnapshot mediaSnapshot = ((!isolated) ? (await bridge.Send("seek", seconds, key)) : (await nativeBridge.Send("seek", seconds, key, Session)));
			MediaSnapshot mediaSnapshot2 = mediaSnapshot;
			if (isolated && mediaSnapshot2 != null && mediaSnapshot2.Available)
			{
				nativeCache = mediaSnapshot2;
				nativeAt = DateTime.UtcNow;
				MediaSnapshot mediaSnapshot3 = MediaFallback.Merge(Session, mediaSnapshot2);
				mediaSnapshot3.Status = mediaSnapshot2.Status;
				mediaSnapshot2 = mediaSnapshot3;
			}
			if (!stopped && !Local)
			{
				bool flag = mediaSnapshot2 != null && mediaSnapshot2.Key == key;
				if (flag && mediaSnapshot2.Available && mediaSnapshot2.Status == "OK")
				{
					Accept(mediaSnapshot2);
					seekTarget = seconds;
					seekUntil = DateTime.UtcNow.AddSeconds(1.5);
					Status = "";
				}
				else
				{
					seekTarget = null;
					Accept(mediaSnapshot2);
					Status = (flag ? "播放器拒绝调整进度" : "曲目或播放器已切换，请重试");
				}
				Notify();
			}
		}
		catch (Exception ex)
		{
			seekTarget = null;
			Status = "无法调整播放进度：" + ex.Message;
			Notify();
		}
		finally
		{
			polling = false;
			if (!stopped && !Local)
			{
				DrainSeek();
			}
		}
	}

	public void SetVolume(double value, bool save = true)
	{
		C.Prefs.Widgets.Volume = LayoutRules.Clamp(value, 0.0, 1.0, 0.6);
		if (player != null)
		{
			player.Volume = C.Prefs.Widgets.Volume;
		}
		if (save)
		{
			C.Save();
		}
		Notify();
	}

	public void SystemVolume(bool up)
	{
		try
		{
			SystemMedia.Key((ushort)(up ? 175 : 174));
		}
		catch (Exception ex)
		{
			Status = ex.Message;
		}
		Notify();
	}

	public void Repeat()
	{
		C.Prefs.Widgets.RepeatMode = (C.Prefs.Widgets.RepeatMode + 1) % 3;
		C.Save();
		Notify();
	}

	public void Shuffle()
	{
		C.Prefs.Widgets.Shuffle = !C.Prefs.Widgets.Shuffle;
		C.Save();
		Notify();
	}

	private MediaSnapshot CachedSupplement()
	{
		if (nativeCache == null)
		{
			return null;
		}
		MediaSnapshot mediaSnapshot = nativeCache.Copy();
		mediaSnapshot.Age += Math.Max(0.0, (DateTime.UtcNow - nativeAt).TotalSeconds);
		return mediaSnapshot;
	}

	private async void Poll()
	{
		if (polling || stopped || Local)
		{
			return;
		}
		polling = true;
		lastPoll = DateTime.UtcNow;
		try
		{
			bool flag = string.IsNullOrEmpty(systemKey) || (DateTime.UtcNow - lastArtworkPoll).TotalSeconds >= 10.0;
			if (flag)
			{
				lastArtworkPoll = DateTime.UtcNow;
			}
			bool primaryDue = DateTime.UtcNow >= nextPrimary;
			MediaSnapshot mediaSnapshot = (primaryDue ? ((await bridge.Send(flag ? "cover" : "refresh")) ?? new MediaSnapshot
			{
				Status = "系统媒体桥未返回数据"
			}) : new MediaSnapshot
			{
				Status = primaryStatus
			});
			MediaSnapshot data = mediaSnapshot;
			if (stopped || Local)
			{
				return;
			}
			primaryStatus = data.Status ?? "未返回状态";
			if (!data.Available || string.IsNullOrWhiteSpace(data.Title))
			{
				if ((DateTime.UtcNow - lastDiscovery).TotalSeconds >= BackgroundWorkRules.MediaPollSeconds(C.MusicVisible, Session.Available, Session.Playing))
				{
					lastDiscovery = DateTime.UtcNow;
					MediaSnapshot mediaSnapshot2 = await nativeBridge.Send("discover");
					if (stopped || Local)
					{
						return;
					}
					nativeStatus = ((mediaSnapshot2 == null) ? "未返回数据" : mediaSnapshot2.Status);
					if (mediaSnapshot2 != null && mediaSnapshot2.Available && !string.IsNullOrWhiteSpace(mediaSnapshot2.Title))
					{
						discovered = mediaSnapshot2;
						discoveredAt = DateTime.UtcNow;
						if (primaryDue)
						{
							nextPrimary = DateTime.UtcNow.AddSeconds(6.0);
						}
					}
					else
					{
						discovered = null;
					}
				}
				if (discovered != null && (DateTime.UtcNow - discoveredAt).TotalSeconds < 4.0)
				{
					data = discovered.Copy();
					data.Age += (DateTime.UtcNow - discoveredAt).TotalSeconds;
				}
				else
				{
					data.Status = "主通道：" + primaryStatus + "；兼容通道：" + nativeStatus;
				}
			}
			else
			{
				discovered = null;
				nextPrimary = DateTime.MinValue;
			}
			bool flag2 = nativeCache != null && nativeCache.Key != data.Key;
			if (flag2)
			{
				nativeCache = null;
			}
			bool flag3 = C.Prefs.Widgets.AutoNativeMediaCompatibility || C.Prefs.Widgets.NativeMediaCompatibility;
			Accept(flag3 ? MediaFallback.Merge(data, CachedSupplement()) : data);
			Notify();
			bool flag4 = data.Available && data.Backend != "NPSMLib direct" && string.IsNullOrEmpty(data.Cover) && Artwork == null;
			if (!(flag3 & flag4) || (!flag2 && !((DateTime.UtcNow - lastNative).TotalSeconds >= 30.0)))
			{
				return;
			}
			lastNative = DateTime.UtcNow;
			MediaSnapshot mediaSnapshot3 = await nativeBridge.Send("cover", 0.0, data.Key, data);
			if (!stopped && !Local && !(Session.Key != data.Key))
			{
				ref string reference = ref nativeStatus;
				string text;
				if (mediaSnapshot3 == null)
				{
					text = "未返回数据";
				}
				else
				{
					text = (mediaSnapshot3.Available ? (mediaSnapshot3.Status + "；封面：" + mediaSnapshot3.CoverStatus) : mediaSnapshot3.Status);
				}
				reference = text;
				if (mediaSnapshot3 != null && mediaSnapshot3.Available && mediaSnapshot3.Key == data.Key)
				{
					nativeCache = mediaSnapshot3;
					nativeAt = DateTime.UtcNow;
					Accept(MediaFallback.Merge(data, mediaSnapshot3));
					Notify();
				}
			}
		}
		catch (Exception ex)
		{
			Status = "音乐读取异常：" + ex.GetBaseException().Message;
			Store.Log(ex);
			Notify();
		}
		finally
		{
			polling = false;
		}
	}

	public string Diagnostics()
	{
		MediaSnapshot session = Session;
		string sessionDump = "";
		try
		{
			// 实时枚举系统全部媒体会话（含被过滤的），用于远程定位"播放器为何读不到"
			System.Threading.Tasks.Task<MediaSnapshot> diagTask = bridge.Send("diagnose");
			if (diagTask.Wait(12000))
			{
				sessionDump = diagTask.Result?.Value ?? "";
			}
		}
		catch
		{
		}
		string dumpLine = ((sessionDump.Length == 0) ? "会话枚举失败或超时" : sessionDump);
		return "GlassBoard-Source v" + Assembly.GetExecutingAssembly().GetName().Version?.ToString() + "\n程序位置：" + Assembly.GetExecutingAssembly().Location + "\n主通道：" + primaryStatus + "\n读取后端：" + session.Backend + "\n播放器：" + session.Source + "\n曲目：" + Title + "\n封面：" + session.CoverStatus + "\n曲目信息：" + session.MetadataStatus + "\n封面补充：" + C.Prefs.Widgets.AutoNativeMediaCompatibility + "\n兼容通道：" + nativeStatus + "\n状态：" + session.Status + "\n会话列表（AUMID | 类型 | 状态 | 标题 | 艺术家）：\n" + dumpLine;
	}

	private void Accept(MediaSnapshot data)
	{
		sessionAt = DateTime.UtcNow;
		// 音乐来源判定：黑名单制，只拦截浏览器/视频站等已知非音乐源，其余播放器默认放行
		if (data != null && data.Available && !MusicPlatforms.IsMusicSource(data.Source ?? ""))
		{
			data.Available = false;
			data.Status = "非音乐来源已忽略（浏览器/视频站）";
		}
		Session = MusicMetadataRecovery.Recover(((DateTime.UtcNow - stableMetadataAt).TotalSeconds < 5.0) ? stableMetadata : null, data ?? new MediaSnapshot());
		ref string status = ref Status;
		string text;
		if (Session.Available)
		{
			text = ((Session.Status != "OK") ? "播放器暂不支持此操作" : "");
		}
		else
		{
			// 没有音乐会话属于正常状态（用户还没播放音乐），静默显示占位文案；
			// 只有桥本身异常（非"无会话"类提示）才保留错误文案。
			string raw = Session.Status ?? "";
			text = (((raw.Contains("Start playback") || raw.Contains("未找到可读取") || raw.Contains("未获得会话") || raw.Length == 0) ? "" : ("音乐读取异常 · " + raw)));
		}
		status = text;
		if (!Session.Available)
		{
			// 打开音乐软件但还没播放任何曲目：GSMTC 会话往往尚未注册，
			// 用进程检测给出"已启动"占位（平台名 + 等待播放曲目）。
			string runningKey = MusicPlatforms.RunningPlatformKey();
			if (runningKey.Length > 0)
			{
				if (runningIconKey != runningKey)
				{
					runningIconKey = runningKey;
					LoadAppIcon(runningKey);
				}
				string friendlyName = MusicPlatforms.FriendlyName(runningKey);
				string windowTitle = MusicPlatforms.WindowTitleFor(runningKey);
				// 很多播放器把当前曲目写在窗口标题里（如 "晴天 - 周杰伦 - 网易云音乐"）：
				// 没注册媒体会话也能读出真实曲目，而不是只显示平台名。
				string track = "";
				string artist = "";
				if (windowTitle.Length > 0 && !windowTitle.Equals(friendlyName, StringComparison.OrdinalIgnoreCase))
				{
					int idx = windowTitle.LastIndexOf(" - ", StringComparison.OrdinalIgnoreCase);
					if (idx > 0 && windowTitle.Substring(idx + 3).IndexOf(friendlyName, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						windowTitle = windowTitle.Substring(0, idx).Trim();
					}
					int split = windowTitle.IndexOf(" - ", StringComparison.OrdinalIgnoreCase);
					if (split > 0)
					{
						track = windowTitle.Substring(0, split).Trim();
						artist = windowTitle.Substring(split + 3).Trim();
					}
					else
					{
						track = windowTitle;
					}
				}
				if (track.Length > 0)
				{
					Title = track;
					Artist = ((artist.Length > 0) ? artist : friendlyName);
					Status = "";
					Artwork = null;
					seekTarget = null;
					if (Session != null)
					{
						Session.Backend = "window-title";
						Session.Source = "app:" + runningKey;
					}
					return;
				}
				Title = friendlyName;
				Artist = "已启动 · 等待播放曲目";
				Status = "";
				Artwork = null;
				seekTarget = null;
				if (Session != null)
				{
					Session.Backend = "window-title";
					Session.Source = "app:" + runningKey;
				}
				return;
			}
			runningIconKey = "";
			if (string.IsNullOrEmpty(systemKey) || !((DateTime.UtcNow - lastAvailable).TotalSeconds < 5.0))
			{
				Title = "等待系统音乐";
				Artist = "开始播放后显示曲名与封面";
				Artwork = null;
				AppIcon = null;
				iconSource = "";
				seekTarget = null;
				systemKey = "";
			}
			return;
		}
		runningIconKey = "";
		lastAvailable = DateTime.UtcNow;
		if (Session.MetadataStatus == "OK")
		{
			stableMetadata = Session.Copy();
			stableMetadataAt = DateTime.UtcNow;
		}
		string friendly = MusicPlatforms.FriendlyName(Session.Source ?? "");
		bool noTrack = string.IsNullOrWhiteSpace(Session.Title) && string.IsNullOrWhiteSpace(Session.Artist);
		if (string.IsNullOrWhiteSpace(Session.Title))
		{
			// 首次打开音乐软件、还没播放任何曲目：显示平台名，而不是误导性的"正在播放"
			Title = ((Session.Playing && noTrack) ? "正在播放" : ((friendly.Length > 0) ? friendly : "已连接音乐播放器"));
		}
		else
		{
			Title = Session.Title;
		}
		string fallback = friendly;
		if (fallback.Length == 0)
		{
			// 未知来源时显示来源本身（去掉 app:/pid: 前缀），而不是伪造"系统播放器"
			string src = (Session.Source ?? "").Trim();
			int idx = src.IndexOf(':');
			fallback = ((idx > 0 && idx < 6) ? src.Substring(idx + 1).Trim() : src);
		}
		Artist = (string.IsNullOrWhiteSpace(Session.Artist) ? ((noTrack && !Session.Playing) ? "等待播放曲目" : ((fallback.Length == 0) ? "未知音乐来源" : fallback)) : Session.Artist);
		Album = Session.Album ?? "";
		if (!string.IsNullOrEmpty(Session.MetadataStatus) && Session.MetadataStatus != "OK" && !noTrack)
		{
			Status = "曲目信息读取失败 · " + Session.MetadataStatus;
		}
		if (Session.Key != systemKey)
		{
			systemKey = Session.Key;
			Artwork = artworkCache.Get(systemKey, DateTime.UtcNow);
			lastCoverFingerprint = "";
			seekTarget = null;
		}
		if (iconSource != (Session.Source ?? ""))
		{
			iconSource = Session.Source ?? "";
			AppIcon = null;
			LoadAppIcon(iconSource);
		}
		if (string.IsNullOrEmpty(Session.Cover))
		{
			return;
		}
		string text2 = CoverFingerprint.Of(Session.Cover);
		if (!(text2 != lastCoverFingerprint))
		{
			return;
		}
		try
		{
			ImageSource imageSource = Image(Convert.FromBase64String(Session.Cover));
			if (imageSource != null)
			{
				Artwork = imageSource;
				lastCoverFingerprint = text2;
				artworkCache.Put(systemKey, imageSource, DateTime.UtcNow);
			}
		}
		catch
		{
		}
	}

	private async void LoadAppIcon(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return;
		}
		Tuple<string, bool> text = await Task.Run(() =>
		{
			string path = null;
			bool known = false;
			// 快速通道：已知播放器按进程名直接取路径，跳过全量进程扫描
			string knownExe = MusicPlatforms.KnownExecutable(source);
			if (!string.IsNullOrEmpty(knownExe))
			{
				try
				{
					Process[] byName = Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(knownExe));
					foreach (Process process in byName)
					{
						try
						{
							string fileName = process.MainModule.FileName;
							if (System.IO.File.Exists(fileName))
							{
								path = fileName;
								break;
							}
						}
						catch
						{
						}
						finally
						{
							process.Dispose();
						}
					}
				}
				catch
				{
				}
				if (path != null)
				{
					known = true;
					return Tuple.Create(path, known);
				}
			}
			int num = 0;
			Process[] processes = Process.GetProcesses();
			foreach (Process process in processes)
			{
				try
				{
					string processName = process.ProcessName;
					if (processName.Length >= 3 && processName.Length > num && source.IndexOf(processName, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						string fileName = process.MainModule.FileName;
						if (File.Exists(fileName))
						{
							path = fileName;
							num = processName.Length;
						}
					}
				}
				catch
				{
				}
				finally
				{
					process.Dispose();
				}
			}
			return Tuple.Create(path, known);
		});
		if (stopped || Local || iconSource != source)
		{
			return;
		}
		try
		{
			if (text.Item1 != null)
			{
				AppIcon = Native.Icon(text.Item1);
				// 仅把白名单内的音乐平台记为可一键唤起的应用，浏览器等不记录
				if (text.Item2)
				{
					lastAppPath = text.Item1;
				}
			}
		}
		catch
		{
		}
		Notify();
	}

	public static ImageSource Image(byte[] bytes)
	{
		if (bytes == null || bytes.Length == 0)
		{
			return null;
		}
		try
		{
			return ThumbnailImages.Read(bytes, 600);
		}
		catch
		{
			return null;
		}
	}

	public void Dispose()
	{
		stopped = true;
		metadataVersion++;
		timer.Stop();
		nativeBridge.Dispose();
		try
		{
			if (player != null)
			{
				player.Close();
			}
		}
		finally
		{
			bridge.Dispose();
			Changed = null;
		}
	}
}
