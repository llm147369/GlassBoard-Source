using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace GlassBoard;

public static class MediaWireProtocol
{
	public static void Write(Stream stream, object value)
	{
		using MemoryStream memoryStream = new MemoryStream();
		new DataContractJsonSerializer(value.GetType()).WriteObject((Stream)memoryStream, value);
		byte[] array = memoryStream.ToArray();
		stream.Write(array, 0, array.Length);
		stream.WriteByte(10);
		stream.Flush();
	}

	public static MediaRequest ReadRequest(string line)
	{
		using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(line));
		return (MediaRequest)new DataContractJsonSerializer(typeof(MediaRequest)).ReadObject((Stream)stream);
	}

	public static void Loop(TextReader input, Stream output, Func<MediaRequest, MediaSnapshot> read)
	{
		string line;
		while ((line = input.ReadLine()) != null)
		{
			MediaRequest mediaRequest = null;
			MediaSnapshot value;
			try
			{
				mediaRequest = ReadRequest(line);
				if (mediaRequest.Command == "exit")
				{
					break;
				}
				value = read(mediaRequest);
			}
			catch (Exception ex)
			{
				value = new MediaSnapshot
				{
					Key = mediaRequest?.Key,
					Source = mediaRequest?.Source,
					Backend = "Netease UIA",
					Status = ex.Message
				};
			}
			Write(output, value);
		}
	}
}
