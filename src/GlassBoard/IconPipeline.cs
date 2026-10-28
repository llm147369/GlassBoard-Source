using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBoard;

// 图标后台加载管线：
// 把「磁盘缓存读取 / PNG 解码 / Shell 图标提取 / 缩略图提取 / SVG 光栅化 / 磁盘缓存写入」
// 全部移出 UI 线程。UI 线程只做内存缓存命中检查（FeatureUi.Cached，纯字典查询）；
// 未命中时立即返回 null（调用方先渲染占位符），由单后台 STA 线程串行提取
// （串行避免 Shell COM 并发问题），完成后把已冻结的 ImageSource 派发回 UI 线程回调换图。
// 启动预热（Controller.WarmIconCache）同样走本管线，不再用空闲定时器占用 UI 线程。
public static class IconPipeline
{
	private sealed class Request
	{
		public Entry Entry;
		public bool Overlays;
		public bool Tile;                  // true：磁贴管线 MediaThumb.TileIcon；false：Dock 管线 FeatureUi.Icon
		public Action<ImageSource> Callback;
		public bool DoneSentinel;          // 预热完成哨兵（无图标工作，只回写完成日志）
		public int WarmTotal;
	}

	private static readonly BlockingCollection<Request> queue = new BlockingCollection<Request>();
	private static readonly Thread worker;
	private static Dispatcher ui;

	static IconPipeline()
	{
		worker = new Thread(Work)
		{
			IsBackground = true,
			Name = "IconPipeline"
		};
		worker.SetApartmentState(ApartmentState.STA);
		worker.Start();
	}

	// 请求图标：内存缓存命中 → 同步返回（不触发回调）；未命中 → 返回 null 并后台提取，
	// 完成后在 UI 线程回调。必须在 UI 线程调用。
	public static ImageSource Request(Entry entry, bool overlays, bool sharper, bool tile, Action<ImageSource> onReady)
	{
		if (entry == null)
		{
			return null;
		}
		if (ui == null)
		{
			ui = Dispatcher.CurrentDispatcher;
		}
		ImageSource hit = FeatureUi.Cached(entry, overlays, sharper);
		if (hit != null)
		{
			return hit;
		}
		queue.Add(new Request
		{
			Entry = entry,
			Overlays = overlays,
			Tile = tile,
			Callback = onReady
		});
		return null;
	}

	// 启动预热：整批入队（无回调），后台逐个提取填满内存 + 磁盘缓存
	public static void Warm(List<Entry> entries, bool overlays)
	{
		if (ui == null)
		{
			ui = Dispatcher.CurrentDispatcher;
		}
		if (entries == null)
		{
			return;
		}
		int n = 0;
		foreach (Entry e in entries)
		{
			if (e == null)
			{
				continue;
			}
			n++;
			queue.Add(new Request
			{
				Entry = e,
				Overlays = overlays,
				Tile = true
			});
		}
		queue.Add(new Request
		{
			DoneSentinel = true,
			WarmTotal = n
		});
	}

	private static void Work()
	{
		foreach (Request req in queue.GetConsumingEnumerable())
		{
			if (req.DoneSentinel)
			{
				Store.Trace("WARM icons done n=" + req.WarmTotal);
				continue;
			}
			ImageSource result = null;
			try
			{
				result = (req.Tile ? MediaThumb.TileIcon(req.Entry, req.Overlays) : FeatureUi.Icon(req.Entry, req.Overlays, true));
			}
			catch
			{
			}
			Action<ImageSource> callback = req.Callback;
			if (callback != null)
			{
				Dispatcher dispatcher = ui;
				if (dispatcher != null)
				{
					try
					{
						dispatcher.BeginInvoke((Action)(() => callback(result)), DispatcherPriority.Background);
					}
					catch
					{
					}
				}
			}
		}
	}
}
