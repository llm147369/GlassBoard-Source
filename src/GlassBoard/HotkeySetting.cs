using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class HotkeySetting
{
	[DataMember]
	public int ActionId;

	[DataMember]
	public uint Modifiers;

	[DataMember]
	public uint Key;

	[DataMember]
	public bool Enabled;

	public HotkeySetting Copy()
	{
		return new HotkeySetting
		{
			ActionId = ActionId,
			Modifiers = Modifiers,
			Key = Key,
			Enabled = Enabled
		};
	}
}
