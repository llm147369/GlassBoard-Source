using System;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace GlassBoard;

[DataContract]
public class MaterialOptions
{
	[DataMember]
	public int Kind;

	[DataMember]
	public string Tint = "";

	[DataMember]
	public double Gloss = 0.15;

	[DataMember]
	public double Grain;

	[DataMember]
	public bool Shadow = true;

	// —— Apple 玻璃细节（仅 Apple 玻璃预设使用；其他材质为默认值 = 渲染零影响）——
	// 饱和度增强：1.0 无变化，1.6 = 苹果玻璃的浓郁感（烘焙底图时逐像素处理一次）
	[DataMember]
	public double Saturation = 1.0;

	// 色调叠加强度：TintBlend 模式下按此 alpha 在底色上叠主题自适应色调（浅色叠白 / 深色叠深蓝灰）
	[DataMember]
	public double TintAlpha = 0.10;

	// 色调叠加模式开关：true = 半透明叠加（Apple）；false = 旧行为（Tint 整体替换底色）
	[DataMember]
	public bool TintBlend;

	// 镜面高光：表面顶部 1px 高光 + 底部 1px 弱反光（玻璃的"镜面感"）
	[DataMember]
	public bool Specular;

	// 发丝边框：表面内侧白色 1px 细边框
	[DataMember]
	public bool Hairline;

	// 材质专属模糊半径：>0 时覆盖 材质种类×窗口模糊 的默认计算（0 = 跟随默认）
	[DataMember]
	public double BlurOverride;

	// 实时背景模糊（Apple 玻璃）：开启后窗口以 ~10fps 持续重捕背景并高质模糊
	// （小米玻璃柔光式动态效果，持续占用 CPU/GPU）。切换离开该材质立即停止并释放。
	[DataMember]
	public bool LiveBlur;

	[OnDeserializing]
	private void Defaults(StreamingContext context)
	{
		Gloss = 0.15;
		Shadow = true;
		Tint = "";
		Saturation = 1.0;
		TintAlpha = 0.10;
	}

	public void Normalize()
	{
		Kind = Math.Max(0, Math.Min(3, Kind));
		Gloss = LayoutRules.Clamp(Gloss, 0.0, 1.0, 0.15);
		Grain = LayoutRules.Clamp(Grain, 0.0, 1.0, 0.0);
		Saturation = LayoutRules.Clamp(Saturation, 1.0, 2.0, 1.0);
		TintAlpha = LayoutRules.Clamp(TintAlpha, 0.0, 0.5, 0.10);
		BlurOverride = LayoutRules.Clamp(BlurOverride, 0.0, 96.0, 0.0);
		if (!Regex.IsMatch(Tint ?? "", "^#[a-fA-F0-9]{6}$"))
		{
			Tint = "";
		}
	}

	public MaterialOptions Copy()
	{
		return (MaterialOptions)MemberwiseClone();
	}
}
