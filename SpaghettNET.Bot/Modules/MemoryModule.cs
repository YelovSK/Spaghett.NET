using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using SpaghettNET.Bot.Services;

namespace SpaghettNET.Bot.Modules;

[SlashCommand(
    "memory",
    "Manages conversational memories",
    DefaultGuildUserPermissions = Permissions.ManageGuild,
    Contexts = [InteractionContextType.Guild])]
public class MemoryModule(
    DiscordMemoryService memoryService,
    ILogger<MemoryModule> logger) : ApplicationCommandModule<ApplicationCommandContext>
{
    private const int DefaultMessageCount = 50;
    private const int MaximumMessageCount = 100;
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
            Description = "Messages to walk backward through per channel (1-100)",
            MinValue = 1,
            MaxValue = MaximumMessageCount)]
        int messages = DefaultMessageCount)
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
                var result = await memoryService.BackfillGuildAsync(
                    guildId,
                    Math.Clamp(messages, 1, MaximumMessageCount));
                response = FormatResult(result);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discord memory backfill failed.");
            response = "Memory backfill failed. No cursor was advanced past the failed message, so it is safe to retry.";
        }

        await Context.Interaction.ModifyResponseAsync(message => message.Content = response);
    }

    private static string FormatResult(DiscordMemoryGuildBackfillResult result)
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
            ? "Backfill is complete for every readable channel."
            : "Run the command again to continue farther back in the remaining channels.";
        if (result.FailedChannels > 0)
        {
            status += $" {result.FailedChannels} channel(s) failed and will be retried.";
        }

        return $"Visited {result.Channels} channels ({result.CompleteChannels} complete). " +
               $"Walked {result.Fetched} messages: evaluated {result.Evaluated}, " +
               $"stored {result.Stored}, already evaluated {result.AlreadyEvaluated}, " +
               $"ignored {result.Ignored}. {status}";
    }

}
