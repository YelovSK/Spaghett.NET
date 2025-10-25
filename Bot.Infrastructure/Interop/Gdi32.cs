using System.Drawing;
using System.Runtime.InteropServices;

namespace Bot.Infrastructure.Interop;

internal class Gdi32
{
    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest,
                              int nWidth, int nHeight,
                              IntPtr hdcSrc, int nXSrc, int nYSrc, CopyPixelOperation dwRop);
}