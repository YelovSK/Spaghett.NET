namespace Bot.Application.Services;

/// <summary>
/// Random OS functions. Realistically mostly just WinAPI.
/// </summary>
public interface ISystemService
{
    MemoryStream GetDesktopScreenshot(eImageFormat format);
    string? GetClipboardData();
    List<Window> GetOpenWindows();
    bool MinimizeWindows(int processId);

    record Window(int ProcessId, string Title, string ProcessName);
}
