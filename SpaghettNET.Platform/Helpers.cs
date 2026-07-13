using SpaghettNET.Platform.Services;
using SkiaSharp;

namespace SpaghettNET.Platform;

internal static class Helpers
{
    public static SKEncodedImageFormat GetImageFormat(eImageFormat format) => format switch
    {
        eImageFormat.Png => SKEncodedImageFormat.Png,
        eImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    public static MemoryStream EncodeBitmap(SKBitmap bitmap, eImageFormat format)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(GetImageFormat(format), 100);

        var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;

        return ms;
    }
}
