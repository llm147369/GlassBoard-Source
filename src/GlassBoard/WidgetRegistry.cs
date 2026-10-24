using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public sealed class WidgetRegistry<T> where T : class
{
	private readonly Dictionary<int, T> items = new Dictionary<int, T>();

	public List<T> Values => items.Values.ToList();

	public List<int> Keys => items.Keys.ToList();

	public bool TryGetValue(int id, out T value)
	{
		return items.TryGetValue(id, out value);
	}

	public void Add(int id, T value)
	{
		if (value == null)
		{
			throw new ArgumentNullException("value");
		}
		items.Add(id, value);
	}

	public bool Remove(int id, T expected)
	{
		if (!items.TryGetValue(id, out var value) || value != expected)
		{
			return false;
		}
		return items.Remove(id);
	}
}
