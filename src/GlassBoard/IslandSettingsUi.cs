using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;

namespace GlassBoard;

// “个性化 → 灵动岛”设置页。全部改动立即写入 Prefs.Island 并应用到灵动岛，
// 同时刷新收起/展开两个实时预览；保存/重置/恢复默认均有独立操作。
// 只作用于灵动岛：不触碰 Dock、Board、桌面组件等其它配置。
// 分组顺序（9 组）：①总开关与位置 ②外观尺寸 ③材质皮肤 ④收起态内容 ⑤事件与动效
// ⑥通知 ⑦剪贴板历史 ⑧音乐 ⑨专注与快捷入口 + 诊断/电量/保存与重置。
public static class IslandSettingsUi
{
	// 与 Controller.Settings() 内 func2/func3/func4 类型完全一致
	public static void Build(StackPanel p, Controller C, bool light,
		Func<StackPanel, string, double, double, double, double, string, Action<double>, Slider> slider,
		Func<StackPanel, string, bool, Action<bool>, System.Windows.Controls.CheckBox> check,
		Func<StackPanel, string, string[], int, Action<int>, System.Windows.Controls.ComboBox> combo,
		Action islandApply)
	{
		IslandOptions draft = (C.Prefs.Island ?? new IslandOptions()).Copy();
		draft.Normalize();
		// 预览控件每次 Render 都新建（旧的随页面重建丢弃），避免把同一控件重复挂到新控件树
		IslandPreview collapsedPreview = null;
		IslandPreview expandedPreview = null;

		void RenderPreviews()
		{
			if (collapsedPreview == null || expandedPreview == null)
			{
				return;
			}
			IslandOptions copy = draft.Copy();
			copy.Normalize();
			collapsedPreview.Render(copy);
			expandedPreview.Render(copy);
		}

		// 改动即应用：写回 Prefs、保存、实时刷新灵动岛与预览
		void Apply()
		{
			draft.Normalize();
			C.Prefs.Island = draft;
			C.Save();
			islandApply?.Invoke();
			RenderPreviews();
		}

		void Rebuild()
		{
			try
			{
				p.Children.Clear();
				Render(draft);
			}
			catch (Exception ex)
			{
				// 设置页重建失败不能静默：记录完整异常，恢复顶部开关与错误提示
				Store.Trace("ISLAND settings rebuild failed: " + ex.GetType().FullName + " " + ex.Message + "\n" + ex.StackTrace);
				RenderFailure(p, light, ex);
			}
		}

		void Render(IslandOptions d)
		{
			// —— ① 总开关与位置 ——
			Border main = Polish.Section("灵动岛", "顶部居中的动态浮层：音乐、电量、通知与快捷控制。所有设置仅影响灵动岛。", light);
			StackPanel mainPanel = (StackPanel)main.Child;
			check(mainPanel, "启用灵动岛", d.Enabled, delegate (bool on)
			{
				d.Enabled = on;
				Apply();
			});
			// 每次重建都创建新的预览控件，杜绝“已属于另一控件树”的重复挂载异常
			StackPanel previewRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
			collapsedPreview = new IslandPreview(C, false);
			expandedPreview = new IslandPreview(C, true);
			previewRow.Children.Add(collapsedPreview);
			previewRow.Children.Add(expandedPreview);
			mainPanel.Children.Add(previewRow);
			StackPanel viewRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
			viewRow.Children.Add(PreviewButton("收起态预览", light, delegate { RenderPreviews(); }));
			viewRow.Children.Add(PreviewButton("展开态预览", light, delegate { RenderPreviews(); }));
			mainPanel.Children.Add(viewRow);
			check(mainPanel, "保持在窗口最上方", d.AlwaysOnTop, delegate (bool v) { d.AlwaysOnTop = v; Apply(); });
			check(mainPanel, "全屏应用时显示", d.ShowOnFullscreen, delegate (bool v) { d.ShowOnFullscreen = v; Apply(); });
			RenderPreviews();
			p.Children.Add(main);

			// —— ② 外观尺寸 ——
			Border layout = Polish.Section("外观尺寸", "宽度与高度独立调节（单位：像素，80%–200%）；收起态布局决定收起条的信息组织方式。", light);
			StackPanel layoutPanel = (StackPanel)layout.Child;
			combo(layoutPanel, "收起态布局", new[] { "经典三段（封面·信息·状态）", "极简（封面·信息）", "信息优先（时钟·天气）" }, d.CollapsedLayout, delegate (int v) { d.CollapsedLayout = v; Apply(); });
			combo(layoutPanel, "形状", new[] { "胶囊", "圆角矩形" }, d.Shape, delegate (int v) { d.Shape = v; Apply(); });
			slider(layoutPanel, "圆角", 4, 48, d.CornerRadius, 1, "px", delegate (double v) { d.CornerRadius = v; Apply(); });
			slider(layoutPanel, "宽度", 180, 640, Math.Round(d.CollapsedWidth), 4, "px", delegate (double v) { d.WidthScale = v / 320.0; Apply(); });
			slider(layoutPanel, "高度", 40, 128, Math.Round(d.CollapsedHeight), 2, "px", delegate (double v) { d.HeightScale = v / 64.0; Apply(); });
			slider(layoutPanel, "顶部间距", 0, 160, d.TopGap, 2, "px", delegate (double v) { d.TopGap = v; Apply(); });
			string[] monitors = MonitorChoices();
			combo(layoutPanel, "显示器", monitors, Math.Min(Math.Max(d.MonitorIndex, 0), monitors.Length - 1), delegate (int v) { d.MonitorIndex = v; Apply(); });
			check(layoutPanel, "自适应对比度（浅色壁纸上自动加深文字与底衬）", d.AdaptiveContrast, delegate (bool v) { d.AdaptiveContrast = v; Apply(); });
			check(layoutPanel, "经典布局（回退到旧版展开面板：无聚合首页、无卡片化）", d.ClassicLayout, delegate (bool v) { d.ClassicLayout = v; Apply(); });
			p.Children.Add(layout);

			// —— ③ 配色与质感 ——
			Border mat = Polish.Section("材质皮肤", "黑白配色、透明度与模糊程度；其余玻璃质感参数按内置默认。", light);
			StackPanel matPanel = (StackPanel)mat.Child;
			combo(matPanel, "黑白配色", new[] { "跟随主题", "浅色", "深色" }, d.FollowSystemTheme ? 0 : (d.Light ? 1 : 2), delegate (int v)
			{
				d.FollowSystemTheme = v == 0;
				d.Light = v != 2;
				Apply();
			});
			slider(matPanel, "不透明度", 10, 100, d.OpacityPercent, 1, "%", delegate (double v) { d.OpacityPercent = v; Apply(); });
			slider(matPanel, "模糊程度", 0, 40, d.BlurRadius, 1, "px", delegate (double v) { d.BlurRadius = v; Apply(); });
			p.Children.Add(mat);

			// —— ④ 收起态内容 ——
			Border content = Polish.Section("收起态内容", "收起条上的信息芯片与提示开关；有数据才显示对应芯片。", light);
			StackPanel contentPanel = (StackPanel)content.Child;
			check(contentPanel, "显示时间", d.ShowTimeChip, delegate (bool v) { d.ShowTimeChip = v; Apply(); });
			check(contentPanel, "显示天气", d.ShowWeatherChip, delegate (bool v) { d.ShowWeatherChip = v; Apply(); });
			check(contentPanel, "显示电量", d.ShowBatteryChip, delegate (bool v) { d.ShowBatteryChip = v; Apply(); });
			check(contentPanel, "充电动效（充电绿脉冲 / 低电橙呼吸）", d.ChargeAnim, delegate (bool v) { d.ChargeAnim = v; Apply(); });
			check(contentPanel, "建议语轮播", d.SuggestionEnabled, delegate (bool v) { d.SuggestionEnabled = v; Apply(); });
			check(contentPanel, "显示音乐跳动条", d.EqualizerEnabled, delegate (bool v) { d.EqualizerEnabled = v; Apply(); });
			combo(contentPanel, "跳动条样式", new string[] { "霓虹辉光", "彩虹", "极光描边（峰值帽）", "玻璃弹簧", "频谱曲线" }, Math.Max(0, Math.Min(4, d.EqualizerStyle)), delegate (int v) { d.EqualizerStyle = v; Apply(); });
			combo(contentPanel, "标题与歌词对齐", new[] { "居中", "靠左" }, d.MusicAlignCenter ? 0 : 1, delegate (int v) { d.MusicAlignCenter = v == 0; Apply(); });
			slider(contentPanel, "低电量提醒阈值", 10, 50, d.LowBatteryThreshold, 1, "%", delegate (double v) { d.LowBatteryThreshold = (int)Math.Round(v); Apply(); });
			p.Children.Add(content);

			// —— ⑤ 事件与动效 ——
			Border motion = Polish.Section("事件与动效", "展开/收起动画、通知到达动画与音量亮度 HUD。", light);
			StackPanel motionPanel = (StackPanel)motion.Child;
			check(motionPanel, "展开动画", d.AnimationEnabled, delegate (bool v) { d.AnimationEnabled = v; Apply(); });
			slider(motionPanel, "动画速度", 0.5, 3, d.ExpandSpeed, 0.1, "×", delegate (double v) { d.ExpandSpeed = v; Apply(); });
			check(motionPanel, "弹簧动效（展开/收起带回弹）", d.SpringMotion, delegate (bool v) { d.SpringMotion = v; Apply(); });
			check(motionPanel, "岛眼睛（收起态显示一双跟随鼠标的眼睛）", d.EyesEnabled, delegate (bool v) { d.EyesEnabled = v; Apply(); });
			combo(motionPanel, "通知到达动画", new[] { "铃铛摇动 + 数字徽标", "仅数字徽标", "不显示" }, d.NotifyArrivalStyle, delegate (int v) { d.NotifyArrivalStyle = v; Apply(); });
			slider(motionPanel, "事件卡停留时间", 2, 15, d.NotifyDurationSec, 1, "秒", delegate (double v) { d.NotifyDurationSec = v; Apply(); });
			check(motionPanel, "音量 / 亮度 HUD", d.HudEnabled, delegate (bool v) { d.HudEnabled = v; Apply(); });
			slider(motionPanel, "HUD 停留时间", 0.8, 3, d.HudDurationSec, 0.1, "秒", delegate (double v) { d.HudDurationSec = v; Apply(); });
			p.Children.Add(motion);

			// —— ⑥ 通知 ——
			Border notify = Polish.Section("通知", "事件卡快捷操作、列表分组与按应用屏蔽。", light);
			StackPanel notifyPanel = (StackPanel)notify.Child;
			check(notifyPanel, "“通知”页面", d.ShowNotificationsPage, delegate (bool v) { d.ShowNotificationsPage = v; Apply(); });
			check(notifyPanel, "事件卡快捷操作（已读 / 静默该应用）", d.ToastActions, delegate (bool v) { d.ToastActions = v; Apply(); });
			check(notifyPanel, "通知列表按应用分组", d.NotifyGrouping, delegate (bool v) { d.NotifyGrouping = v; Apply(); });
			check(notifyPanel, "隐藏通知正文", d.HideNotifyBody, delegate (bool v) { d.HideNotifyBody = v; Apply(); });
			Dictionary<string, string> known = IslandNotifications.Recent()
				.GroupBy((IslandNotification n) => string.IsNullOrEmpty(n.Aumid) ? n.AppName : n.Aumid)
				.Select((IGrouping<string, IslandNotification> g) => g.First())
				.Take(12)
				.ToDictionary((IslandNotification n) => n.AppName + "\n" + (n.Aumid ?? ""), (IslandNotification n) => n.Aumid);
			foreach (KeyValuePair<string, string> kv in known)
			{
				string key = kv.Value ?? kv.Key.Split('\n')[0];
				check(notifyPanel, "屏蔽 " + kv.Key.Split('\n')[0], d.MutedApps.Contains(key, StringComparer.OrdinalIgnoreCase), delegate (bool v)
				{
					if (v)
					{
						if (!d.MutedApps.Contains(key, StringComparer.OrdinalIgnoreCase))
						{
							d.MutedApps.Add(key);
						}
					}
					else
					{
						d.MutedApps.RemoveAll((string a) => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
					}
					Apply();
				});
			}
			if (known.Count == 0)
			{
				notifyPanel.Children.Add(new TextBlock
				{
					Text = "还没有收到过通知。收到后即可在此按应用控制。",
					FontSize = 11,
					Foreground = Polish.Muted(light)
				});
			}
			p.Children.Add(notify);

			// —— ⑦ 剪贴板历史 ——
			Border clip = Polish.Section("剪贴板历史", "使用系统剪贴板更新通知（非轮询）。内容只保存在本机；锁屏/隐私模式下隐藏预览。", light);
			StackPanel clipPanel = (StackPanel)clip.Child;
			check(clipPanel, "启用剪贴板历史", d.ClipboardEnabled, delegate (bool v)
			{
				d.ClipboardEnabled = v;
				IslandClipboard.SetEnabled(v, d.ClipboardMaxItems, d.ClipboardRetainDays);
				Apply();
			});
			check(clipPanel, "暂停记录", d.ClipboardPaused, delegate (bool v)
			{
				d.ClipboardPaused = v;
				IslandClipboard.SetPaused(v);
				Apply();
			});
			check(clipPanel, "列表搜索框", d.ClipSearch, delegate (bool v) { d.ClipSearch = v; Apply(); });
			check(clipPanel, "类型图标（文字 / 图片 / 文件）", d.ClipTypeIcons, delegate (bool v) { d.ClipTypeIcons = v; Apply(); });
			check(clipPanel, "双击条目粘贴到前台窗口", d.ClipDoublePaste, delegate (bool v) { d.ClipDoublePaste = v; Apply(); });
			slider(clipPanel, "历史条数上限", 10, 500, d.ClipboardMaxItems, 10, "条", delegate (double v) { d.ClipboardMaxItems = (int)Math.Round(v); IslandClipboard.SetEnabled(d.ClipboardEnabled, (int)Math.Round(v), d.ClipboardRetainDays); Apply(); });
			slider(clipPanel, "保留时间", 1, 90, d.ClipboardRetainDays, 1, "天", delegate (double v) { d.ClipboardRetainDays = (int)Math.Round(v); IslandClipboard.SetEnabled(d.ClipboardEnabled, d.ClipboardMaxItems, (int)Math.Round(v)); Apply(); });
			StackPanel clipBtns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
			clipBtns.Children.Add(FeatureUi.Button("清空剪贴板历史", delegate { IslandClipboard.Clear(); Apply(); }));
			clipPanel.Children.Add(clipBtns);
			clipPanel.Children.Add(new TextBlock
			{
				Text = "注意：无法保证识别所有密码或敏感内容；隐私模式下自动隐藏正文预览。",
				FontSize = 11,
				Foreground = Polish.Muted(light),
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 6, 0, 0)
			});
			p.Children.Add(clip);

			// —— ⑧ 音乐 ——
			Border musicSet = Polish.Section("音乐", "音乐页增强与本地/系统播放源。", light);
			StackPanel musicSetPanel = (StackPanel)musicSet.Child;
			check(musicSetPanel, "“正在播放”页面", d.ShowMusicPage, delegate (bool v) { d.ShowMusicPage = v; Apply(); });
			check(musicSetPanel, "收藏按钮（本地收藏列表）", d.MusicFavorite, delegate (bool v) { d.MusicFavorite = v; Apply(); });
			check(musicSetPanel, "媒体源切换（本地 / 系统）", d.MusicSourceSwitch, delegate (bool v) { d.MusicSourceSwitch = v; Apply(); });
			check(musicSetPanel, "悬停进度条预览时间", d.MusicProgressPeek, delegate (bool v) { d.MusicProgressPeek = v; Apply(); });
			check(musicSetPanel, "滚轮调节音量", d.MusicWheelVolume, delegate (bool v) { d.MusicWheelVolume = v; Apply(); });
			p.Children.Add(musicSet);

			// —— ⑨ 快捷入口 ——
			// 专注模式设置已移入灵动岛首页“专注”磁贴面板（时长/休息/结束提醒都在岛上调）。
			Border focus = Polish.Section("快捷入口", "控制中心页面与岛内快捷启动器。专注模式请在灵动岛首页的“专注”磁贴上直接设置时长。", light);
			StackPanel focusPanel = (StackPanel)focus.Child;
			check(focusPanel, "“控制中心”页面", d.ShowControlPage, delegate (bool v) { d.ShowControlPage = v; Apply(); });
			check(focusPanel, "快捷启动器（岛内搜索回车呼出应用面板）", d.QuickLauncher, delegate (bool v) { d.QuickLauncher = v; Apply(); });
			p.Children.Add(focus);

			// —— 通知监听诊断 ——
			Border diag = Polish.Section("通知监听状态", "监听 Windows 通知需要“通知”访问权限。授权被拒绝或撤销时，灵动岛收不到新通知；正文无法读取时只显示来源应用。", light);
			StackPanel diagPanel = (StackPanel)diag.Child;
			diagPanel.Children.Add(new TextBlock
			{
				Text = NotificationCenter.IslandStatus(),
				FontSize = 11,
				Foreground = Polish.Muted(light),
				TextWrapping = TextWrapping.Wrap
			});
			diagPanel.Children.Add(new TextBlock
			{
				Text = ChatPopupReader.Status(),
				FontSize = 11,
				Foreground = Polish.Muted(light),
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 3, 0, 0)
			});
			StackPanel diagBtns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
			diagBtns.Children.Add(FeatureUi.Button("重新检查授权", delegate
			{
				NotificationCenter.Recheck();
				Apply();
				Rebuild();
			}));
			diagBtns.Children.Add(FeatureUi.Button("打开 Windows 通知设置", delegate
			{
				try
				{
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
				}
				catch (Exception ex)
				{
					Store.Trace("ISLAND open notification settings failed: " + ex.Message);
				}
			}));
			diagPanel.Children.Add(diagBtns);
			p.Children.Add(diag);

			// —— 电量检测说明 ——
			Border battery = Polish.Section("电量检测", "电脑电池经 Windows 电源状态接口读取；鼠标/键盘经 HID 电池服务读取。没有有效数据的项目会被完全隐藏。", light);
			StackPanel batteryPanel = (StackPanel)battery.Child;
			batteryPanel.Children.Add(new TextBlock
			{
				Text = BatteryCore.Diagnostic(),
				FontSize = 11,
				Foreground = Polish.Muted(light),
				TextWrapping = TextWrapping.Wrap
			});
			batteryPanel.Children.Add(FeatureUi.Button("重新检测", delegate { BatteryCore.Start(System.Windows.Threading.Dispatcher.CurrentDispatcher); Apply(); }));
			p.Children.Add(battery);

			// —— 保存 / 重置 / 恢复默认 ——
			Border actions = Polish.Section("保存与重置", "所有改动即时生效；“重置样式”只恢复外观参数，保留功能开关与数据；“恢复默认”恢复全部默认值。", light);
			StackPanel actionPanel = (StackPanel)actions.Child;
			StackPanel actionRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
			actionRow.Children.Add(FeatureUi.Button("保存当前设置", delegate { Apply(); }));
			actionRow.Children.Add(FeatureUi.Button("重置样式", delegate
			{
				bool enabled = draft.Enabled;
				bool clipEnabled = draft.ClipboardEnabled;
				bool clipPaused = draft.ClipboardPaused;
				int clipMax = draft.ClipboardMaxItems;
				int clipRetain = draft.ClipboardRetainDays;
				List<string> muted = new List<string>(draft.MutedApps);
				draft = new IslandOptions
				{
					Enabled = enabled,
					ClipboardEnabled = clipEnabled,
					ClipboardPaused = clipPaused,
					ClipboardMaxItems = clipMax,
					ClipboardRetainDays = clipRetain,
					MutedApps = muted
				};
				Apply();
				Rebuild();
			}));
			actionRow.Children.Add(FeatureUi.Button("恢复默认", delegate
			{
				draft = new IslandOptions { Enabled = draft.Enabled };
				IslandClipboard.SetEnabled(false, draft.ClipboardMaxItems, draft.ClipboardRetainDays);
				Apply();
				Rebuild();
			}));
			actionPanel.Children.Add(actionRow);
			p.Children.Add(actions);
		}

		Render(draft);
	}

	// 设置页重建失败时显示可读的错误信息（完整异常已写日志），保证设置页不会整体不可用。
	private static void RenderFailure(StackPanel p, bool light, Exception ex)
	{
		try
		{
			Border fail = Polish.Section("灵动岛", "设置页重建失败。可尝试“恢复默认”，或关闭后重新打开设置页。", light);
			StackPanel failPanel = (StackPanel)fail.Child;
			failPanel.Children.Add(new TextBlock
			{
				Text = ex.GetType().FullName + ": " + ex.Message,
				FontSize = 11,
				Foreground = Polish.Muted(light),
				TextWrapping = TextWrapping.Wrap
			});
			p.Children.Add(fail);
		}
		catch
		{
		}
	}

	private static Border PreviewButton(string label, bool light, Action click)
	{
		Border b = new Border
		{
			CornerRadius = new CornerRadius(12),
			Background = Polish.Card(light),
			Padding = new Thickness(12, 6, 12, 6),
			Margin = new Thickness(4, 0, 4, 0),
			Cursor = System.Windows.Input.Cursors.Hand
		};
		b.Child = new TextBlock { Text = label, FontSize = 12, Foreground = Polish.Ink(light) };
		b.MouseLeftButtonUp += (object s, System.Windows.Input.MouseButtonEventArgs e) => click();
		return b;
	}

	private static string[] MonitorChoices()
	{
		Screen[] screens = Screen.AllScreens;
		if (screens.Length == 0)
		{
			return new[] { "主显示器" };
		}
		string[] result = new string[screens.Length];
		for (int i = 0; i < screens.Length; i++)
		{
			result[i] = "显示器 " + (i + 1) + (screens[i].Primary ? "（主）" : "") + " " + screens[i].Bounds.Width + "×" + screens[i].Bounds.Height;
		}
		return result;
	}
}
