using System.Collections.Generic;

namespace GlassBoard;

public sealed class BoardEditHistory
{
	private sealed class Item
	{
		public BoardEditState State;

		public string Label;
	}

	private readonly List<Item> undo = new List<Item>();

	private readonly List<Item> redo = new List<Item>();

	private const int Limit = 40;

	public bool CanUndo => undo.Count > 0;

	public bool CanRedo => redo.Count > 0;

	public string UndoLabel
	{
		get
		{
			if (!CanUndo)
			{
				return "";
			}
			return undo[undo.Count - 1].Label;
		}
	}

	public string RedoLabel
	{
		get
		{
			if (!CanRedo)
			{
				return "";
			}
			return redo[redo.Count - 1].Label;
		}
	}

	public void Clear()
	{
		undo.Clear();
		redo.Clear();
	}

	public bool Remember(BoardEditState before, Preferences after, string label)
	{
		if (before.Signature() == BoardEditState.Capture(after).Signature())
		{
			return false;
		}
		undo.Add(new Item
		{
			State = before,
			Label = label
		});
		if (undo.Count > 40)
		{
			undo.RemoveAt(0);
		}
		redo.Clear();
		return true;
	}

	public string Undo(Preferences p)
	{
		if (!CanUndo)
		{
			return null;
		}
		Item item = undo[undo.Count - 1];
		undo.RemoveAt(undo.Count - 1);
		redo.Add(new Item
		{
			State = BoardEditState.Capture(p),
			Label = item.Label
		});
		item.State.Apply(p);
		return item.Label;
	}

	public string Redo(Preferences p)
	{
		if (!CanRedo)
		{
			return null;
		}
		Item item = redo[redo.Count - 1];
		redo.RemoveAt(redo.Count - 1);
		undo.Add(new Item
		{
			State = BoardEditState.Capture(p),
			Label = item.Label
		});
		item.State.Apply(p);
		return item.Label;
	}
}
