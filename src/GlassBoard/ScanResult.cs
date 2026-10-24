using System.Collections.Generic;

namespace GlassBoard;

public class ScanResult
{
	public List<string> Paths = new List<string>();

	public int Skipped;

	public bool Limited;
}
