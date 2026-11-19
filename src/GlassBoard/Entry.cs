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


	// 大面板软件分组（主分组；空 = 未分组）
	[DataMember(EmitDefaultValue = false)]
	public string Group;

	// 附加分组：历史版本支持"同一软件出现在多个分组"；现改为唯一归属，保留字段只为兼容旧配置文件
	[DataMember(EmitDefaultValue = false)]
	public List<string> ExtraGroups;

	[OnDeserialized]
	private void OnDeserialized(StreamingContext ctx)
	{
		// 分组改为唯一归属：历史版本的多分组共享数据统一收敛——图标只保留主分组，
		// 从附加分组中消失（避免"同一个图标出现在两个分组"）
		if (ExtraGroups != null && ExtraGroups.Count > 0)
		{
			ExtraGroups = new List<string>();
		}
	}
}
