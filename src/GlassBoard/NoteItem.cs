using System;
using System.ComponentModel;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class NoteItem : INotifyPropertyChanged
{
	[DataMember]
	public string Id = Guid.NewGuid().ToString("N");

	[DataMember]
	public string Body = "";

	private string title = "新备忘录";

	[DataMember]
	public long Updated = DateTime.UtcNow.Ticks;

	[DataMember]
	public string Title
	{
		get
		{
			return title;
		}
		set
		{
			if (!(title == value))
			{
				title = value;
				PropertyChangedEventHandler propertyChangedEventHandler = PropertyChanged;
				if (propertyChangedEventHandler != null)
				{
					propertyChangedEventHandler(this, new PropertyChangedEventArgs("Title"));
					propertyChangedEventHandler(this, new PropertyChangedEventArgs("DisplayTitle"));
				}
			}
		}
	}

	public string DisplayTitle
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(Title))
			{
				return Title;
			}
			return "未命名备忘录";
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	public void Normalize()
	{
		if (string.IsNullOrWhiteSpace(Id))
		{
			Id = Guid.NewGuid().ToString("N");
		}
		Title = (string.IsNullOrWhiteSpace(Title) ? "未命名备忘录" : Title.Trim());
		if (Title.Length > 120)
		{
			Title = Title.Substring(0, 120);
		}
		Body = Body ?? "";
	}

	public override string ToString()
	{
		return Title;
	}
}
