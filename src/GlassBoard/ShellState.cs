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

	// 隐藏桌面图标时记录的原注册表 HideIcons 值（崩溃恢复时还原）
	[DataMember]
	public int? HideIcons;
}
