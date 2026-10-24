using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public class HotkeyManager
{
	private readonly Func<HotkeySetting, bool> register;

	private readonly Action<int> unregister;

	public List<HotkeySetting> Active = new List<HotkeySetting>();

	public HotkeyManager(Func<HotkeySetting, bool> registerKey, Action<int> unregisterKey)
	{
		register = registerKey;
		unregister = unregisterKey;
	}

	public List<string> Start(IEnumerable<HotkeySetting> keys)
	{
		List<string> list = new List<string>();
		foreach (HotkeySetting item in keys.Where((HotkeySetting k) => k.Enabled))
		{
			if (register(item))
			{
				Active.Add(item.Copy());
			}
			else
			{
				list.Add(HotkeyRules.Actions[item.ActionId - 1] + "（" + HotkeyRules.Label(item) + "）");
			}
		}
		return list;
	}

	public void Release()
	{
		foreach (HotkeySetting item in Active)
		{
			unregister(item.ActionId);
		}
		Active.Clear();
	}

	public bool TryApply(List<HotkeySetting> candidate, out string error)
	{
		error = HotkeyRules.Validate(candidate);
		if (error != null)
		{
			return false;
		}
		List<HotkeySetting> keys = Active.Select((HotkeySetting k) => k.Copy()).ToList();
		Release();
		List<string> list = Start(candidate);
		if (list.Count == 0)
		{
			return true;
		}
		Release();
		List<string> list2 = Start(keys);
		error = "无法注册：" + string.Join("、", list) + "。可能被其他软件或 Windows 占用，原设置未改动。";
		if (list2.Count > 0)
		{
			error = error + "\n原快捷键恢复失败：" + string.Join("、", list2) + "。对应操作可从托盘执行。";
		}
		return false;
	}
}
