using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace GlassBoard;

// 灵动岛的全部外观与行为设置。仅作用于灵动岛，不影响 Dock / 大面板 / 组件。
// 序列化要求与 Preferences 其它成员一致：旧配置缺少本字段时使用下方默认值，
// Normalize() 负责把旧版本/异常值收敛到安全范围。
[DataContract]
public sealed class IslandOptions
{
	// DataContractJsonSerializer 反序列化不会运行字段初始化器：旧配置缺少某字段时该字段为 default(T)。
	// 与 Preferences 的模式一致，用 OnDeserializing 恢复所有非零默认值，再交由 Normalize 收敛。
	[OnDeserializing]
	private void OnDeserializing(StreamingContext ctx)
	{
		// 出厂默认 = 官方推荐配置（宽高比例、间距、字体、透明度、频谱曲线等），
		// 显式写进配置的字段仍会覆盖这里（反序列化在初始化之后进行）。
		// 新用户默认关闭灵动岛：Enabled 只在旧配置显式为 true 时开启。
		Enabled = false;
		SizeScale = 1.05;
		WidthScale = 1.1125;
		HeightScale = 1.1875;
		CollapsedWidth = 356.0;
		CollapsedHeight = 76.0;
		ExpandedWidth = 422.75;
		TopGap = 0.0;
		OpacityPercent = 22.0;
		BlurRadius = 0.0;
		BorderOpacity = 0.35;
		ShadowStrength = 0.3;
		ImageDarken = 0.35;
		FontSize = 14.95;
		IconSize = 23.0;
		ItemGap = 11.5;
		CornerRadius = 20.0;
		ExpandSpeed = 1.0;
		NotifyDurationSec = 5.0;
		LowBatteryThreshold = 20;
		EqualizerStyle = 4;
		MusicAlignCenter = true;
		ClipboardMaxItems = 100;
		ClipboardRetainDays = 14;
		ClipboardEnabled = true;
		ShowMusic = true;
		ShowCover = true;
		ShowBattery = true;
		ShowNotifyHint = true;
		ShowPcBattery = true;
		ShowMouseBattery = true;
		ShowKeyboardBattery = true;
		ShowMusicPage = true;
		ShowNotificationsPage = true;
		ShowControlPage = true;
		AnimationEnabled = true;
		Light = true;
		FollowSystemTheme = true;
		Shape = 1;
		Material = new MaterialOptions();
		MutedApps = new List<string>();
	}

	// —— 总开关与预设 ——
	[DataMember]
	public bool Enabled = false;

	// 保持在窗口最上方（置顶）。默认不保持：跟随正常 Z 序，
	// 被其他窗口遮挡时降到它们下面；Win+D 显示桌面由 DesktopCoverGuard 兜底。
	[DataMember]
	public bool AlwaysOnTop;

	// 预设皮肤：0=GlassBoard 毛玻璃 1=清透 2=纯色 3=深色 4=自定义
	[DataMember]
	public int Preset = 0;

	// 形状：0=胶囊 1=圆角矩形
	[DataMember]
	public int Shape = 1;

	[DataMember]
	public double CornerRadius = 20.0;

	// —— 尺寸（逻辑像素） ——
	[DataMember]
	public double CollapsedWidth = 356.0;

	[DataMember]
	public double CollapsedHeight = 76.0;

	[DataMember]
	public double ExpandedWidth = 422.75;

	// —— 位置 ——
	// 距所选屏幕工作区顶部的间距（物理像素语义，按当前 DPI 换算）
	[DataMember]
	public double TopGap = 0.0;

	// 0 = 主显示器；越界时回退主显示器
	[DataMember]
	public int MonitorIndex = 0;

	// —— 背景与材质 ——
	[DataMember(EmitDefaultValue = false)]
	public MaterialOptions Material = new MaterialOptions();

	// 主题色下标（IconPacks.AccentColor）
	[DataMember]
	public int Accent = 0;

	// 纯色/深色预设使用的背景色（#RRGGBB；空 = 按主题自动）
	[DataMember]
	public string BackgroundColor = "";

	// 不透明度 10..100（100 = 完全不透明）
	[DataMember]
	public double OpacityPercent = 22.0;

	// 模糊程度 0..40（玻璃材质的底图模糊；纯色预设下仍影响边缘氛围）
	[DataMember]
	public double BlurRadius = 0.0;

	[DataMember]
	public string BorderColor = "";

	[DataMember]
	public double BorderOpacity = 0.35;

	// 阴影强度 0..0.8（0 关闭投影）
	[DataMember]
	public double ShadowStrength = 0.3;

	// —— 自定义背景图 ——
	// 图片必须位于应用管理的目录（Store.Folder\island）。设置页选择图片时自动复制过去，
	// 方案导入时通过 [asset] 机制物化到该目录，因此不会保存易失效的临时路径。
	[DataMember]
	public string CustomImage = "";

	// 裁切方式：0=填充 1=适应 2=拉伸
	[DataMember]
	public int ImageCrop = 0;

	[DataMember]
	public double ImageBlur = 0.0;

	// 遮罩浓度 0..0.9（压暗背景图保证文字对比度）
	[DataMember]
	public double ImageDarken = 0.35;

	// —— 排版 ——
	[DataMember]
	public double FontSize = 14.95;

	[DataMember]
	public double IconSize = 23.0;

	[DataMember]
	public double ItemGap = 11.5;

	// 整体大小倍率（旧版唯一大小控件；保留仅用于旧配置迁移，新设置页使用宽/高独立倍率）
	[DataMember]
	public double SizeScale = 1.05;

	// 宽/高独立倍率：设置页的“宽度”“高度”两个滑杆自由调节
	// 宽度派生收起/展开宽度，高度派生收起高度；字号/图标/间距取两轴均值
	[DataMember]
	public double WidthScale = 1.1125;

	[DataMember]
	public double HeightScale = 1.1875;

	// —— 内容显示开关（收起态） ——
	[DataMember]
	public bool ShowMusic = true;

	[DataMember]
	public bool ShowCover = true;

	// 电量区域总开关；单独项目见 ShowPcBattery 等
	[DataMember]
	public bool ShowBattery = true;

	[DataMember]
	public bool ShowNotifyHint = true;

	[DataMember]
	public bool ShowPcBattery = true;

	[DataMember]
	public bool ShowMouseBattery = true;

	[DataMember]
	public bool ShowKeyboardBattery = true;

	// —— 展开面板页开关 ——
	[DataMember]
	public bool ShowMusicPage = true;

	[DataMember]
	public bool ShowNotificationsPage = true;

	[DataMember]
	public bool ShowControlPage = true;

	// —— 动效 ——
	// 0.5..2 倍速
	[DataMember]
	public double ExpandSpeed = 1.0;

	[DataMember]
	public bool AnimationEnabled = true;

	// —— 行为 ——
	[DataMember]
	public bool FollowSystemTheme = true;

	// 不跟随系统主题时的手动明暗（true = 浅色）
	[DataMember]
	public bool Light = true;

	// 全屏应用在前台时是否显示（关闭则自动隐藏，防止打扰游戏/视频）
	[DataMember]
	public bool ShowOnFullscreen = false;

	// 事件提示卡停留秒数 2..15
	[DataMember]
	public double NotifyDurationSec = 5.0;

	// 隐藏通知正文（只显示来源应用 + “有新通知”）
	[DataMember]
	public bool HideNotifyBody = false;

	// 不显示通知提示的应用（AppUserModelId 小写，允许空串表示未打包应用）
	[DataMember(EmitDefaultValue = false)]
	public List<string> MutedApps = new List<string>();

	// 低电量事件提示阈值（电脑电池百分比，达到即提示一次，回升后重新武装）
	[DataMember]
	public int LowBatteryThreshold = 20;

	// 跳动条样式：0=霓虹辉光 1=彩虹 2=极光描边(峰值帽) 3=玻璃弹簧 4=频谱曲线
	[DataMember]
	public int EqualizerStyle = 4;

	// 收起态标题/歌词对齐：true=居中（默认），false=靠左
	public bool MusicAlignCenter = true;

	// —— 剪贴板历史 ——
	// 首次启用必须由用户在设置页主动开启（出厂默认开启，可随时关闭）
	[DataMember]
	public bool ClipboardEnabled = true;

	[DataMember]
	public bool ClipboardPaused = false;

	[DataMember]
	public int ClipboardMaxItems = 100;

	[DataMember]
	public int ClipboardRetainDays = 14;

	public IslandOptions Copy()
	{
		return new IslandOptions
		{
			Enabled = Enabled,
			Preset = Preset,
			Shape = Shape,
			CornerRadius = CornerRadius,
			CollapsedWidth = CollapsedWidth,
			CollapsedHeight = CollapsedHeight,
			ExpandedWidth = ExpandedWidth,
			TopGap = TopGap,
			MonitorIndex = MonitorIndex,
			Material = (Material ?? new MaterialOptions()).Copy(),
			Accent = Accent,
			BackgroundColor = BackgroundColor,
			OpacityPercent = OpacityPercent,
			BlurRadius = BlurRadius,
			BorderColor = BorderColor,
			BorderOpacity = BorderOpacity,
			ShadowStrength = ShadowStrength,
			CustomImage = CustomImage,
			ImageCrop = ImageCrop,
			ImageBlur = ImageBlur,
			ImageDarken = ImageDarken,
			FontSize = FontSize,
			IconSize = IconSize,
			ItemGap = ItemGap,
			SizeScale = SizeScale,
			WidthScale = WidthScale,
			HeightScale = HeightScale,
			ShowMusic = ShowMusic,
			ShowCover = ShowCover,
			ShowBattery = ShowBattery,
			ShowNotifyHint = ShowNotifyHint,
			ShowPcBattery = ShowPcBattery,
			ShowMouseBattery = ShowMouseBattery,
			ShowKeyboardBattery = ShowKeyboardBattery,
			ShowMusicPage = ShowMusicPage,
			ShowNotificationsPage = ShowNotificationsPage,
			ShowControlPage = ShowControlPage,
			ExpandSpeed = ExpandSpeed,
			AnimationEnabled = AnimationEnabled,
			FollowSystemTheme = FollowSystemTheme,
			Light = Light,
			ShowOnFullscreen = ShowOnFullscreen,
			NotifyDurationSec = NotifyDurationSec,
			HideNotifyBody = HideNotifyBody,
			MutedApps = (MutedApps ?? new List<string>()).Select((string a) => (a ?? "").ToLowerInvariant()).ToList(),
			LowBatteryThreshold = LowBatteryThreshold,
			EqualizerStyle = EqualizerStyle,
			MusicAlignCenter = MusicAlignCenter,
			ClipboardEnabled = ClipboardEnabled,
			ClipboardPaused = ClipboardPaused,
			ClipboardMaxItems = ClipboardMaxItems,
			ClipboardRetainDays = ClipboardRetainDays,
			AlwaysOnTop = AlwaysOnTop
		};
	}

	// 应用预设皮肤：把一组既定外观写入选项（Preset 保持当前值，由调用方设置）。
	public static void ApplyPreset(IslandOptions o, int preset, bool light)
	{
		if (o == null)
		{
			return;
		}
		switch (preset)
		{
		case 0: // GlassBoard 毛玻璃
			o.Material = new MaterialOptions
			{
				Kind = 0,
				Tint = "",
				Gloss = 0.55,
				Grain = 0.22,
				Shadow = true
			};
			o.Accent = 0;
			o.BackgroundColor = "";
			o.OpacityPercent = 78.0;
			o.BlurRadius = 18.0;
			o.BorderColor = "";
			o.BorderOpacity = 0.35;
			o.ShadowStrength = 0.3;
			o.Shape = 0;
			o.CornerRadius = 22.0;
			break;
		case 1: // 清透
			o.Material = new MaterialOptions
			{
				Kind = 1,
				Tint = "",
				Gloss = 0.65,
				Grain = 0.0,
				Shadow = true
			};
			o.Accent = 0;
			o.BackgroundColor = "";
			o.OpacityPercent = 62.0;
			o.BlurRadius = 12.0;
			o.BorderColor = "";
			o.BorderOpacity = 0.55;
			o.ShadowStrength = 0.22;
			o.Shape = 0;
			o.CornerRadius = 20.0;
			break;
		case 2: // 纯色
			o.Material = new MaterialOptions
			{
				Kind = 2,
				Tint = "",
				Gloss = 0.0,
				Grain = 0.0,
				Shadow = true
			};
			o.Accent = 0;
			o.BackgroundColor = light ? "#F2F4F8" : "#23262E";
			o.OpacityPercent = 100.0;
			o.BlurRadius = 0.0;
			o.BorderColor = "";
			o.BorderOpacity = 0.25;
			o.ShadowStrength = 0.35;
			o.Shape = 1;
			o.CornerRadius = 14.0;
			break;
		case 3: // 深色
			o.Material = new MaterialOptions
			{
				Kind = 2,
				Tint = "",
				Gloss = 0.0,
				Grain = 0.0,
				Shadow = true
			};
			o.Accent = 0;
			o.BackgroundColor = "#12141A";
			o.OpacityPercent = 100.0;
			o.BlurRadius = 0.0;
			o.BorderColor = "#3A4150";
			o.BorderOpacity = 0.5;
			o.ShadowStrength = 0.4;
			o.Shape = 0;
			o.CornerRadius = 22.0;
			break;
		default:
			break; // 自定义：不动
		}
	}

	public void Normalize()
	{
		if (Material == null)
		{
			Material = new MaterialOptions();
		}
		Material.Normalize();
		// 旧版预设皮肤写入的自定义底色/边框色会盖过“黑白配色”与“跟随主题”的明暗计算
		// （IslandChrome.SolidFill 优先用 BackgroundColor），导致选浅色/深色/跟随主题都无效。
		// ApplyPreset 已无调用方、设置页也已无皮肤入口，加载时直接清掉旧残留。
		BackgroundColor = "";
		BorderColor = "";
		// 收起态内容开关已从设置中移除（用户要求）：音乐/封面/电量/通知提示恒显示，
		// 仅在确有有效数据时显示对应项目。旧配置中保存过的 false 一律忽略。
		ShowMusic = true;
		ShowCover = true;
		ShowBattery = true;
		ShowPcBattery = true;
		ShowMouseBattery = true;
		ShowKeyboardBattery = true;
		ShowNotifyHint = true;
		// “浅色固定”（FollowSystemTheme=false + Light=true）是合法配置，允许保留；
		// 早期误迁移曾把它强制改回跟随系统，导致设置页切浅色无效。
		Preset = Math.Max(0, Math.Min(4, Preset));
		Shape = Math.Max(0, Math.Min(1, Shape));
		CornerRadius = LayoutRules.Clamp(CornerRadius, 0.0, 48.0, 22.0);
		// 大小倍率：设置页提供“宽度”“高度”两个独立滑杆（默认 100%，范围 80%–200%）。
		// 旧配置只有 SizeScale（100%–160%）：迁移到两个轴，保留旧值语义。
		SizeScale = LayoutRules.Clamp(SizeScale, 1.0, 1.6, 1.0);
		// 迁移：早期版本缺失 SizeScale 字段时反序列化得到 0（被钳到 0.6）并保存过污染值；
		// 也可能保存过 80% 等过小值。设置页下限现为 100%，因此 <0.99 一律重置为 1.0。
		if (SizeScale < 0.99)
		{
			SizeScale = 1.0;
		}
		// 旧配置缺失 Width/HeightScale 字段 → 反序列化为 0 → 取 SizeScale 作为初值
		WidthScale = LayoutRules.Clamp(WidthScale < 0.99 ? SizeScale : WidthScale, 0.8, 2.0, 1.0);
		HeightScale = LayoutRules.Clamp(HeightScale < 0.99 ? SizeScale : HeightScale, 0.8, 2.0, 1.0);
		CollapsedWidth = LayoutRules.Clamp(320.0 * WidthScale, 180.0, 640.0, 320.0);
		CollapsedHeight = LayoutRules.Clamp(64.0 * HeightScale, 40.0, 128.0, 64.0);
		ExpandedWidth = LayoutRules.Clamp(380.0 * WidthScale, 240.0, 760.0, 380.0);
		TopGap = LayoutRules.Clamp(TopGap, 0.0, 200.0, 10.0);
		MonitorIndex = Math.Max(0, MonitorIndex);
		Accent = Math.Max(0, Math.Min(5, Accent));
		if (BackgroundColor == null)
		{
			BackgroundColor = "";
		}
		OpacityPercent = LayoutRules.Clamp(OpacityPercent, 10.0, 100.0, 78.0);
		BlurRadius = LayoutRules.Clamp(BlurRadius, 0.0, 40.0, 18.0);
		if (BorderColor == null)
		{
			BorderColor = "";
		}
		BorderOpacity = LayoutRules.Clamp(BorderOpacity, 0.0, 1.0, 0.35);
		ShadowStrength = LayoutRules.Clamp(ShadowStrength, 0.0, 0.8, 0.3);
		if (CustomImage == null)
		{
			CustomImage = "";
		}
		ImageCrop = Math.Max(0, Math.Min(2, ImageCrop));
		ImageBlur = LayoutRules.Clamp(ImageBlur, 0.0, 40.0, 0.0);
		ImageDarken = LayoutRules.Clamp(ImageDarken, 0.0, 0.9, 0.35);
		double midScale = (WidthScale + HeightScale) * 0.5;
		FontSize = LayoutRules.Clamp(13.0 * midScale, 10.0, 24.0, 13.0);
		IconSize = LayoutRules.Clamp(20.0 * midScale, 12.0, 36.0, 20.0);
		ItemGap = LayoutRules.Clamp(10.0 * midScale, 4.0, 32.0, 10.0);
		ExpandSpeed = LayoutRules.Clamp(ExpandSpeed, 0.5, 2.0, 1.0);
		NotifyDurationSec = LayoutRules.Clamp(NotifyDurationSec, 2.0, 15.0, 5.0);
		LowBatteryThreshold = Math.Max(5, Math.Min(95, LowBatteryThreshold));
		EqualizerStyle = Math.Max(0, Math.Min(4, EqualizerStyle));
		ClipboardMaxItems = Math.Max(10, Math.Min(500, ClipboardMaxItems));
		ClipboardRetainDays = Math.Max(1, Math.Min(90, ClipboardRetainDays));
		if (MutedApps == null)
		{
			MutedApps = new List<string>();
		}
		else
		{
			MutedApps = MutedApps.Where((string a) => a != null).Select((string a) => a.ToLowerInvariant()).Distinct().ToList();
		}
	}

	// 自定义背景图必须在应用管理的目录内；越界（例如旧配置引用了用户目录或临时目录）时
	// 清空引用，绝不让设置页/方案系统保存一个易失效的路径。
	public void SanitizeImagePath()
	{
		if (string.IsNullOrWhiteSpace(CustomImage))
		{
			return;
		}
		try
		{
			string root = Path.GetFullPath(Store.Folder);
			string full = Path.GetFullPath(CustomImage);
			if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
			{
				CustomImage = "";
			}
		}
		catch
		{
			CustomImage = "";
		}
	}
}
