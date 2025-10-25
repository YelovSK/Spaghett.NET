using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using Bot.Application.Services;

namespace Bot.Infrastructure.Services;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
public class ImageService : IImageService
{
    public MemoryStream GetSolidColorImage(int r, int g, int b, int width, int height, eImageFormat format)
    {
        using var bmp = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bmp);
        graphics.Clear(Color.FromArgb(r, g, b));

        var ms = new MemoryStream();
        bmp.Save(ms, Helpers.GetImageFormat(format));
        ms.Position = 0;

        return ms;
    }


    public MemoryStream GenerateMandelbrot(double zoom, double centerX, double centerY, uint iterations, int width,
        int height, eImageFormat format)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);

        var stride = data.Stride;
        var scan0 = data.Scan0;

        var viewWidth = 3.5 / zoom;
        var viewHeight = viewWidth * height / width;
        var scaleX = viewWidth / width;
        var scaleY = viewHeight / height;

        unsafe
        {
            var row0 = (byte*)scan0;

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

        bmp.UnlockBits(data);

        var ms = new MemoryStream();
        bmp.Save(ms, Helpers.GetImageFormat(format));
        ms.Position = 0;
        bmp.Dispose();
        
        return ms;
    }
}