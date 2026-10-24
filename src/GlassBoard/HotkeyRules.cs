using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public static class HotkeyRules
{
	public static readonly string[] Actions = new string[7] { "展开 / 收起面板", "隐藏 / 显示任务栏", "隐藏 / 显示桌面图标", "添加软件", "外观设置", "恢复任务栏与图标", "Windows 任务栏自动隐藏" };

	public static readonly uint[] ModifierValues = new uint[7] { 3u, 6u, 5u, 7u, 2u, 1u, 4u };

	public static readonly string[] ModifierNames = new string[7] { "Ctrl + Alt", "Ctrl + Shift", "Alt + Shift", "Ctrl + Alt + Shift", "Ctrl", "Alt", "Shift" };

	public static List<HotkeySetting> Defaults()
	{
		return new uint[7] { 32u, 84u, 68u, 65u, 83u, 82u, 72u }.Select((uint k, int i) => new HotkeySetting
		{
			ActionId = i + 1,
			Modifiers = 3u,
			Key = k,
			Enabled = true
		}).ToList();
	}

	public static Dictionary<string, uint> Keys()
	{
		Dictionary<string, uint> dictionary = new Dictionary<string, uint>();
		dictionary["Space"] = 32u;
		for (uint num = 65u; num <= 90; num++)
		{
			dictionary[((char)num).ToString()] = num;
		}
		for (uint num2 = 48u; num2 <= 57; num2++)
		{
			dictionary[((char)num2).ToString()] = num2;
		}
		for (uint num3 = 1u; num3 <= 12; num3++)
		{
			dictionary["F" + num3] = 111 + num3;
		}
		dictionary["Enter"] = 13u;
		dictionary["Tab"] = 9u;
		dictionary["Home"] = 36u;
		dictionary["End"] = 35u;
		dictionary["PageUp"] = 33u;
		dictionary["PageDown"] = 34u;
		dictionary["Insert"] = 45u;
		dictionary["Delete"] = 46u;
		return dictionary;
	}

	public static string Label(HotkeySetting key)
	{
		if (key == null || !key.Enabled)
		{
			return "已关闭";
		}
		int num = Array.IndexOf(ModifierValues, key.Modifiers);
		string text = Keys().FirstOrDefault((KeyValuePair<string, uint> p) => p.Value == key.Key).Key ?? ("VK " + key.Key);
		return ((num >= 0) ? ModifierNames[num] : "?") + " + " + text;
	}

	public static string Validate(List<HotkeySetting> keys)
	{
		if (keys == null || keys.Count != Actions.Length || keys.Select((HotkeySetting k) => k?.ActionId ?? 0).Distinct().Count() != Actions.Length || keys.Any((HotkeySetting k) => k == null || k.ActionId < 1 || k.ActionId > Actions.Length))
		{
			return "快捷键配置必须包含全部七项功能。";
		}
		if (keys.Any((HotkeySetting k) => !ModifierValues.Contains(k.Modifiers) || !Keys().ContainsValue(k.Key)))
		{
			return "请选择有效的组合键和按键。";
		}
		IGrouping<string, HotkeySetting> grouping = (from k in keys
			where k.Enabled
			group k by k.Modifiers + ":" + k.Key).FirstOrDefault((IGrouping<string, HotkeySetting> g) => g.Count() > 1);
		if (grouping != null)
		{
			return "快捷键重复：" + string.Join("、", grouping.Select((HotkeySetting k) => Actions[k.ActionId - 1])) + "。请换一个组合。";
		}
		return null;
	}

	public static List<HotkeySetting> Normalize(List<HotkeySetting> keys)
	{
		List<HotkeySetting> list = Defaults();
		if (keys != null)
		{
			foreach (HotkeySetting item in keys.Where((HotkeySetting k) => k != null && k.ActionId >= 1 && k.ActionId <= Actions.Length && ModifierValues.Contains(k.Modifiers) && Keys().ContainsValue(k.Key)))
			{
				list[item.ActionId - 1] = item.Copy();
			}
		}
		if (Validate(list) != null && keys != null && keys.All((HotkeySetting k) => k == null || k.ActionId != 7))
		{
			list[6].Enabled = false;
		}
		if (Validate(list) != null)
		{
			return Defaults();
		}
		return list;
	}

	public static bool Same(List<HotkeySetting> first, List<HotkeySetting> second)
	{
		if (first == null || second == null || first.Count != second.Count)
		{
			return false;
		}
		return first.All((HotkeySetting a) => second.Any((HotkeySetting b) => a.ActionId == b.ActionId && a.Modifiers == b.Modifiers && a.Key == b.Key && a.Enabled == b.Enabled));
	}
}
