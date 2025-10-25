namespace Bot.Application.Services;

public interface IImageService
{
    MemoryStream GetSolidColorImage(int r, int g, int b, int width, int height, eImageFormat format);
    MemoryStream GenerateMandelbrot(double zoom, double centerX, double centerY, uint iterations, int width, int height, eImageFormat format);
    
    public bool IsColorValid(int r, int g, int b) =>
        r is >= 0 and <= 255 &&
        g is >= 0 and <= 255 &&
        b is >= 0 and <= 255;
}

public enum eImageFormat
{
    Png,
    Jpeg,
}