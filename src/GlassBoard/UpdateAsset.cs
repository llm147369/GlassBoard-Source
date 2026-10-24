using System.Runtime.Serialization;

namespace GlassBoard;

[DataContract]
public class UpdateAsset
{
	[DataMember(Name = "name")]
	public string Name;

	[DataMember(Name = "size")]
	public long Size;

	[DataMember(Name = "browser_download_url")]
	public string ReleaseUrl;

	[DataMember(Name = "download_url")]
	public string FileUrl;

	[DataMember(Name = "digest")]
	public string Digest;

	[DataMember(Name = "sha")]
	public string BlobSha;

	[DataMember(Name = "type")]
	public string Type;
}
