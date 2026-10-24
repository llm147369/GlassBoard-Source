using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class ShellState
{
	[DataMember]
	public long Handle;

	[DataMember]
	public string ClassName;

	[DataMember]
	public bool Visible;
}
