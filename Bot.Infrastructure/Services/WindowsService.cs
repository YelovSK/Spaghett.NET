using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Bot.Application.Services;
using Bot.Infrastructure.Interop;

namespace Bot.Infrastructure.Services;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
public class WindowsService : ISystemService
{
    private const uint CF_UNICODETEXT = 13; 
    
    public MemoryStream GetDesktopScreenshot(eImageFormat format)
    {
        var width = User32.GetSystemMetrics(0); // SM_CXSCREEN
        var height = User32.GetSystemMetrics(1); // SM_CYSCREEN

        using var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);

        var hdcBitmap = g.GetHdc();
        var hdcScreen = User32.GetWindowDC(User32.GetDesktopWindow());

        Gdi32.BitBlt(hdcBitmap, 0, 0, width, height, hdcScreen, 0, 0,
            CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);

        g.ReleaseHdc(hdcBitmap);
        User32.ReleaseDC(User32.GetDesktopWindow(), hdcScreen);
        
        var ms = new MemoryStream();
        bmp.Save(ms, Helpers.GetImageFormat(format));
        ms.Position = 0;

        return ms;
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