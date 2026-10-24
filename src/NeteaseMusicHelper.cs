using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using GlassBoard;
using Interop.UIAutomationClient;

public static class NeteaseMusicHelper
{
	private static readonly bool PRINT_EXCEPTION_LOG = false;

	private const int UIA_ProcessIdPropertyId = 30002;

	private const int UIA_ControlTypePropertyId = 30003;

	private const int UIA_TextControlTypeId = 50020;

	private const int PROGRESS_POLL_INTERVAL_MS = 300;

	private const int FULL_SCAN_RETRY_MS = 500;

	private const int TRACK_CHANGE_GRACE_MS = 2000;

	private static readonly object StateLock = new object();

	private static readonly AutoResetEvent WorkSignal = new AutoResetEvent(initialState: false);

	private static Thread _workerThread;

	private static string _requestedTrack;

	private static long _requestedGeneration;

	private static long _resultGeneration = -1L;

	private static int _currentSec = -1;

	private static int _totalSec = -1;

	private static bool _playing;

	private static DateTime _progressAt = DateTime.MinValue;

	public static string LastError = "";

	private static DateTime _restartAfter = DateTime.MinValue;

	public static void SetPlaying(bool value)
	{
		lock (StateLock)
		{
			_playing = value;
		}
	}

	public static void SetTrack(string track)
	{
		lock (StateLock)
		{
			if (string.Equals(_requestedTrack, track, StringComparison.Ordinal))
			{
				if (!string.IsNullOrEmpty(track) && _workerThread != null && !_workerThread.IsAlive && DateTime.UtcNow >= _restartAfter)
				{
					_workerThread = null;
					EnsureWorkerStarted();
				}
				return;
			}
			_requestedTrack = track;
			_requestedGeneration++;
			_resultGeneration = -1L;
			_currentSec = -1;
			_totalSec = -1;
			if (!string.IsNullOrEmpty(track))
			{
				EnsureWorkerStarted();
			}
		}
		WorkSignal.Set();
	}

	public static bool TryGetCachedProgress(out int currentSec, out int totalSec)
	{
		lock (StateLock)
		{
			currentSec = _currentSec;
			totalSec = _totalSec;
			return _resultGeneration == _requestedGeneration && currentSec >= 0 && totalSec > 0 && (!_playing || (DateTime.UtcNow - _progressAt).TotalSeconds <= 4.0);
		}
	}

	private static void EnsureWorkerStarted()
	{
		if (_workerThread == null)
		{
			_workerThread = new Thread(ProgressWorker)
			{
				IsBackground = true,
				Name = "NeteaseProgressUIA"
			};
			_workerThread.SetApartmentState(ApartmentState.STA);
			_workerThread.Start();
		}
	}

	private static void ProgressWorker()
	{
		IUIAutomation iUIAutomation = null;
		IUIAutomationElement iUIAutomationElement = null;
		IUIAutomationElement iUIAutomationElement2 = null;
		long num = -1L;
		DateTime dateTime = DateTime.MinValue;
		DateTime dateTime2 = DateTime.MinValue;
		int num2 = -1;
		bool flag = false;
		try
		{
			iUIAutomation = CreateAutomation();
			while (true)
			{
				string requestedTrack;
				long requestedGeneration;
				lock (StateLock)
				{
					requestedTrack = _requestedTrack;
					requestedGeneration = _requestedGeneration;
				}
				if (requestedGeneration != num)
				{
					ReleaseComObject(iUIAutomationElement2);
					iUIAutomationElement2 = null;
					num = requestedGeneration;
					dateTime = DateTime.UtcNow.AddMilliseconds(2000.0);
					dateTime2 = DateTime.MinValue;
					num2 = -1;
					flag = false;
				}
				if (string.IsNullOrEmpty(requestedTrack))
				{
					ReleaseComObject(iUIAutomationElement2);
					iUIAutomationElement2 = null;
					ReleaseComObject(iUIAutomationElement);
					iUIAutomationElement = null;
					num2 = -1;
					flag = false;
					WorkSignal.WaitOne();
					continue;
				}
				int num3 = (int)Math.Ceiling((dateTime - DateTime.UtcNow).TotalMilliseconds);
				if (num3 > 0)
				{
					WorkSignal.WaitOne(Math.Min(num3, 300));
					continue;
				}
				int currentSec = -1;
				int totalSec = -1;
				if (iUIAutomationElement2 != null)
				{
					try
					{
						if (!TryParseProgressText(iUIAutomationElement2.CurrentName, out currentSec, out totalSec))
						{
							ReleaseComObject(iUIAutomationElement2);
							iUIAutomationElement2 = null;
						}
					}
					catch
					{
						ReleaseComObject(iUIAutomationElement2);
						iUIAutomationElement2 = null;
					}
				}
				if (iUIAutomationElement2 == null && (DateTime.UtcNow - dateTime2).TotalMilliseconds >= 500.0)
				{
					dateTime2 = DateTime.UtcNow;
					if (!IsUsablePlayerWindow(iUIAutomationElement))
					{
						ReleaseComObject(iUIAutomationElement);
						iUIAutomationElement = FindNeteasePlayerWindow(iUIAutomation);
					}
					if (iUIAutomationElement != null)
					{
						iUIAutomationElement2 = FindProgressElement(iUIAutomation, iUIAutomationElement, out currentSec, out totalSec);
					}
				}
				if (currentSec >= 0 && totalSec > 0)
				{
					bool num4 = !flag || currentSec != num2;
					num2 = currentSec;
					flag = true;
					if (num4)
					{
						PublishProgress(requestedGeneration, currentSec, totalSec);
					}
				}
				else
				{
					ClearPublishedProgress(requestedGeneration);
				}
				WorkSignal.WaitOne(300);
			}
		}
		catch (Exception ex)
		{
			LastError = ex.Message;
			_restartAfter = DateTime.UtcNow.AddSeconds(5.0);
			if (PRINT_EXCEPTION_LOG)
			{
				Console.Error.WriteLine("【网易云】进度 UIA worker 异常：" + ex);
			}
		}
		finally
		{
			ReleaseComObject(iUIAutomationElement2);
			ReleaseComObject(iUIAutomationElement);
			ReleaseComObject(iUIAutomation);
		}
	}

	private static void PublishProgress(long generation, int currentSec, int totalSec)
	{
		lock (StateLock)
		{
			if (generation == _requestedGeneration && !string.IsNullOrEmpty(_requestedTrack))
			{
				_currentSec = currentSec;
				_totalSec = totalSec;
				_progressAt = DateTime.UtcNow;
				_resultGeneration = generation;
			}
		}
	}

	private static void ClearPublishedProgress(long generation)
	{
		lock (StateLock)
		{
			if (generation == _requestedGeneration && !string.IsNullOrEmpty(_requestedTrack))
			{
				_currentSec = -1;
				_totalSec = -1;
				_resultGeneration = -1L;
			}
		}
	}

	private static bool IsUsablePlayerWindow(IUIAutomationElement window)
	{
		if (window == null)
		{
			return false;
		}
		try
		{
			string currentName = window.CurrentName;
			string requestedTrack;
			lock (StateLock)
			{
				requestedTrack = _requestedTrack;
			}
			return NeteaseTimelineRules.WindowTitle(currentName, requestedTrack);
		}
		catch
		{
			return false;
		}
	}

	private static IUIAutomationElement FindNeteasePlayerWindow(IUIAutomation automation)
	{
		IUIAutomationElement iUIAutomationElement = null;
		IUIAutomationElement iUIAutomationElement2 = null;
		IUIAutomationCondition iUIAutomationCondition = null;
		IUIAutomationElementArray iUIAutomationElementArray = null;
		try
		{
			Process[] processesByName = Process.GetProcessesByName("cloudmusic");
			if (processesByName.Length == 0)
			{
				return null;
			}
			iUIAutomationElement2 = automation.GetRootElement();
			Process[] array = processesByName;
			foreach (Process obj in array)
			{
				int id = obj.Id;
				obj.Dispose();
				if (iUIAutomationElement != null)
				{
					continue;
				}
				try
				{
					ReleaseComObject(iUIAutomationCondition);
					iUIAutomationCondition = null;
					ReleaseComObject(iUIAutomationElementArray);
					iUIAutomationElementArray = null;
					iUIAutomationCondition = automation.CreatePropertyCondition(30002, id);
					iUIAutomationElementArray = iUIAutomationElement2.FindAll(TreeScope.TreeScope_Children, iUIAutomationCondition);
					for (int j = 0; j < iUIAutomationElementArray.Length; j++)
					{
						IUIAutomationElement iUIAutomationElement3 = null;
						try
						{
							iUIAutomationElement3 = iUIAutomationElementArray.GetElement(j);
							if (IsUsablePlayerWindow(iUIAutomationElement3))
							{
								iUIAutomationElement = iUIAutomationElement3;
								iUIAutomationElement3 = null;
								break;
							}
						}
						catch
						{
						}
						finally
						{
							ReleaseComObject(iUIAutomationElement3);
						}
					}
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			LastError = ex.Message;
			_restartAfter = DateTime.UtcNow.AddSeconds(5.0);
			if (PRINT_EXCEPTION_LOG)
			{
				Console.Error.WriteLine("【网易云】查找播放窗口异常：" + ex);
			}
		}
		finally
		{
			ReleaseComObject(iUIAutomationElementArray);
			ReleaseComObject(iUIAutomationCondition);
			ReleaseComObject(iUIAutomationElement2);
		}
		return iUIAutomationElement;
	}

	private static IUIAutomationElement FindProgressElement(IUIAutomation automation, IUIAutomationElement root, out int currentSec, out int totalSec)
	{
		currentSec = -1;
		totalSec = -1;
		IUIAutomationElement result = null;
		IUIAutomationCondition iUIAutomationCondition = null;
		IUIAutomationElementArray iUIAutomationElementArray = null;
		try
		{
			iUIAutomationCondition = automation.CreatePropertyCondition(30003, 50020);
			iUIAutomationElementArray = root.FindAll(TreeScope.TreeScope_Descendants, iUIAutomationCondition);
			for (int i = 0; i < iUIAutomationElementArray.Length; i++)
			{
				IUIAutomationElement iUIAutomationElement = null;
				try
				{
					iUIAutomationElement = iUIAutomationElementArray.GetElement(i);
					if (TryParseProgressText(iUIAutomationElement.CurrentName, out currentSec, out totalSec))
					{
						result = iUIAutomationElement;
						iUIAutomationElement = null;
						break;
					}
				}
				catch
				{
				}
				finally
				{
					ReleaseComObject(iUIAutomationElement);
				}
			}
		}
		catch (Exception ex)
		{
			LastError = ex.Message;
			_restartAfter = DateTime.UtcNow.AddSeconds(5.0);
			if (PRINT_EXCEPTION_LOG)
			{
				Console.Error.WriteLine("【网易云】解析进度 UIA 异常：" + ex);
			}
		}
		finally
		{
			ReleaseComObject(iUIAutomationElementArray);
			ReleaseComObject(iUIAutomationCondition);
		}
		return result;
	}

	private static bool TryParseProgressText(string text, out int currentSec, out int totalSec)
	{
		currentSec = -1;
		totalSec = -1;
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}
		string text2 = text.Replace(" ", "");
		int num = text2.IndexOf('/');
		if (num < 0)
		{
			num = text2.IndexOf('|');
		}
		if (num <= 0 || num >= text2.Length - 1)
		{
			return false;
		}
		string timeStr = text2.Substring(0, num);
		string timeStr2 = text2.Substring(num + 1);
		if (!TryParseTimeString(timeStr, out var seconds) || !TryParseTimeString(timeStr2, out var seconds2))
		{
			return false;
		}
		if (seconds2 <= 0 || seconds < 0 || seconds > seconds2 + 2)
		{
			return false;
		}
		currentSec = seconds;
		totalSec = seconds2;
		return true;
	}

	private static bool TryParseTimeString(string timeStr, out int seconds)
	{
		seconds = 0;
		string[] array = timeStr.Split(':');
		if (array.Length != 2)
		{
			return false;
		}
		if (!int.TryParse(array[0], out var result) || !int.TryParse(array[1], out var result2))
		{
			return false;
		}
		if (result < 0 || result2 < 0 || result2 >= 60)
		{
			return false;
		}
		seconds = result * 60 + result2;
		return true;
	}

	private static IUIAutomation CreateAutomation()
	{
		try
		{
			return new CUIAutomation8Class();
		}
		catch
		{
		}
		try
		{
			return new CUIAutomationClass();
		}
		catch
		{
		}
		Guid[] array = new Guid[2]
		{
			new Guid("E22AD333-B25F-460C-83D0-0581107395C9"),
			new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")
		};
		foreach (Guid clsid in array)
		{
			try
			{
				Type typeFromCLSID = Type.GetTypeFromCLSID(clsid);
				if (typeFromCLSID != null && Activator.CreateInstance(typeFromCLSID) is IUIAutomation result)
				{
					return result;
				}
			}
			catch
			{
			}
		}
		throw new InvalidOperationException("无法创建 UI Automation COM 对象（CUIAutomation8/CUIAutomation）。");
	}

	private static void ReleaseComObject(object comObject)
	{
		if (comObject == null || !Marshal.IsComObject(comObject))
		{
			return;
		}
		try
		{
			Marshal.FinalReleaseComObject(comObject);
		}
		catch
		{
		}
	}
}
