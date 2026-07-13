using SkiaSharp;

namespace SpaghettNET.Platform.Services;

public class ImageService
{
    public bool IsColorValid(int r, int g, int b) =>
        r is >= 0 and <= 255 &&
        g is >= 0 and <= 255 &&
        b is >= 0 and <= 255;

    public MemoryStream GetSolidColorImage(int r, int g, int b, int width, int height, eImageFormat format)
    {
        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bmp = new SKBitmap(imageInfo);
        using var canvas = new SKCanvas(bmp);

        canvas.Clear(new SKColor((byte)r, (byte)g, (byte)b));

        return Helpers.EncodeBitmap(bmp, format);
    }

    public MemoryStream GenerateMandelbrot(double zoom, double centerX, double centerY, uint iterations, int width,
        int height, eImageFormat format)
    {
        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bmp = new SKBitmap(imageInfo);
        var stride = bmp.RowBytes;
        var pixels = bmp.GetPixels();

        var viewWidth = 3.5 / zoom;
        var viewHeight = viewWidth * height / width;
        var scaleX = viewWidth / width;
        var scaleY = viewHeight / height;

        unsafe
        {
            var row0 = (byte*)pixels;

            Parallel.For(0, height, y =>
            {
                var row = row0 + y * stride;
                var cy = centerY + (y - height / 2.0) * scaleY;

                for (var x = 0; x < width; x++)
                {
                    var cx = centerX + (x - width / 2.0) * scaleX;

                    double zr = 0, zi = 0;
                    var i = 0;
                    double zr2 = 0, zi2 = 0;

                    for (; i < iterations && (zr2 + zi2) <= 4.0; i++)
                    {
                        zi = 2 * zr * zi + cy;
                        zr = zr2 - zi2 + cx;
                        zr2 = zr * zr;
                        zi2 = zi * zi;
                    }

                    var isInside = i == iterations;
                    var value = isInside
                        ? (byte)255
                        : (byte)0;

                    var offset = x * 4;
                    row[offset + 0] = value;
                    row[offset + 1] = value;
                    row[offset + 2] = value;
                    row[offset + 3] = 255;
                }
            });
        }

        return Helpers.EncodeBitmap(bmp, format);
    }
}
