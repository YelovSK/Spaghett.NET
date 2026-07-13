using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using SpaghettNET.Platform.Interop;
using SkiaSharp;

namespace SpaghettNET.Platform.Services;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
public class WindowsService : ISystemService
{
    private const uint CF_UNICODETEXT = 13; 
    
    public MemoryStream GetDesktopScreenshot(eImageFormat format)
    {
        var width = User32.GetSystemMetrics(0); // SM_CXSCREEN
        var height = User32.GetSystemMetrics(1); // SM_CYSCREEN

        var desktopWindow = User32.GetDesktopWindow();
        var hdcScreen = User32.GetWindowDC(desktopWindow);
        var hdcMemory = Gdi32.CreateCompatibleDC(hdcScreen);
        var hBitmap = Gdi32.CreateCompatibleBitmap(hdcScreen, width, height);
        var previousObject = Gdi32.SelectObject(hdcMemory, hBitmap);

        try
        {
            Gdi32.BitBlt(hdcMemory, 0, 0, width, height, hdcScreen, 0, 0,
                Gdi32.SRCCOPY | Gdi32.CAPTUREBLT);

            var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var bmp = new SKBitmap(imageInfo);

            var bitmapInfo = new Gdi32.BitmapInfo
            {
                Header = new Gdi32.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<Gdi32.BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = Gdi32.BI_RGB,
                    SizeImage = (uint)(bmp.RowBytes * height),
                },
            };

            Gdi32.GetDIBits(hdcMemory, hBitmap, 0, (uint)height, bmp.GetPixels(),
                ref bitmapInfo, Gdi32.DIB_RGB_COLORS);

            return Helpers.EncodeBitmap(bmp, format);
        }
        finally
        {
            Gdi32.SelectObject(hdcMemory, previousObject);
            Gdi32.DeleteObject(hBitmap);
            Gdi32.DeleteDC(hdcMemory);
            User32.ReleaseDC(desktopWindow, hdcScreen);
        }
    }
    
    public string? GetClipboardData()
    {
        if (!User32.IsClipboardFormatAvailable(CF_UNICODETEXT))
        {
            return null;
        }

        if (!User32.OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            var handle = User32.GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var pointer = Kernel32.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    public List<ISystemService.Window> GetOpenWindows()
    {
        var windows = new List<ISystemService.Window>();

        User32.EnumWindows((hWnd, _) =>
        {
            if (!User32.IsWindowVisible(hWnd))
            {
                return true;
            }
            
            var length = User32.GetWindowTextLength(hWnd);
            if (length == 0)
            {
                return true;
            }

            var builder = new StringBuilder(length + 1);
            User32.GetWindowText(hWnd, builder, builder.Capacity);

            User32.GetWindowThreadProcessId(hWnd, out var pid);
            var processName = Process.GetProcessById((int)pid).ProcessName;

            windows.Add(new ISystemService.Window((int)pid, builder.ToString(), processName));

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public bool MinimizeWindows(int processId)
    {
        var minimized = false;
        
        User32.EnumWindows((hWnd, lParam) =>
        {
            if (!User32.IsWindowVisible(hWnd))
            {
                return true;
            }

            User32.GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid == processId)
            {
                // Technically the window could've already been minimized, would have to check the state.
                User32.ShowWindow(hWnd, User32.SW_MINIMIZE);
                minimized = true;
            }

            return true;
        }, IntPtr.Zero);

        return minimized;
    }
}
