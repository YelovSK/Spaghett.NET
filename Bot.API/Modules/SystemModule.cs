using Bot.API.Extensions;
using Bot.Application.Services;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Bot.API.Modules;

public class SystemModule(ISystemService systemService) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("screenshot", "Shows a screenshot of my desktop")]
    public InteractionMessageProperties Screenshot()
    {
        var ms = systemService.GetDesktopScreenshot(eImageFormat.Jpeg);
        
        return InteractionMessageProperties
            .Create()
            .WithAttachments([new AttachmentProperties("screenshot.jpg", ms)]);
    }
    
    [SlashCommand("clipboard", "Sends the last clipboard entry")]
    public InteractionMessageProperties Clipboard()
    {
        return systemService.GetClipboardData() ?? "Could not get clipboard data";
    }
    
    [SlashCommand("windows", "Sends the list of open windows")]
    public InteractionMessageProperties Windows()
    {
        return systemService
            .GetOpenWindows()
            .Select(x => $"{x.ProcessName} - {x.Title} [PID = {x.ProcessId}]")
            .Join(Environment.NewLine);
    }
    
    [SlashCommand("minimizewindow", "Minimizes all windows for a process ID")]
    public InteractionMessageProperties MinimizeWindows(int processId)
    {
        return systemService.MinimizeWindows(processId)
            ? "Minimized"
            : "Did not find any open windows with the process ID";
    }
}
