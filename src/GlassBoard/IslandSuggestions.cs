using System;
using System.Collections.Generic;

namespace GlassBoard;

// 收起态中部“动态建议”文案引擎：按 时间问候 + 天气 + 屏幕刷新率 + 今日随手记 组合轮播池。
// 纯文案逻辑，不碰 UI、不写日志；任何一项无数据就跳过该项，绝不伪造。
public static class IslandSuggestions
{
	public static string[] Build(WeatherSnapshot ws, double hz, NoteBook book, int unreadNotifications)
	{
		List<string> list = new List<string>();
		try
		{
			DateTime now = DateTime.Now;
			int hour = now.Hour;
			list.Add(hour < 6 ? "夜深了，记得早点休息" : hour < 9 ? "早上好，新的一天" : hour < 12 ? "上午好，专注高效" : hour < 14 ? "中午好，记得吃午饭" : hour < 18 ? "下午好，继续加油" : hour < 22 ? "晚上好，放松一下" : "夜深了，早点休息");

			if (unreadNotifications > 0)
			{
				list.Add("还有 " + unreadNotifications + " 条消息未处理");
			}

			WeatherCurrent current = (ws == null ? null : ws.Current);
			if (current != null)
			{
				string desc = WeatherPresentation.Description(current.Code, current.Description);
				string temp = current.Temperature.ToString("0");
				if (desc.Contains("雨") || desc.Contains("雷"))
				{
					list.Add("有雨，出门记得带伞");
				}
				else if (desc.Contains("雪"))
				{
					list.Add("下雪了，注意保暖");
				}
				else if (desc.Contains("晴") && current.Temperature >= 30.0)
				{
					list.Add("大晴天，注意防晒补水");
				}
				else if (desc.Contains("晴") && current.Temperature <= 8.0)
				{
					list.Add("天冷，出门多穿一件");
				}
				else if (desc.Contains("雾") || desc.Contains("霾"))
				{
					list.Add("能见度低，出行注意安全");
				}
				else
				{
					list.Add(temp + "° " + desc + "，体感不错");
				}
			}

			if (hz >= 100.0)
			{
				list.Add(hz.ToString("0") + "Hz 高刷满血运行");
			}
			else if (hz >= 1.0)
			{
				list.Add(hz.ToString("0") + "Hz 刷新，画面流畅");
			}

			int count = 0;
			string first = "";
			try
			{
				if (book != null && book.Items != null)
				{
					foreach (NoteItem item in book.Items)
					{
						if (item == null || string.IsNullOrWhiteSpace(item.Body))
						{
							continue;
						}
						count++;
						if (first.Length == 0)
						{
							first = (item.DisplayTitle ?? "").Trim();
						}
					}
				}
			}
			catch
			{
			}
			if (count > 0)
			{
				if (first.Length > 12)
				{
					first = first.Substring(0, 12) + "…";
				}
				list.Add(count > 1 ? ("今天有 " + count + " 条随手记待处理" + (first.Length > 0 ? "：" + first : "")) : ("待办：" + first));
			}
			else
			{
				list.Add("随手记空空的，记点想法吧");
			}

			list.Add("久坐提醒：起来活动一下");
			list.Add("多喝水，保持好状态");
		}
		catch
		{
		}
		if (list.Count == 0)
		{
			list.Add("展开灵动岛查看详情");
		}
		return list.ToArray();
	}
}
