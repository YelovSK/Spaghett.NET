using Microsoft.Extensions.Logging;
using NetCord.Gateway;

namespace SpaghettNET.Bot.Services;

/// <summary>
/// Holds each channel's latest run open until someone else speaks or the author goes quiet, then
/// hands it to memory evaluation. Open runs live only in memory, so a restart drops them.
/// </summary>
public sealed class DiscordMemoryRunBuffer(
    DiscordMemoryService memoryService,
    ILogger<DiscordMemoryRunBuffer> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, OpenRun> _openRuns = [];

    /// <summary>Feeds every message seen in a guild channel, including the bot's own.</summary>
    public void Observe(Message message)
    {
        if (!memoryService.IsEnabled || message.GuildId is not { } guildId)
        {
            return;
        }

        lock (_gate)
        {
            var open = _openRuns.GetValueOrDefault(message.ChannelId);
            var next = MessageRun.Advance(open?.Run, message);
            if (open is not null && next == open.Run)
            {
                open.Timer.Change(MessageRun.MaxGap, Timeout.InfiniteTimeSpan);
                return;
            }

            if (open is not null)
            {
                Close(message.ChannelId, open);
            }

            if (next is not null)
            {
                _openRuns[message.ChannelId] = new OpenRun(guildId, next, message.ChannelId, this);
            }
        }
    }

    private void CloseIfQuiet(ulong channelId, OpenRun open)
    {
        lock (_gate)
        {
            if (_openRuns.GetValueOrDefault(channelId) == open)
            {
                Close(channelId, open);
            }
        }
    }

    private void Close(ulong channelId, OpenRun open)
    {
        _openRuns.Remove(channelId);
        open.Timer.Dispose();
        _ = EvaluateAsync(open);
    }

    private async Task EvaluateAsync(OpenRun open)
    {
        try
        {
            await memoryService.EvaluateRunAsync(open.GuildId, open.Run);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord memory evaluation failed.");
        }
    }

    private sealed class OpenRun
    {
        public OpenRun(ulong guildId, MessageRun run, ulong channelId, DiscordMemoryRunBuffer buffer)
        {
            GuildId = guildId;
            Run = run;
            Timer = new Timer(
                _ => buffer.CloseIfQuiet(channelId, this),
                null,
                MessageRun.MaxGap,
                Timeout.InfiniteTimeSpan);
        }

        public ulong GuildId { get; }
        public MessageRun Run { get; }
        public Timer Timer { get; }
    }
}
