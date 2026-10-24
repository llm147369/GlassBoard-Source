using System.ComponentModel;

namespace GlassBoard;

public class AddCandidate : INotifyPropertyChanged
{
	private bool selected = true;

	public string Path { get; set; }

	public string Name => EntryPathRules.Name(Path);

	public bool Selected
	{
		get
		{
			return selected;
		}
		set
		{
			if (selected != value)
			{
				selected = value;
				if (PropertyChanged != null)
				{
					PropertyChanged(this, new PropertyChangedEventArgs("Selected"));
				}
			}
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;
}
