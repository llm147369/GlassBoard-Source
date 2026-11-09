using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace GlassBoard;

// 桌面壁纸偏好：开关、当前壁纸与样式、系统原始壁纸记录（用于一键还原）、图库缓存时间。
// 旧配置没有此字段时反序列化为 null，Preferences.Normalize() 里补齐默认值。
[DataContract]
public class WallpaperOptions
{
	// 启用壁纸：总开关。开启后应用 Path 并开机恢复；关闭后还原系统壁纸（保留设置可随时再开）
	[DataMember]
	public bool Enabled;

	// 开机自动恢复（仅在启用壁纸时生效）。旧配置缺失时为 null，Normalize 里默认 true
	[DataMember(EmitDefaultValue = false)]
	public bool? AutoRestore = null;

	// 当前壁纸文件（位于应用壁纸库 Wallpapers\ 内）；空 = 未用本功能设置过壁纸
	[DataMember]
	public string Path;

	// 样式：0 填充 / 1 适应 / 2 拉伸 / 3 居中 / 4 平铺
	[DataMember]
	public int Style;

	// 首次设置壁纸前记录的系统原始壁纸（显示器ID → 壁纸文件），用于"恢复系统壁纸"
	[DataMember(EmitDefaultValue = false)]
	public Dictionary<string, string> Previous = new Dictionary<string, string>();

	// 来源：0 本地导入 / 1 官方图库（仅供界面标记）
	[DataMember]
	public int Source;

	// 上次成功拉取官方图库清单的时间（Utc ticks）
	[DataMember]
	public long CatalogFetchedUtc;

	// 动态壁纸：启用后播放 VideoPath 指定的视频（循环、可调音量），覆盖静态壁纸之上
	[DataMember]
	public bool VideoEnabled;

	// 动态壁纸视频文件（直接引用用户原文件，不复制入库）；空 = 未选择
	[DataMember]
	public string VideoPath;

	// 动态壁纸音量 0-100
	[DataMember]
	public double VideoVolume = 100.0;

	// 动态壁纸静音
	[DataMember]
	public bool VideoMuted;

	public WallpaperOptions Copy()
	{
		return new WallpaperOptions
		{
			Enabled = Enabled,
			AutoRestore = AutoRestore,
			Path = Path,
			Style = Style,
			Previous = Previous == null ? new Dictionary<string, string>() : new Dictionary<string, string>(Previous),
			Source = Source,
			CatalogFetchedUtc = CatalogFetchedUtc,
			VideoEnabled = VideoEnabled,
			VideoPath = VideoPath,
			VideoVolume = VideoVolume,
			VideoMuted = VideoMuted
		};
	}

	public void Normalize()
	{
		if (Previous == null)
		{
			Previous = new Dictionary<string, string>();
		}
		if (AutoRestore == null)
		{
			AutoRestore = true;
		}
		Style = Math.Max(0, Math.Min(4, Style));
		VideoVolume = Math.Max(0.0, Math.Min(100.0, VideoVolume));
		// 壁纸文件已不存在（被移动/删除）：清掉引用，开机恢复时保持现状不打扰
		if (Path != null && !System.IO.File.Exists(Path))
		{
			Path = null;
		}
		if (VideoPath != null && !System.IO.File.Exists(VideoPath))
		{
			VideoPath = null;
		}
	}
}
