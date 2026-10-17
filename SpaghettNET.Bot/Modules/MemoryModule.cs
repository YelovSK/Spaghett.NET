using System.Text;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using SpaghettNET.Bot.Services.Memory;

namespace SpaghettNET.Bot.Modules;

[SlashCommand(
    "memory",
    "Manages conversational memories",
    DefaultGuildUserPermissions = Permissions.ManageGuild,
    Contexts = [InteractionContextType.Guild])]
public class MemoryModule(
    DiscordMemoryService memoryService,
    DiscordMemoryBackfill memoryBackfill,
    ILogger<MemoryModule> logger) : ApplicationCommandModule<ApplicationCommandContext>
{
    private const int DefaultDays = 30;
    private const int MaximumDays = 3650;
    // Discord rate-limits edits to the same reply, and nobody reads progress faster than this anyway.
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(3);
    private const int DefaultListCount = 20;
    private const int MaximumListCount = 50;
    private const int MaximumMemoryLength = 90;
    // Discord caps a message at 2000 characters; the rest is the header and newlines.
    private const int ListCharacterBudget = 1800;

    [SubSlashCommand("list", "Shows the most recent memories")]
    public Task List(
        [SlashCommandParameter(
            Description = "How many to try to show (1-50)",
            MinValue = 1,
            MaxValue = MaximumListCount)]
        int count = DefaultListCount) =>
        RespondAsync(async guildId =>
        {
            var memories = await memoryService.GetLatestMemoriesAsync(guildId, count);
            if (memories.Count == 0)
            {
                return "Nothing has been remembered yet.";
            }

            var total = await memoryService.CountMemoriesAsync(guildId);
            var lines = new List<string>();
            var characters = 0;
            foreach (var memory in memories)
            {
                var line = $"<t:{memory.CreatedAt.ToUnixTimeSeconds()}:R> **{memory.AuthorName}** " +
                           $"in <#{memory.ChannelId}>: {Truncate(memory.Content, MaximumMemoryLength)}";
                if (characters + line.Length > ListCharacterBudget)
                {
                    break;
                }

                lines.Add(line);
                characters += line.Length + Environment.NewLine.Length;
            }

            var shown = $"Showing {lines.Count} of {total} remembered messages";
            if (lines.Count < memories.Count)
            {
                shown += $" ({memories.Count - lines.Count} more would not fit)";
            }

            return $"{shown}:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
        });

    [SubSlashCommand("export", "Sends every memory as a text file")]
    public async Task Export()
    {
        await Context.Interaction.SendResponseAsync(
            InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        string content;
        string? file = null;
        try
        {
            if (Context.Interaction.GuildId is not { } guildId)
            {
                content = "This command only works in a server channel.";
            }
            else
            {
                var memories = await memoryService.GetLatestMemoriesAsync(guildId);
                content = memories.Count == 0
                    ? "Nothing has been remembered yet."
                    : $"All {memories.Count} memories, newest first. Excluded channels are left out.";
                file = memories.Count == 0
                    ? null
                    : string.Join("\n\n", memories.Select(memory =>
                        $"[{memory.CreatedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC] {memory.AuthorName} in {memory.ChannelName}\n" +
                        memory.Content.ReplaceLineEndings("\n")));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Memory export failed.");
            content = "That failed.";
        }

        await Context.Interaction.ModifyResponseAsync(message =>
        {
            message.Content = content;
            if (file is not null)
            {
                message.Attachments = [new AttachmentProperties("memories.txt", new MemoryStream(Encoding.UTF8.GetBytes(file)))];
            }
        });
    }

    private static string Truncate(string value, int length)
    {
        var singleLine = value.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= length ? singleLine : singleLine[..length] + "…";
    }

    [SubSlashCommand("exclude", "Stops remembering and recalling messages from a channel")]
    public Task Exclude(
        [SlashCommandParameter(Description = "The channel to leave out of memory")]
        INamedChannel channel) =>
        RespondAsync(async guildId =>
            await memoryService.ExcludeChannelAsync(guildId, channel.Id, channel.Name)
                ? $"Memory is off for {ChannelMention(channel)}. Threads under it are covered too. " +
                  "Anything already remembered stays stored but will not be recalled, and comes back if you include it again."
                : $"{ChannelMention(channel)} was already excluded.");

    [SubSlashCommand("include", "Resumes remembering a channel that was excluded")]
    public Task Include(
        [SlashCommandParameter(Description = "The channel to bring back into memory")]
        INamedChannel channel) =>
        RespondAsync(async _ =>
            await memoryService.IncludeChannelAsync(channel.Id)
                ? $"Memory is on again for {ChannelMention(channel)}, including anything it remembered before."
                : $"{ChannelMention(channel)} was not excluded.");

    [SubSlashCommand("excluded", "Lists the channels left out of memory")]
    public Task Excluded() =>
        RespondAsync(async guildId =>
        {
            var excluded = await memoryService.GetExcludedChannelsAsync(guildId);
            return excluded.Count == 0
                ? "No channels are excluded from memory."
                : "Excluded from memory:\n" + string.Join(
                    Environment.NewLine,
                    excluded.Select(channel => $"- <#{channel.ChannelId}> ({channel.ChannelName})"));
        });

    private static string ChannelMention(INamedChannel channel) =>
        channel is Channel entity ? $"<#{entity.Id}>" : $"#{channel.Name}";

    private async Task RespondAsync(Func<ulong, Task<string>> handler)
    {
        await Context.Interaction.SendResponseAsync(
            InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        string response;
        try
        {
            response = Context.Interaction.GuildId is { } guildId
                ? await handler(guildId)
                : "This command only works in a server channel.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Memory command failed.");
            response = "That failed. Nothing was changed.";
        }

        await Context.Interaction.ModifyResponseAsync(message => message.Content = response);
    }

    [SubSlashCommand("backfill", "Examines older messages across this server for memories")]
    public async Task Backfill(
        [SlashCommandParameter(
            Description = "How many days back to go, continuing from earlier backfills",
            MinValue = 1,
            MaxValue = MaximumDays)]
        int days = DefaultDays)
    {
        await Context.Interaction.SendResponseAsync(
            InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        string response;
        try
        {
            if (Context.Interaction.GuildId is not { } guildId)
            {
                response = "Memory backfill can only run in a server channel.";
            }
            else
            {
                var progress = new ThrottledProgress<DiscordMemoryBackfillProgress>(
                    ProgressInterval,
                    async update =>
                    {
                        try
                        {
                            await Context.Interaction.ModifyResponseAsync(message => message.Content =
                                $"Backfilling the last {days} days… {update.FinishedChannels}/{update.Channels} channels done, " +
                                $"{update.Fetched} messages walked, {update.Stored} stored.");
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Could not show memory backfill progress.");
                        }
                    });
                var result = await memoryBackfill.BackfillGuildAsync(
                    guildId,
                    DateTimeOffset.UtcNow.AddDays(-days),
                    progress);
                await progress.WhenIdle();
                response = FormatResult(result, days);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discord memory backfill failed.");
            response = "Memory backfill failed. No cursor was advanced past the failed message, so it is safe to retry.";
        }

        await Context.Interaction.ModifyResponseAsync(message => message.Content = response);
    }

    private static string FormatResult(DiscordMemoryGuildBackfillResult result, int days)
    {
        if (result.IsDisabled)
        {
            return "Memory is disabled. Configure OpenAI:MemoryModel first.";
        }

        if (result.Channels == 0)
        {
            return "The bot cannot read message history in any text channel in this server.";
        }

        var status = result.CompleteChannels == result.Channels
            ? "Every readable channel is backfilled to its first message."
            : $"Channels are covered back {days} days; use more days to go farther.";
        if (result.FailedChannels > 0)
        {
            status += $" {result.FailedChannels} channel(s) failed; run it again to retry them.";
        }

        return $"Visited {result.Channels} channels ({result.CompleteChannels} complete). " +
               $"Walked {result.Fetched} messages: evaluated {result.Evaluated}, " +
               $"stored {result.Stored}, already evaluated {result.AlreadyEvaluated}, " +
               $"ignored {result.Ignored}. {status}";
    }

    /// <summary>Passes on the latest value at most once per interval, and never while a report is still running.</summary>
    private sealed class ThrottledProgress<T>(TimeSpan interval, Func<T, Task> report) : IProgress<T>
    {
        private readonly Lock _gate = new();
        private Task _running = Task.CompletedTask;
        private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

        public void Report(T value)
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (!_running.IsCompleted || now - _lastReport < interval)
                {
                    return;
                }

                _lastReport = now;
                _running = report(value);
            }
        }

        public Task WhenIdle()
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }
}
