using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

// 灵动岛通知内容模型。只保存来源应用、标题、正文和时间戳——不保存任何通知的
// 原始 id，不伪造未读数；正文绝不打日志。内容仅在 UI 线程读写（由 NotificationCenter
// 在 UI 线程上推送）。
public sealed class IslandNotification
{
	public string AppName; // 来源应用显示名；取不到时为空
	public string Aumid; // AppUserModelId（小写）；用于设置页按应用控制，不展示、不记日志
	public string Title;
	public string Body;
	public DateTime Stamp;
	public bool AlwaysFresh; // 弹窗通道专用：同应用同标题同正文的新消息也要重新弹卡（去重由通道自身负责）
}

// 灵动岛通知的中央存储：最近 N 条 + 事件。IslandWindow 订阅 Arrived 显示事件卡与列表。
public static class IslandNotifications
{
	private const int MaxItems = 50;
	private static readonly List<IslandNotification> recent = new List<IslandNotification>();

	public static event Action<IslandNotification> Arrived;
	public static event Action Cleared;

	public static List<IslandNotification> Recent()
	{
		lock (recent)
		{
			return recent.ToList();
		}
	}

	// NotificationCenter 在 UI 线程调用。
	public static void Push(IslandNotification n)
	{
		if (n == null)
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(n.AppName) && string.IsNullOrWhiteSpace(n.Title) && string.IsNullOrWhiteSpace(n.Body))
		{
			return; // 无任何可读内容：不猜测、不提示
		}
		lock (recent)
		{
			// 不再按“同应用同标题同正文”去重：聊天软件同一句“在吗”连发是正常消息，
			// 按内容合并会吞掉真实通知。去重由各通道按系统通知 ID 完成
			//（NotificationCenter.islandSeen；微信闪烁通道用 AlwaysFresh）。
			recent.Insert(0, n);
			while (recent.Count > MaxItems)
			{
				recent.RemoveAt(recent.Count - 1);
			}
		}
		Arrived?.Invoke(n);
	}

	public static void Clear()
	{
		lock (recent)
		{
			recent.Clear();
		}
		Cleared?.Invoke();
	}

	// 打开对应应用后按谓词清理“已看”通知；返回实际移除条数。
	public static int RemoveMatching(Func<IslandNotification, bool> pred)
	{
		int removed = 0;
		lock (recent)
		{
			removed = recent.RemoveAll(delegate (IslandNotification n)
			{
				try
				{
					return pred(n);
				}
				catch
				{
					return false;
				}
			});
		}
		return removed;
	}
}
