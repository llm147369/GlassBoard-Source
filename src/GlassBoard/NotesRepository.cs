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
		Flush();
	}
}
