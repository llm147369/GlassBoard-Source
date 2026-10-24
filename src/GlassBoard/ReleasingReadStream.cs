using System;
using System.IO;

namespace GlassBoard;

internal sealed class ReleasingReadStream : Stream
{
	private Stream inner;

	private Stream Current
	{
		get
		{
			if (inner == null)
			{
				throw new ObjectDisposedException("ReleasingReadStream");
			}
			return inner;
		}
	}

	public override bool CanRead => inner != null;

	public override bool CanSeek => inner != null;

	public override bool CanWrite => false;

	public override long Length => Current.Length;

	public override long Position
	{
		get
		{
			return Current.Position;
		}
		set
		{
			Current.Position = value;
		}
	}

	public ReleasingReadStream(byte[] bytes)
	{
		inner = new MemoryStream(bytes, writable: false);
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return Current.Read(buffer, offset, count);
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		return Current.Seek(offset, origin);
	}

	public override void Flush()
	{
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		throw new NotSupportedException();
	}

	protected override void Dispose(bool disposing)
	{
		Stream stream = inner;
		inner = null;
		if (disposing)
		{
			stream?.Dispose();
		}
		base.Dispose(disposing);
	}
}
