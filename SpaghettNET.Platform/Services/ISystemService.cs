namespace SpaghettNET.Platform.Services;

/// <summary>
/// Host operating-system functions exposed to bot commands.
/// </summary>
public interface ISystemService
{
    MemoryStream GetDesktopScreenshot(eImageFormat format);
    string? GetClipboardData();
    List<Window> GetOpenWindows();
    bool MinimizeWindows(int processId);

    record Window(int ProcessId, string Title, string ProcessName);
}
