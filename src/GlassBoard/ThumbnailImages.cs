using System.Windows.Media.Imaging;

namespace GlassBoard;

public static class ThumbnailImages
{
	public static BitmapImage Read(byte[] bytes, int maxEdge)
	{
		using ReleasingReadStream releasingReadStream = new ReleasingReadStream(bytes);
		BitmapFrame bitmapFrame = BitmapDecoder.Create(releasingReadStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
		int pixelWidth = bitmapFrame.PixelWidth;
		int pixelHeight = bitmapFrame.PixelHeight;
		int[] array = ImageDecodeRules.Fit(pixelWidth, pixelHeight, maxEdge);
		releasingReadStream.Position = 0L;
		BitmapImage bitmapImage = new BitmapImage();
		bitmapImage.BeginInit();
		bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
		if (array[0] != pixelWidth || array[1] != pixelHeight)
		{
			if (pixelWidth >= pixelHeight)
			{
				bitmapImage.DecodePixelWidth = array[0];
			}
			else
			{
				bitmapImage.DecodePixelHeight = array[1];
			}
		}
		bitmapImage.StreamSource = releasingReadStream;
		bitmapImage.EndInit();
		bitmapImage.Freeze();
		return bitmapImage;
	}
}
