using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class NoteBook
{
	[DataMember]
	public List<NoteItem> Items = new List<NoteItem>();

	[DataMember]
	public string SelectedId;

	public NoteItem Selected => Items.First((NoteItem n) => n.Id == SelectedId);

	public void Normalize()
	{
		Items = (Items ?? new List<NoteItem>()).Where((NoteItem n) => n != null).ToList();
		foreach (NoteItem item in Items)
		{
			item.Normalize();
		}
		Items = (from n in Items
			group n by n.Id into g
			select g.First()).ToList();
		if (Items.Count == 0)
		{
			Items.Add(new NoteItem
			{
				Title = "随手记"
			});
		}
		if (!Items.Any((NoteItem n) => n.Id == SelectedId))
		{
			SelectedId = Items[0].Id;
		}
	}
}
