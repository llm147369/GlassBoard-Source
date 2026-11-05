using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace GlassBoard;

// 灵动岛剪贴板历史。
//
// - 事件驱动：AddClipboardFormatListener + WM_CLIPBOARDUPDATE（0x031D），绝不轮询。
// - 支持文本与图片的本地历史；图片以 PNG 存于 Store.Folder\island\clips（应用管理目录）。
// - 内容只保存在本机：内存列表 + 本地图片文件，绝不上传。
// - 锁屏（SessionSwitch.SessionLock）期间暂停捕获并进入隐私模式（UI 隐藏正文预览）。
// - 暂停、清空、条数上限、保留天数由 IslandOptions 控制；不写内容到任何日志。
// - 无法可靠识别所有密码/敏感数据：只按“与上一条相同则去重”的规则去重，不做敏感内容判断。
public static class IslandClipboard
{
	[System.Runtime.Serialization.DataContract]
	public sealed class ClipItem
	{
		[System.Runtime.Serialization.DataMember]
		public string Kind; // "text" / "image"
		[System.Runtime.Serialization.DataMember]
		public string Text; // 截断到 4096 字符
		[System.Runtime.Serialization.DataMember]
		public string ImagePath; // 本地 PNG 路径
		[System.Runtime.Serialization.DataMember]
		public string Hash; // 图片内容 SHA256（hex 小写）；文本为 null。用于跨会话去重。
		[System.Runtime.Serialization.DataMember]
		public DateTime Stamp;
	}

	private const int WM_CLIPBOARDUPDATE = 0x031D;

	private static HwndSource source;
	private static bool running;
	private static bool paused;
	private static volatile bool privacy;
	private static volatile string lastText;
	private static volatile byte[] lastImageHash;
	private static readonly object Gate = new object();
	private static readonly List<ClipItem> ItemsList = new List<ClipItem>();

	public static event Action Changed;
	public static event Action PrivacyChanged;

	public static bool PrivacyActive => privacy;
	public static bool IsPaused => paused;

	public static string Folder => Path.Combine(Store.Folder, "island", "clips");

	// 当前全部条目（新→旧）。返回副本，调用方勿修改。
	public static List<ClipItem> Items()
	{
		lock (Gate)
		{
			return ItemsList.ToList();
		}
	}

	public static void Start()
	{
		lock (Gate)
		{
			if (running)
			{
				return;
			}
			running = true;
			Directory.CreateDirectory(Folder);
			LoadNow(); // 恢复上次会话的历史（文本 + 图片索引），开关机/更新后不丢
			Prune(); // 应用保留天数/条数，并清理磁盘孤儿文件
			HwndSourceParameters p = new HwndSourceParameters("GlassBoard.IslandClipboard")
			{
				ParentWindow = new IntPtr(-3), // HWND_MESSAGE
				WindowStyle = 0
			};
			source = new HwndSource(p);
			source.AddHook(WndProc);
			AddClipboardFormatListener(source.Handle);
			SystemEvents.SessionSwitch += OnSessionSwitch;
		}
	}

	public static void Stop()
	{
		lock (Gate)
		{
			if (!running)
			{
				return;
			}
			running = false;
			SystemEvents.SessionSwitch -= OnSessionSwitch;
			if (source != null)
			{
				RemoveClipboardFormatListener(source.Handle);
				source.RemoveHook(WndProc);
				source.Dispose();
				source = null;
			}
		}
	}

	public static void SetPaused(bool value)
	{
		paused = value;
	}

	public static void SetEnabled(bool value, int maxItems, int retainDays)
	{
		SetPaused(!value || paused);
		lock (Gate)
		{
			if (!value)
			{
				return;
			}
			PruneNow(maxItems, retainDays);
		}
	}

	private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
	{
		if (e.Reason == SessionSwitchReason.SessionLock)
		{
			privacy = true; // 锁屏：暂停捕获 + 隐藏预览
			RaisePrivacy();
		}
		else if (e.Reason == SessionSwitchReason.SessionUnlock)
		{
			privacy = false;
			RaisePrivacy();
		}
	}

	private static void RaisePrivacy()
	{
		try
		{
			System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(delegate
			{
				PrivacyChanged?.Invoke();
			}));
		}
		catch
		{
		}
	}

	private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == WM_CLIPBOARDUPDATE)
		{
			handled = true;
			// 从历史页点击“复制”回写剪贴板时跳过本次更新：回写不应再记录一条历史。
			if (TryConsumeSuppression())
			{
				return IntPtr.Zero;
			}
			Capture();
		}
		return IntPtr.Zero;
	}

	private static volatile string suppressText;
	private static volatile string suppressImage;

	// 自回写抑制：WriteBack 设置指纹，下一次剪贴板更新若内容匹配则消费掉，不再记录。
	private static bool TryConsumeSuppression()
	{
		try
		{
			if (suppressText != null)
			{
				if (Clipboard.ContainsText())
				{
					string now = Clipboard.GetText();
					if (now == suppressText)
					{
						suppressText = null;
						return true;
					}
				}
				suppressText = null;
			}
			if (suppressImage != null)
			{
				if (Clipboard.ContainsImage())
				{
					suppressImage = null;
					return true; // 图片逐字节比对代价高：自回写后只跳过一次更新
				}
				suppressImage = null;
			}
		}
		catch
		{
		}
		return false;
	}

	private static void Capture()
	{
		if (paused || privacy)
		{
			return;
		}
		IslandOptions o = IslandSettings.Current;
		if (o == null || !o.ClipboardEnabled)
		{
			return;
		}
		try
		{
			// 文本优先
			if (Clipboard.ContainsText())
			{
				string text = Clipboard.GetText();
				if (string.IsNullOrEmpty(text) || text.Length < 1)
				{
					return;
				}
				if (text.Length > 4096)
				{
					text = text.Substring(0, 4096);
				}
				if (lastText == text)
				{
					return; // 连续重复不记录
				}
				lastText = text;
				lastImageHash = null;
				Add(new ClipItem { Kind = "text", Text = text, Stamp = DateTime.Now });
				Prune();
				RaiseChanged();
				return;
			}
			if (Clipboard.ContainsImage())
			{
				System.Windows.Media.Imaging.BitmapSource img = Clipboard.GetImage();
				if (img == null || img.PixelWidth * img.PixelHeight > 40000000)
				{
					return;
				}
				using MemoryStream ms = new MemoryStream();
				PngBitmapEncoder encoder = new PngBitmapEncoder();
				encoder.Frames.Add(BitmapFrame.Create(img));
				encoder.Save(ms);
				byte[] png = ms.ToArray();
				byte[] hash = SHA256.Create().ComputeHash(png);
				if (lastImageHash != null && hash.SequenceEqual(lastImageHash))
				{
					return;
				}
				lastImageHash = hash;
				lastText = null;
				string hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
				string file;
				lock (Gate)
				{
					ClipItem existing = ItemsList.FirstOrDefault((ClipItem a) => a.Kind == "image" && a.Hash == hex);
					file = existing?.ImagePath;
				}
				if (string.IsNullOrEmpty(file))
				{
					file = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".png");
					File.WriteAllBytes(file, png);
				}
				Add(new ClipItem { Kind = "image", ImagePath = file, Hash = hex, Stamp = DateTime.Now });
				Prune();
				RaiseChanged();
			}
		}
		catch
		{
			// 剪贴板被其它进程占用等瞬态错误：忽略，等待下一次事件。
		}
	}

	private static void Add(ClipItem item)
	{
		lock (Gate)
		{
			ClipItem dup = null;
			if (item.Kind == "text")
			{
				dup = ItemsList.FirstOrDefault((ClipItem a) => a.Kind == "text" && a.Text == item.Text);
			}
			else if (item.Kind == "image" && !string.IsNullOrEmpty(item.Hash))
			{
				dup = ItemsList.FirstOrDefault((ClipItem a) => a.Kind == "image" && a.Hash == item.Hash);
			}
			if (dup != null)
			{
				// 内容与历史中某条相同：不新增条目，把旧条目提到最前并更新记录时间。
				ItemsList.Remove(dup);
				dup.Stamp = item.Stamp;
				ItemsList.Insert(0, dup);
				if (dup != item && !string.IsNullOrEmpty(item.ImagePath) && item.ImagePath != dup.ImagePath)
				{
					TryDelete(item.ImagePath); // 重复图片新写的文件没有保留价值
				}
			}
			else
			{
				ItemsList.Insert(0, item);
			}
		}
		SaveNow();
	}

	public static void Clear()
	{
		lock (Gate)
		{
			foreach (ClipItem item in ItemsList)
			{
				TryDelete(item.ImagePath);
			}
			ItemsList.Clear();
		}
		lastText = null;
		lastImageHash = null;
		SaveNow();
		RaiseChanged();
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}

	public static void Prune()
	{
		IslandOptions o = IslandSettings.Current;
		PruneNow(o?.ClipboardMaxItems ?? 100, o?.ClipboardRetainDays ?? 14);
	}

	private static void PruneNow(int maxItems, int retainDays)
	{
		bool changed = false;
		lock (Gate)
		{
			DateTime cutoff = DateTime.Now.AddDays(-Math.Max(1, retainDays));
			List<ClipItem> removed = ItemsList.Where((ClipItem a) => a.Stamp < cutoff).ToList();
			List<ClipItem> over = ItemsList.Skip(Math.Max(10, maxItems)).ToList();
			foreach (ClipItem item in removed.Concat(over))
			{
				ItemsList.Remove(item);
				TryDelete(item.ImagePath);
				changed = true;
			}
		}
		// 清理磁盘上不在列表中的孤儿文件
		try
		{
			HashSet<string> live = new HashSet<string>(ItemsList.Where((ClipItem a) => a.ImagePath != null).Select((ClipItem a) => Path.GetFileName(a.ImagePath)), StringComparer.OrdinalIgnoreCase);
			foreach (string file in Directory.GetFiles(Folder, "*.png"))
			{
				if (!live.Contains(Path.GetFileName(file)))
				{
					TryDelete(file);
					changed = true;
				}
			}
		}
		catch
		{
		}
		if (changed)
		{
			SaveNow();
		}
	}

	// —— 持久化：历史索引存 %LOCALAPPDATA%\GlassBoard\island\clips\history.json ——
	// 文本条目全部进 JSON；图片条目存 PNG 文件 + JSON 索引，开关机/应用更新后自动恢复。
	private static string IndexFile => Path.Combine(Folder, "history.json");

	private static void SaveNow()
	{
		try
		{
			List<ClipItem> snapshot;
			lock (Gate)
			{
				snapshot = ItemsList.ToList();
			}
			Directory.CreateDirectory(Folder);
			string tmp = IndexFile + ".tmp";
			using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<ClipItem>)).WriteObject(fs, snapshot);
			}
			File.Copy(tmp, IndexFile, true);
			File.Delete(tmp);
		}
		catch
		{
			// 磁盘写入失败不打断剪贴板功能。
		}
	}

	private static void LoadNow()
	{
		try
		{
			if (!File.Exists(IndexFile))
			{
				return;
			}
			using FileStream fs = File.OpenRead(IndexFile);
			List<ClipItem> saved = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<ClipItem>)).ReadObject(fs) as List<ClipItem>;
			if (saved == null)
			{
				return;
			}
			lock (Gate)
			{
				foreach (ClipItem item in saved)
				{
					if (item == null)
					{
						continue;
					}
					if (item.Kind == "text")
					{
						if (string.IsNullOrEmpty(item.Text))
						{
							continue;
						}
						ItemsList.Add(item);
					}
					else if (item.Kind == "image")
					{
						if (!File.Exists(item.ImagePath))
						{
							continue; // 图片文件丢失（被外部清理）：丢弃索引
						}
						ItemsList.Add(item);
					}
				}
			}
		}
		catch
		{
			// 索引损坏/版本不兼容：当作空历史开始，不崩溃。
		}
	}

	private static void RaiseChanged()
	{
		try
		{
			System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(delegate
			{
				Changed?.Invoke();
			}));
		}
		catch
		{
		}
	}

	// 点击历史条目回写剪贴板。
	public static bool WriteBack(ClipItem item)
	{
		try
		{
			if (item == null)
			{
				return false;
			}
			if (item.Kind == "text")
			{
				suppressText = item.Text ?? "";
				suppressImage = null;
				Clipboard.SetText(item.Text ?? "");
				return true;
			}
			if (item.Kind == "image" && File.Exists(item.ImagePath))
			{
				BitmapImage bmp = new BitmapImage();
				bmp.BeginInit();
				bmp.CacheOption = BitmapCacheOption.OnLoad;
				bmp.UriSource = new Uri(item.ImagePath);
				bmp.EndInit();
				suppressText = null;
				suppressImage = item.ImagePath;
				Clipboard.SetImage(bmp);
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool AddClipboardFormatListener(IntPtr hwnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

// 灵动岛当前设置的轻量访问器：由 IslandHub 在设置变化时更新。
public static class IslandSettings
{
	private static volatile IslandOptions current = new IslandOptions();

	public static IslandOptions Current => current;

	public static void Set(IslandOptions value)
	{
		current = value ?? new IslandOptions();
	}
}
