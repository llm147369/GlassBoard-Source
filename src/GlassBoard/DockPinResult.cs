using System.Collections.Generic;

namespace GlassBoard;

public sealed class DockPinResult
{
	public int Added;

	public int Skipped;

	public List<string> Paths = new List<string>();
}
