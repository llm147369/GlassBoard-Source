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

	// 隐藏任务栏时记录的 AppBar 原始状态（崩溃恢复时还原工作区占用）
	[DataMember]
	public uint? AppBarState;
}
