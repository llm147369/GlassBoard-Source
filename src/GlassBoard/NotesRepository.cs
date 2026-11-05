using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;

namespace GlassBoard;

public sealed class NotesRepository : IDisposable
{
	private readonly string path;

	private readonly DispatcherTimer timer = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(650.0)
	};

	private readonly DispatcherTimer remind = new DispatcherTimer
	{
		Interval = TimeSpan.FromSeconds(30)
	};

	private bool dirty;

	public NoteBook Book;

	public string Status = "已保存";

	public NotesRepository()
	{
		path = Path.Combine(Store.Folder, "notes.json");
		Book = Load();
		Book.Normalize();
		timer.Tick += (object s, EventArgs e) =>
		{
			timer.Stop();
			Flush();
		};
		// 提醒调度：每 30 秒检查一次到期的随手记，通过灵动岛通知通道弹卡。
		remind.Tick += (object s, EventArgs e) =>
		{
			try
			{
				long now = DateTime.UtcNow.Ticks;
				foreach (NoteItem n in Book.Items)
				{
					if (n.RemindAt > 0 && n.RemindAt <= now)
					{
						n.RemindAt = 0;
						Changed();
						Flush();
						IslandNotifications.Push(new IslandNotification
						{
							AppName = "随手记",
							Title = "提醒 · " + n.DisplayTitle,
							Body = string.IsNullOrWhiteSpace(n.Body) ? "（无内容）" : (n.Body.Length > 200 ? n.Body.Substring(0, 200) : n.Body),
							Stamp = DateTime.Now,
							AlwaysFresh = true
						});
					}
				}
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
		};
		remind.Start();
	}

	private NoteBook Load()
	{
		return NoteFiles.Read(path, Store.Log);
	}

	public void Changed()
	{
		dirty = true;
		Book.Selected.Updated = DateTime.UtcNow.Ticks;
		Status = "正在保存…";
		timer.Stop();
		timer.Start();
	}

	public bool Flush()
	{
		timer.Stop();
		if (!dirty && File.Exists(path))
		{
			return true;
		}
		try
		{
			NoteFiles.Write(path, Book);
			dirty = false;
			Status = "已保存 · " + DateTime.Now.ToString("HH:mm");
			return true;
		}
		catch (Exception ex)
		{
			Store.Log(ex);
			Status = "保存失败，请先导出备忘录";
			dirty = true;
			return false;
		}
	}

	public void Add()
	{
		Flush();
		NoteItem noteItem = new NoteItem();
		Book.Items.Add(noteItem);
		Book.SelectedId = noteItem.Id;
		Changed();
		Flush();
	}

	public void Remove()
	{
		Book.Items.Remove(Book.Selected);
		Book.Normalize();
		Changed();
		Flush();
	}

	public void Select(string id)
	{
		Flush();
		if (Book.Items.Any((NoteItem n) => n.Id == id))
		{
			Book.SelectedId = id;
			Changed();
			Flush();
		}
	}

	public void Export(string destination)
	{
		NoteItem selected = Book.Selected;
		File.WriteAllText(destination, selected.Title + Environment.NewLine + Environment.NewLine + selected.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
	}

	public void Reload()
	{
		timer.Stop();
		dirty = false;
		Book = Load();
		Book.Normalize();
		Status = "已恢复";
	}

	public void Dispose()
	{
		timer.Stop();
		remind.Stop();
		Flush();
	}
}
