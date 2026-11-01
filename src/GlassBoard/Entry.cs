using System.Collections.Generic;
using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class Entry
{
	[DataMember]
	public string Name;

	[DataMember]
	public string Path;

	[DataMember]
	public bool Pinned;

	[DataMember(EmitDefaultValue = false)]
	public bool IsFolder;

	[DataMember(EmitDefaultValue = false)]
	public string IconPath;

	[DataMember(EmitDefaultValue = false)]
	public string LaunchTarget;

	[DataMember(EmitDefaultValue = false)]
	public string LaunchArguments;

	[DataMember(EmitDefaultValue = false)]
	public string ProductInfo;

	[DataMember(EmitDefaultValue = false)]
	public string WindowTarget;

	// 大面板文件夹归属（空 = 根目录）；由磁贴拖入文件夹/解散文件夹维护
	[DataMember(EmitDefaultValue = false)]
	public string Folder;

	// 大面板软件分组（主分组；空 = 未分组）
	[DataMember(EmitDefaultValue = false)]
	public string Group;

	// 附加分组：同一软件可同时出现在多个分组
	[DataMember(EmitDefaultValue = false)]
	public List<string> ExtraGroups;
}
