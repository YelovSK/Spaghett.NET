using System.Diagnostics.CodeAnalysis;
using System.Drawing.Imaging;
using Bot.Application.Services;

namespace Bot.Infrastructure;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
internal static class Helpers
{
    public static ImageFormat GetImageFormat(eImageFormat format) => format switch
        {
            eImageFormat.Png => ImageFormat.Png,
            eImageFormat.Jpeg => ImageFormat.Jpeg,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
}