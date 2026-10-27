using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace GlassBoard;

// 应用通知中心：监听 Windows 10/11 系统通知，把通知归属到 Dock 入口并点亮红点徽标。
// 设计要点：
//  - 事件驱动优先：NotificationChanged 只入队，单一后台 worker 串行处理，UI 线程零阻塞；
//    系统不支持通知事件（add_NotificationChanged 抛 0x80070490 的已知情况）时回退为
//    10 秒轮询 GetNotificationsAsync + 增量对账，行为等价只是延迟稍大；
//  - 增量更新：只对状态发生变化的入口调用 Dock.NotifyBadge，不整体刷新 Dock；
//  - 不伪造未读数：仅维护“是否有未处理通知”的布尔状态，徽标一律显示红点；
//  - 自愈：周期全量对账弥补漏事件/漏轮询，入口快照 30 秒刷新；
//  - 授权：RequestAccessAsync 首次启动时请求；被拒绝或被撤销时功能整体停用并记录日志。
public static class NotificationCenter
{
	private static readonly object gate = new object();
	private static UserNotificationListener listener;
	private static bool started;
	private static Controller controller;

	// 通知归属状态（仅后台线程访问，gate 保护）
	private static Dictionary<uint, string> idToApp = new Dictionary<uint, string>();                 // notificationId -> aumid(小写)
	private static readonly Dictionary<string, HashSet<uint>> active = new Dictionary<string, HashSet<uint>>(); // aumid -> 未处理通知集合
	private static readonly Dictionary<string, long> lastPulse = new Dictionary<string, long>();      // aumid -> 上次弹跳 TickCount（2 秒防抖）

	// 用户已处理（点击打开应用）的通知 id：对账时跳过。QQ 等应用把已读通知长期留在
	// 通知中心，Removed 事件不会到来，需要靠“点开应用”来熄灭；系统移除时清理标记。
	private static readonly HashSet<uint> seenIds = new HashSet<uint>();
	// 当前点亮中的入口路径（原样）：激活清理时判断是否需要熄灭
	private static readonly HashSet<string> litPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	// aumid -> 最近归属的入口路径（激活清理时反查用）
	private static readonly Dictionary<string, string> aumidToPath = new Dictionary<string, string>();

	// 待处理事件队列 + 单 worker
	private static readonly Queue<(uint Id, bool Added)> pending = new Queue<(uint, bool)>();
	private static bool workerRunning;

	// 入口快照与缓存
	private static List<Entry> entrySnapshot = new List<Entry>();
	private static readonly Dictionary<string, (string Aumid, long Stamp)> lnkAumidCache = new Dictionary<string, (string, long)>();
	private static readonly Dictionary<string, (string Target, long Stamp)> lnkTargetCache = new Dictionary<string, (string, long)>();

	// 未打包应用的回退 AUMID 索引（{用户GUID}\Tencent\Weixin\Weixin.exe 形式），
	// 从 Explorer FeatureUsage\AppLaunch 值名提取：exe 文件名(无扩展名,小写) -> 完整 AUMID
	private static Dictionary<string, string> fallbackAumidIndex;
	private static int fallbackAumidStamp;

	// 未打包（非 MSIX）应用的通知归属：系统不提供 AppInfo，改用「每个候选 AUMID 的
	// 通知历史条数」增量对比——列表变化时，条数唯一增长的应用就是通知来源。
	private static readonly Dictionary<string, int> histCounts = new Dictionary<string, int>(); // aumid(原样) -> 历史条数
	private static HashSet<uint> lastIds = new HashSet<uint>();
	private static bool histSeeded;
	private static int histCheckRunning;
	private static int lastHistCheckTick;

	private static DispatcherTimer snapshotTimer;
	private static System.Threading.Timer resyncTimer;

	public static void Start(Controller c)
	{
		if (started)
		{
			return;
		}
		started = true;
		controller = c;
		try
		{
			if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
			{
				Store.Trace("NOTIFY listener type not present (system too old)");
				return;
			}
			listener = UserNotificationListener.Current;
			UserNotificationListenerAccessStatus status = listener.GetAccessStatus();
			Store.Trace("NOTIFY access status=" + status);
			if (status == UserNotificationListenerAccessStatus.Unspecified)
			{
				// 首次运行：请求授权（API 要求 UI 线程，此处即 UI 线程；Wait 不会死锁，完成由系统 RPC 驱动）
				Task<UserNotificationListenerAccessStatus> req = listener.RequestAccessAsync().AsTask();
				if (!req.Wait(8000))
				{
					Store.Trace("NOTIFY access request timeout");
					return;
				}
				status = req.Result;
				Store.Trace("NOTIFY access requested -> " + status);
			}
			if (status != UserNotificationListenerAccessStatus.Allowed)
			{
				Store.Trace("NOTIFY access denied — 通知红点停用（可在 Windows 设置里授权）");
				return;
			}
			// 事件订阅优先：部分系统上 add_NotificationChanged 会抛 0x80070490（找不到元素，系统
			// 通知事件代理缺失的已知问题），此时回退为轮询 GetNotificationsAsync 增量对账。
			bool eventAvailable = false;
			try
			{
				listener.NotificationChanged += OnNotificationChanged;
				eventAvailable = true;
			}
			catch (Exception ex)
			{
				Store.Trace("NOTIFY event unavailable (" + ex.Message + ") — fallback to polling");
			}
			RefreshEntrySnapshot();
			snapshotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
			snapshotTimer.Tick += (s, e) => RefreshEntrySnapshot();
			snapshotTimer.Start();
			// 初始对账：只同步徽标状态，不播弹跳动画
			Task.Run(() => SyncSnapshot(false));
			// 周期对账：有事件时 60 秒兜底；无事件（轮询模式）时 10 秒一拍保持响应速度
			int pollMs = eventAvailable ? 60000 : 3000; // 事件不可用时提高轮询频率，缩短漏报窗口
			resyncTimer = new System.Threading.Timer(_ => SyncSnapshot(true), null, TimeSpan.FromMilliseconds(pollMs), TimeSpan.FromMilliseconds(pollMs));
			Store.Trace("NOTIFY started event=" + eventAvailable + " pollMs=" + pollMs);
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
	}

	// 事件入口（通知中心回调线程）：只入队，尽快返回
	private static void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
	{
		lock (gate)
		{
			pending.Enqueue((args.UserNotificationId, args.ChangeKind == UserNotificationChangedKind.Added));
			if (workerRunning)
			{
				return;
			}
			workerRunning = true;
		}
		Task.Run(ProcessQueue);
	}

	private static void ProcessQueue()
	{
		for (; ; )
		{
			(uint Id, bool Added) item;
			lock (gate)
			{
				if (pending.Count == 0)
				{
					workerRunning = false;
					return;
				}
				item = pending.Dequeue();
			}
			ProcessItem(item);
		}
	}

	private static void ProcessItem((uint Id, bool Added) item)
	{
		try
		{
			if (item.Added)
			{
				UserNotification n = OnUi(() => listener.GetNotification(item.Id));
				if (n == null)
				{
					return; // 通知在取回前已被移除
				}
				string aumid = "";
				string display = "";
				try
				{
					aumid = (n.AppInfo?.AppUserModelId ?? "").ToLowerInvariant();
					display = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
				}
				catch
				{
				}
				lock (gate)
				{
					idToApp[item.Id] = aumid;
					if (!active.TryGetValue(aumid, out HashSet<uint> set))
					{
						set = new HashSet<uint>();
						active[aumid] = set;
					}
					set.Add(item.Id);
				}
				if (aumid.Length == 0)
				{
					// 未打包应用的通知取不到 AppInfo：走历史条数对比归属
					Task.Run(() => CheckHistoryCounts(true));
					return;
				}
				ResolveAndDispatch(aumid, true, true, display);
			}
			else
			{
				bool fallback = false;
				lock (gate)
				{
					seenIds.Remove(item.Id); // 系统已移除：清理已读标记
if (idToApp.TryGetValue(item.Id, out string aumid))
					{
						idToApp.Remove(item.Id);
						if (active.TryGetValue(aumid, out HashSet<uint> set))
						{
							set.Remove(item.Id);
							if (set.Count == 0)
							{
								active.Remove(aumid);
								lastPulse.Remove(aumid);
								if (aumid.Length > 0)
								{
									ResolveAndDispatch(aumid, false, false, "");
								}
								else
								{
									fallback = true;
								}
							}
						}
					}
				}
				if (fallback)
				{
					// 未打包应用的通知被移除：历史条数对比熄灯
					Task.Run(() => CheckHistoryCounts(false));
				}
			}
		}
		catch (Exception ex)
		{
			Store.Trace("NOTIFY process error " + ex.Message);
		}
	}

	// 全量对账：与系统当前通知列表逐项比对，修正徽标状态。
	// listener 是线程仿射（STA/UI）对象，跨线程调用会抛 RPC_E_WRONG_THREAD(0x8001010E)，
	// 因此 WinRT 检索一律经 OnUi 在 UI 线程执行，差异计算仍留在后台线程。
	private static void SyncSnapshot(bool pulse)
	{
		try
		{
			UserNotificationListener l = listener;
			if (l == null)
			{
				return;
			}
			if (OnUi(() => l.GetAccessStatus()) != UserNotificationListenerAccessStatus.Allowed)
			{
				return;
			}
			IReadOnlyList<UserNotification> list = OnUi(() => l.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult());
			if (list == null)
			{
				return;
			}
			HashSet<uint> allIds = new HashSet<uint>();
			foreach (UserNotification n in list)
			{
				allIds.Add(n.Id);
			}
			HashSet<uint> newIds;
			lock (gate)
			{
				Dictionary<string, HashSet<uint>> current = new Dictionary<string, HashSet<uint>>();
				Dictionary<uint, string> newIdToApp = new Dictionary<uint, string>();
				foreach (UserNotification n in list)
				{
					bool seen;
					lock (gate)
					{
						seen = seenIds.Contains(n.Id);
					}
					if (seen)
					{
						continue; // 用户已点开该应用：这条通知不再当作未处理
					}
					try
					{
						string aumid = (n.AppInfo?.AppUserModelId ?? "").ToLowerInvariant();
						newIdToApp[n.Id] = aumid;
						if (!current.TryGetValue(aumid, out HashSet<uint> set))
						{
							set = new HashSet<uint>();
							current[aumid] = set;
						}
						set.Add(n.Id);
					}
					catch
					{
						// 未打包应用的通知 AppInfo 不可用：仍记录 id（aumid 留空），
						// 保证 id 集合变化能触发下面的历史条数对比归属。
						newIdToApp[n.Id] = "";
					}
				}
				// 已点亮但系统里没有了 -> 熄灭
				foreach (string aumid in active.Keys.ToList())
				{
					if (!current.ContainsKey(aumid))
					{
						active.Remove(aumid);
						lastPulse.Remove(aumid);
						ResolveAndDispatch(aumid, false, false, "");
					}
				}
				// 已知应用：对比新旧通知集合，新增的通知（且允许动画时）触发一次弹跳
				foreach (KeyValuePair<string, HashSet<uint>> kv in current)
				{
					if (!active.TryGetValue(kv.Key, out HashSet<uint> oldSet))
					{
						// 系统里有但我们不知道 -> 点亮（周期对账才播动画，初始 seed 不播）
						active[kv.Key] = kv.Value;
						if (pulse)
						{
							ResolveAndDispatch(kv.Key, true, true, "");
						}
					}
					else if (pulse && oldSet.Count > 0 && kv.Value.Any(id => !oldSet.Contains(id)))
					{
						// 轮询模式下新通知到达：更新集合并弹跳（2 秒防抖在 ResolveAndDispatch 内）
						active[kv.Key] = kv.Value;
						ResolveAndDispatch(kv.Key, true, true, "");
					}
					else
					{
						active[kv.Key] = kv.Value;
					}
				}
				idToApp = newIdToApp;
				newIds = new HashSet<uint>(newIdToApp.Keys);
				// 清理已读标记：系统里已经不存在的 id 不再需要标记
				seenIds.IntersectWith(allIds);
			}
			bool changed;
			lock (gate)
			{
				changed = !lastIds.SetEquals(newIds);
			}
			if (changed)
			{
				CheckHistoryCounts(pulse);
				lock (gate)
				{
					lastIds = newIds;
				}
			}
		}
		catch (Exception ex)
		{
			Store.Trace("NOTIFY sync error " + ex.Message);
		}
	}

	// 未打包应用通知归属：对比各候选 AUMID 的 ToastNotificationManager.History 条数。
	// 系统没有公开接口给出这类通知的发送方（AppInfo 仅适用于 MSIX 应用），
	// 因此在通知列表发生变化时，唯一出现条数增长的应用即视为来源；多个同时增长则跳过（不猜）。
	private static void CheckHistoryCounts(bool pulse)
	{
		if (Interlocked.Exchange(ref histCheckRunning, 1) != 0)
		{
			return; // 单飞：上一次对比尚未完成
		}
		try
		{
			int tick = Environment.TickCount;
			if (tick - lastHistCheckTick < 1500)
			{
				return; // 事件风暴时最多 1.5 秒一次
			}
			lastHistCheckTick = tick;
			Controller c = controller;
			if (c == null || listener == null)
			{
				return;
			}
			Dictionary<string, string> candidates = CollectCandidates();
			if (candidates.Count == 0)
			{
				Store.Trace("NOTIFY hist no candidates");
				return;
			}
			// 逐候选读取当前历史条数（WinRT 调用走 UI 线程）
			Dictionary<string, int> now = new Dictionary<string, int>();
			foreach (string aumid in candidates.Keys)
			{
				try
				{
					int count = OnUi(() => ToastNotificationManager.History.GetHistory(aumid).Count);
					now[aumid] = count;
				}
				catch (Exception ex)
				{
					Store.Trace("NOTIFY hist read error " + ex.Message);
				}
			}
			lock (gate)
			{
				if (!histSeeded)
				{
					// 首次只打基线，不点亮：启动时已存在的通知不算“新通知”
					foreach (KeyValuePair<string, int> kv in now)
					{
						histCounts[kv.Key] = kv.Value;
					}
					histSeeded = true;
					Store.Trace("NOTIFY hist seeded candidates=" + candidates.Count);
					return;
				}
				// 新出现的候选（例如应用刚启动、进程 AUMID 变得可见）先打基线不点亮
				foreach (KeyValuePair<string, int> kv in now)
				{
					if (!histCounts.ContainsKey(kv.Key))
					{
						histCounts[kv.Key] = kv.Value;
						Store.Trace("NOTIFY hist baseline aumid=" + kv.Key + " count=" + kv.Value);
					}
				}
				List<KeyValuePair<string, int>> increased = now.Where(kv => histCounts.TryGetValue(kv.Key, out int old) && kv.Value > old).ToList();
				List<KeyValuePair<string, int>> decreased = now.Where(kv => histCounts.TryGetValue(kv.Key, out int old) && kv.Value < old).ToList();
				foreach (KeyValuePair<string, int> kv in now)
				{
					histCounts[kv.Key] = kv.Value;
				}
				if (increased.Count == 1)
				{
					// 唯一增长 -> 确定来源，点亮并弹跳
					DispatchEntry(candidates[increased[0].Key], true, pulse);
				}
				else if (increased.Count > 1)
				{
					Store.Trace("NOTIFY hist ambiguous increased=" + increased.Count + " (skip)");
				}
				foreach (KeyValuePair<string, int> kv in decreased)
				{
					if (kv.Value == 0)
					{
						// 该应用的通知已全部处理 -> 熄灭
						DispatchEntry(candidates[kv.Key], false, false);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Store.Trace("NOTIFY hist error " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref histCheckRunning, 0);
		}
	}

	// 候选 AUMID：Dock 里每个入口的快捷方式 AUMID（.lnk），
	// 为空时回退到与该入口关联的正在运行进程的应用标识（应用运行时通过
	// SetCurrentProcessExplicitAppUserModelID 设置过才有效）。
	private static Dictionary<string, string> CollectCandidates()
	{
		Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		List<Entry> entries;
		lock (gate)
		{
			entries = new List<Entry>(entrySnapshot);
		}
		foreach (Entry entry in entries)
		{
			if (entry.IsFolder || string.IsNullOrEmpty(entry.Path))
			{
				continue;
			}
			string aumid = "";
			if (entry.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
			{
				aumid = ShortcutAumid(entry.Path);
			}
			if (aumid.Length == 0)
			{
				// 快捷方式没有声明 AUMID（很常见）或非快捷方式入口：查运行中进程
				try
				{
					foreach (RunningWindow w in RunningWindows.For(entry))
					{
						if (!string.IsNullOrEmpty(w.AppUserModelId))
						{
							aumid = w.AppUserModelId;
							break;
						}
					}
				}
				catch
				{
				}
			}
			if (aumid.Length > 0 && !map.ContainsKey(aumid))
			{
				map[aumid] = entry.Path;
			}
			// 微信等应用既不写快捷方式 AUMID、进程也不设置显式 AUMID，Windows 按
			// 用户级回退 AUMID（{GUID}\路径\App.exe）归属其通知；用 exe 文件名在
			// FeatureUsage 索引里找到该 AUMID 作为补充候选
			string exeName = Path.GetFileNameWithoutExtension(ExeNameFor(entry));
			if (exeName.Length > 0)
			{
				string fallback = LookupFallbackAumid(Norm(exeName));
				if (fallback.Length > 0 && !map.ContainsKey(fallback))
				{
					map[fallback] = entry.Path;
				}
			}
		}
		return map;
	}

	// 已知入口的直接投递（历史条数对比路径已确定归属，无需再评分）
	private static void DispatchEntry(string path, bool on, bool pulse)
	{
		Store.Trace("NOTIFY badge(hist) on=" + on + " pulse=" + pulse + " entry=" + path);
		Dispatch(path, on, pulse);
	}

	// listener 在 UI 线程创建且线程仿射：所有 WinRT 调用都经此函数调度到 UI 线程同步执行
	// （后台线程阻塞等待；轮询间隔 10s、单次检索毫秒级，代价可忽略）。
	private static T OnUi<T>(Func<T> fn)
	{
		Dispatcher d = controller?.Dock?.Dispatcher;
		if (d == null || d.HasShutdownStarted)
		{
			return default;
		}
		T result = default;
		try
		{
			d.Invoke(DispatcherPriority.Background, new Action(() =>
			{
				result = fn();
			}));
		}
		catch (Exception ex)
		{
			Store.Trace("NOTIFY ui call error " + ex.Message);
		}
		return result;
	}

	// 把 aumid 解析到 Dock 入口并投递 UI 更新（2 秒防抖内重复通知只亮徽标不再弹跳）
	private static void ResolveAndDispatch(string aumid, bool on, bool pulse, string display)
	{
		if (pulse)
		{
			long now = Environment.TickCount;
			if (lastPulse.TryGetValue(aumid, out long t) && now - t < 2000)
			{
				pulse = false;
			}
			else
			{
				lastPulse[aumid] = now;
			}
		}
		string path = Match(aumid, display);
		if (path == null)
		{
			Store.Trace("NOTIFY no match aumid=" + aumid + " display=" + display);
			return;
		}
		if (on)
		{
			lock (gate)
			{
				aumidToPath[aumid] = path;
			}
		}
		Store.Trace("NOTIFY badge on=" + on + " pulse=" + pulse + " aumid=" + aumid + " entry=" + path);
		Dispatch(path, on, pulse);
	}

	private static void Dispatch(string path, bool on, bool pulse)
	{
		lock (gate)
		{
			if (on)
			{
				litPaths.Add(path);
			}
			else
			{
				litPaths.Remove(path);
			}
		}
		Controller c = controller;
		if (c == null)
		{
			return;
		}
		Dispatcher d = c.Dock?.Dispatcher;
		if (d == null || d.HasShutdownStarted)
		{
			return;
		}
		d.BeginInvoke(DispatcherPriority.Background, new Action(() =>
		{
			try
			{
				c.Dock.NotifyBadge(path, on, pulse);
			}
			catch (Exception ex)
			{
				Store.Trace("NOTIFY ui error " + ex.Message);
			}
		}));
	}

	// 用户点击 Dock 图标打开应用（Controller.Launch）时调用：该入口的徽标立即熄灭。
	// 有些应用（QQ 等）把已读通知长期留在 Windows 通知中心，Removed 事件不会到来，
	// 不能只靠系统事件熄灭；点开应用 = 用户已看到消息，按任务栏惯例清掉提醒。
	public static void ClearEntry(string path)
	{
		try
		{
			if (string.IsNullOrEmpty(path))
			{
				return;
			}
			bool dispatch;
			lock (gate)
			{
				List<string> keys = new List<string>();
				foreach (KeyValuePair<string, string> kv in aumidToPath)
				{
					if (string.Equals(kv.Value, path, StringComparison.OrdinalIgnoreCase))
					{
						keys.Add(kv.Key);
					}
				}
				foreach (string aumid in keys)
				{
					if (active.TryGetValue(aumid, out HashSet<uint> set))
					{
						foreach (uint id in set)
						{
							seenIds.Add(id);
						}
						active.Remove(aumid);
					}
					lastPulse.Remove(aumid);
					aumidToPath.Remove(aumid);
				}
				dispatch = keys.Count > 0 || litPaths.Contains(path);
				litPaths.Remove(path);
			}
			if (dispatch)
			{
				Store.Trace("NOTIFY cleared by activation entry=" + path);
				Dispatch(path, false, false);
			}
		}
		catch (Exception ex)
		{
			Store.Trace("NOTIFY clear error " + ex.Message);
		}
	}

	// 通知归属评分：快捷方式 AUMID 精确匹配 100 > 目标名与 AUMID 互含 70 > 显示名与目标名互含 60 > 入口名精确 55
	private static string Match(string aumid, string display)
	{
		List<Entry> entries;
		lock (gate)
		{
			entries = new List<Entry>(entrySnapshot);
		}
		string aumidNorm = Norm(aumid);
		string displayNorm = Norm(display);
		string bestPath = null;
		int bestScore = 0;
		foreach (Entry entry in entries)
		{
			if (entry.IsFolder || string.IsNullOrEmpty(entry.Path))
			{
				continue;
			}
			string exeNorm = Norm(Path.GetFileNameWithoutExtension(entry.Path));
			// 快捷方式入口用目标 exe 名（微信.lnk -> weixinexe），比 .lnk 文件名更能对上 AUMID
			string exeNorm2 = exeNorm;
			if (entry.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
			{
				string target = LnkTarget(entry.Path);
				if (target.Length > 0)
				{
					exeNorm2 = Norm(Path.GetFileNameWithoutExtension(target));
				}
			}
			if (exeNorm.Length < 2 && exeNorm2.Length < 2)
			{
				continue;
			}
			int score = 0;
			if (aumidNorm.Length > 0)
			{
				string shortcutAumid = ShortcutAumid(entry.Path);
				if (shortcutAumid.Length > 0 && aumidNorm == Norm(shortcutAumid))
				{
					score = 100;
				}
				else
				{
					if (exeNorm2.Length >= 2 && aumidNorm.Contains(exeNorm2))
					{
						score = Math.Max(score, 70);
					}
					else if (exeNorm.Length >= 2 && aumidNorm.Contains(exeNorm))
					{
						score = Math.Max(score, 70);
					}
					if (score < 100)
					{
						string fallback = LookupFallbackAumid(exeNorm2.Length >= 2 ? exeNorm2 : exeNorm);
						if (fallback.Length > 0 && aumidNorm == Norm(fallback))
						{
							score = 100;
						}
					}
				}
			}
			if (displayNorm.Length > 0 && ((exeNorm2.Length >= 2 && (displayNorm.Contains(exeNorm2) || exeNorm2.Contains(displayNorm)))
				|| (exeNorm.Length >= 2 && (displayNorm.Contains(exeNorm) || exeNorm.Contains(displayNorm)))))
			{
				score = Math.Max(score, 60);
			}
			if (displayNorm.Length >= 3 && displayNorm == Norm(entry.Name))
			{
				score = Math.Max(score, 55);
			}
			if (score > bestScore)
			{
				bestScore = score;
				bestPath = entry.Path;
			}
		}
		return bestScore >= 55 ? bestPath : null;
	}

	private static string Norm(string s)
	{
		return (s ?? "").ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "").Replace(".", "");
	}

	// 入口对应的 exe 文件名（lnk 走目标路径，其余走入口路径本身）
	private static string ExeNameFor(Entry entry)
	{
		string path = entry.Path ?? "";
		if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
		{
			string target = LnkTarget(path);
			if (target.Length > 0)
			{
				return Path.GetFileName(target);
			}
			return "";
		}
		return Path.GetFileName(path);
	}

	// .lnk 目标路径（按文件时间戳缓存）
	private static string LnkTarget(string path)
	{
		long stamp = 0;
		try
		{
			stamp = File.GetLastWriteTimeUtc(path).Ticks;
		}
		catch
		{
		}
		lock (gate)
		{
			if (lnkTargetCache.TryGetValue(path, out (string Target, long Stamp) cached) && cached.Stamp == stamp)
			{
				return cached.Target;
			}
		}
		string target = "";
		try
		{
			target = Native.ShortcutTarget(path) ?? "";
		}
		catch
		{
		}
		lock (gate)
		{
			lnkTargetCache[path] = (target, stamp);
		}
		return target;
	}

	// Explorer FeatureUsage\AppLaunch 的值名就是每个启动过的应用的 AUMID
	//（未打包应用为 {用户GUID}\路径\App.exe）；按 exe 文件名建立索引，10 分钟缓存
	private static Dictionary<string, string> FallbackAumidIndex()
	{
		int now = Environment.TickCount;
		lock (gate)
		{
			if (fallbackAumidIndex != null && now - fallbackAumidStamp < 600000)
			{
				return fallbackAumidIndex;
			}
		}
		Dictionary<string, string> index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
				@"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppLaunch"))
			{
				if (key != null)
				{
					foreach (string name in key.GetValueNames())
					{
						if (name.StartsWith("{", StringComparison.Ordinal))
						{
							int slash = name.LastIndexOf('\\');
							if (slash > 2 && slash < name.Length - 1)
							{
								string file = name.Substring(slash + 1);
								if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
								{
									index[Norm(Path.GetFileNameWithoutExtension(file))] = name;
								}
							}
						}
					}
				}
			}
		}
		catch
		{
		}
		lock (gate)
		{
			fallbackAumidIndex = index;
			fallbackAumidStamp = now;
		}
		return index;
	}

	private static string LookupFallbackAumid(string exeNorm)
	{
		Dictionary<string, string> index = FallbackAumidIndex();
		if (index.TryGetValue(exeNorm, out string aumid))
		{
			return aumid;
		}
		return "";
	}

	// 快捷方式 AUMID（.lnk 专用，按文件时间戳缓存）
	private static string ShortcutAumid(string path)
	{
		if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
		{
			return "";
		}
		long stamp = 0;
		try
		{
			stamp = File.GetLastWriteTimeUtc(path).Ticks;
		}
		catch
		{
		}
		lock (gate)
		{
			if (lnkAumidCache.TryGetValue(path, out (string Aumid, long Stamp) cached) && cached.Stamp == stamp)
			{
				return cached.Aumid;
			}
		}
		string aumid = "";
		try
		{
			aumid = Native.ShortcutAumid(path) ?? "";
		}
		catch
		{
		}
		lock (gate)
		{
			lnkAumidCache[path] = (aumid, stamp);
		}
		return aumid;
	}

	// 入口快照：UI 线程每 30 秒从与 Dock 相同的排序规则刷新（后台线程只读快照）
	private static void RefreshEntrySnapshot()
	{
		try
		{
			List<Entry> list = DockOrderRules.Ordered(controller.Prefs).ToList();
			lock (gate)
			{
				entrySnapshot = list;
			}
		}
		catch (Exception ex)
		{
			Store.Trace("NOTIFY snapshot error " + ex.Message);
		}
	}
}
