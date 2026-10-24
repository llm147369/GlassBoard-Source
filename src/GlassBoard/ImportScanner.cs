using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace GlassBoard;

public static class ImportScanner
{
	public static ScanResult Scan(IEnumerable<string> roots, bool recurse, CancellationToken cancellation, int fileLimit = 2500, int directoryLimit = 10000, bool launchOnly = false)
	{
		ScanResult scanResult = new ScanResult();
		Stack<string> stack = new Stack<string>(roots.Where(Directory.Exists));
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		while (stack.Count > 0)
		{
			cancellation.ThrowIfCancellationRequested();
			string text = stack.Pop();
			if (!hashSet.Add(text))
			{
				continue;
			}
			if (hashSet.Count > directoryLimit || scanResult.Paths.Count >= fileLimit)
			{
				scanResult.Limited = true;
				break;
			}
			try
			{
				foreach (string item in Directory.EnumerateFiles(text))
				{
					cancellation.ThrowIfCancellationRequested();
					if (EntryRules.Importable(item) && (!launchOnly || StartupEntryRules.Candidate(item)))
					{
						scanResult.Paths.Add(item);
					}
					if (scanResult.Paths.Count >= fileLimit)
					{
						scanResult.Limited = true;
						break;
					}
				}
				if (!recurse)
				{
					continue;
				}
				foreach (string item2 in Directory.EnumerateDirectories(text))
				{
					cancellation.ThrowIfCancellationRequested();
					try
					{
						if ((File.GetAttributes(item2) & FileAttributes.ReparsePoint) == 0)
						{
							stack.Push(item2);
						}
					}
					catch
					{
						scanResult.Skipped++;
					}
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				scanResult.Skipped++;
			}
		}
		return scanResult;
	}
}
