using System;
using System.Collections.Generic;
using System.Linq;

namespace GlassBoard;

public sealed class TrackArtworkCache<T> where T : class
{
	private sealed class Item
	{
		public T Value;

		public DateTime At;
	}

	private readonly Dictionary<string, Item> items = new Dictionary<string, Item>(StringComparer.Ordinal);

	private readonly int limit;

	public TrackArtworkCache(int capacity = 8)
	{
		limit = Math.Max(1, capacity);
	}

	public void Put(string key, T value, DateTime now)
	{
		if (string.IsNullOrEmpty(key) || value == null)
		{
			return;
		}
		items[key] = new Item
		{
			Value = value,
			At = now
		};
		while (items.Count > limit)
		{
			items.Remove(items.OrderBy((KeyValuePair<string, Item> p) => p.Value.At).First().Key);
		}
	}

	public T Get(string key, DateTime now)
	{
		if (string.IsNullOrEmpty(key) || !items.TryGetValue(key, out var value))
		{
			return null;
		}
		if ((now - value.At).TotalMinutes > 15.0)
		{
			items.Remove(key);
			return null;
		}
		return value.Value;
	}

	public void Clear()
	{
		items.Clear();
	}
}
