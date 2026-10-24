using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace GlassBoard;

public sealed class NeteaseUiReader
{
	private AutomationElement window;

	private AutomationElement progress;

	private string key = "";

	private string vendorKey = "";

	private DateTime changedAt = DateTime.MinValue;

	private DateTime lastAdvance = DateTime.MinValue;

	private double previous = -1.0;

	private RangeValuePattern seekRange;

	private DateTime lastRangeScan = DateTime.MinValue;

	private AutomationElement FindWindow(MediaRequest r)
	{
		Process[] processesByName = Process.GetProcessesByName("cloudmusic");
		foreach (Process process in processesByName)
		{
			try
			{
				foreach (AutomationElement item in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id)))
				{
					if (NeteaseTimelineRules.WindowTitle(item.Current.Name, r.Title))
					{
						return item;
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
		return null;
	}

	private bool ReadText(AutomationElement element, out double current, out double total)
	{
		current = (total = 0.0);
		try
		{
			return element != null && NeteaseTimelineRules.Progress(element.Current.Name, out current, out total);
		}
		catch
		{
			return false;
		}
	}

	private AutomationElement FindProgress(out double current, out double total)
	{
		current = (total = 0.0);
		foreach (AutomationElement item in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
		{
			if (ReadText(item, out current, out total))
			{
				return item;
			}
		}
		return null;
	}

	private RangeValuePattern FindSeekRange()
	{
		if (window == null || progress == null)
		{
			return null;
		}
		if (seekRange != null)
		{
			try
			{
				if (!seekRange.Current.IsReadOnly && seekRange.Current.Maximum > seekRange.Current.Minimum)
				{
					return seekRange;
				}
			}
			catch
			{
			}
			seekRange = null;
		}
		if ((DateTime.UtcNow - lastRangeScan).TotalSeconds < 3.0)
		{
			return null;
		}
		lastRangeScan = DateTime.UtcNow;
		Rect boundingRectangle;
		try
		{
			boundingRectangle = progress.Current.BoundingRectangle;
		}
		catch
		{
			return null;
		}
		AutomationElementCollection automationElementCollection = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsRangeValuePatternAvailableProperty, true));
		RangeValuePattern result = null;
		double num = 0.0;
		foreach (AutomationElement item in automationElementCollection)
		{
			try
			{
				string text = item.Current.Name ?? "";
				if (text.IndexOf("音量", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("volume", StringComparison.OrdinalIgnoreCase) >= 0 || !item.TryGetCurrentPattern(RangeValuePattern.Pattern, out var patternObject))
				{
					continue;
				}
				RangeValuePattern rangeValuePattern = (RangeValuePattern)patternObject;
				if (rangeValuePattern.Current.IsReadOnly || rangeValuePattern.Current.Maximum <= rangeValuePattern.Current.Minimum)
				{
					continue;
				}
				Rect boundingRectangle2 = item.Current.BoundingRectangle;
				bool flag = text.IndexOf("进度", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("progress", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("seek", StringComparison.OrdinalIgnoreCase) >= 0;
				if (flag || (!boundingRectangle2.IsEmpty && !boundingRectangle.IsEmpty && !(boundingRectangle2.Width < 200.0) && !(Math.Abs(boundingRectangle2.Top - boundingRectangle.Top) > 65.0)))
				{
					double num2 = (double)(flag ? 100000 : 0) + boundingRectangle2.Width;
					if (num2 > num)
					{
						result = rangeValuePattern;
						num = num2;
					}
				}
			}
			catch
			{
			}
		}
		seekRange = result;
		return result;
	}

	public MediaSnapshot Read(MediaRequest r)
	{
		MediaSnapshot mediaSnapshot = new MediaSnapshot
		{
			Key = r.Key,
			Source = r.Source,
			Backend = "Netease UIA",
			Status = "未找到网易云进度文本；请显示主窗口并将鼠标移到播放器进度条",
			TimelineStatus = "未找到进度文本"
		};
		if (!NeteaseTimelineRules.Source(r.Source))
		{
			return mediaSnapshot;
		}
		if (r.Key != vendorKey)
		{
			NeteaseMusicHelper.SetTrack(null);
			vendorKey = r.Key;
		}
		NeteaseMusicHelper.SetTrack(r.Title);
		NeteaseMusicHelper.SetPlaying(r.Playing);
		if (r.Command != "seek" && NeteaseMusicHelper.TryGetCachedProgress(out var currentSec, out var totalSec))
		{
			mediaSnapshot.Available = true;
			mediaSnapshot.Position = currentSec;
			mediaSnapshot.Duration = totalSec;
			mediaSnapshot.MaxSeek = totalSec;
			mediaSnapshot.Rate = 0.0;
			mediaSnapshot.CanSeek = false;
			mediaSnapshot.Status = "OK";
			mediaSnapshot.TimelineStatus = "Now Playing COM UIA；真实进度（只读）";
			return mediaSnapshot;
		}
		if (r.Key != key)
		{
			key = r.Key;
			progress = null;
			seekRange = null;
			lastRangeScan = DateTime.MinValue;
			previous = -1.0;
			changedAt = DateTime.UtcNow;
			lastAdvance = DateTime.UtcNow;
		}
		if ((DateTime.UtcNow - changedAt).TotalMilliseconds < 700.0)
		{
			mediaSnapshot.Status = "等待切歌后的界面更新";
			return mediaSnapshot;
		}
		try
		{
			if (window != null && !NeteaseTimelineRules.WindowTitle(window.Current.Name, r.Title))
			{
				window = null;
			}
		}
		catch
		{
			window = null;
		}
		if (window == null)
		{
			window = FindWindow(r);
			progress = null;
			seekRange = null;
			lastRangeScan = DateTime.MinValue;
		}
		if (window == null)
		{
			mediaSnapshot.Status = "请打开网易云主窗口，并保持播放进度可见";
			return mediaSnapshot;
		}
		if (!ReadText(progress, out var current, out var total))
		{
			progress = FindProgress(out current, out total);
			if (progress == null)
			{
				mediaSnapshot.Status = "Now Playing 尚未读到进度；" + (string.IsNullOrEmpty(NeteaseMusicHelper.LastError) ? "请显示网易云主窗口进度条" : NeteaseMusicHelper.LastError);
				return mediaSnapshot;
			}
		}
		if (current != previous)
		{
			previous = current;
			lastAdvance = DateTime.UtcNow;
		}
		else if (r.Playing && (DateTime.UtcNow - lastAdvance).TotalSeconds > 4.0)
		{
			progress = null;
			mediaSnapshot.Status = "进度文本未更新，请显示网易云窗口的进度条";
			return mediaSnapshot;
		}
		RangeValuePattern rangeValuePattern = FindSeekRange();
		mediaSnapshot.Available = true;
		mediaSnapshot.Status = "OK";
		mediaSnapshot.Duration = total;
		mediaSnapshot.Position = current;
		mediaSnapshot.MinSeek = 0.0;
		mediaSnapshot.MaxSeek = total;
		mediaSnapshot.Rate = 0.0;
		mediaSnapshot.CanSeek = rangeValuePattern != null;
		mediaSnapshot.TimelineStatus = ((rangeValuePattern != null) ? "UI Automation progress and writable range" : "UI Automation progress; read-only");
		if (r.Command == "seek")
		{
			if (rangeValuePattern == null)
			{
				mediaSnapshot.Status = "播放器未开放可写进度控件";
				return mediaSnapshot;
			}
			rangeValuePattern.SetValue(NeteaseTimelineRules.RangeTarget(r.Seconds, total, rangeValuePattern.Current.Minimum, rangeValuePattern.Current.Maximum));
			mediaSnapshot.Position = Math.Max(0.0, Math.Min(total, r.Seconds));
			previous = -1.0;
			lastAdvance = DateTime.UtcNow;
		}
		return mediaSnapshot;
	}

	public static int Run()
	{
		int code = 0;
		Thread thread = new Thread(() =>
		{
			try
			{
				NeteaseUiReader neteaseUiReader = new NeteaseUiReader();
				using StreamReader input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true, 4096);
				using Stream output = Console.OpenStandardOutput();
				MediaWireProtocol.Loop(input, output, neteaseUiReader.Read);
			}
			catch (Exception ex)
			{
				code = 1;
				try
				{
					using StreamWriter streamWriter = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
					streamWriter.WriteLine(ex.GetType().Name + "：" + ex.Message);
				}
				catch
				{
				}
			}
		});
		thread.SetApartmentState(ApartmentState.MTA);
		thread.Start();
		thread.Join();
		return code;
	}
}
