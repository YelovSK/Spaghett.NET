using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using SpaghettNET.Persistence;
using DiscordMemoryBackfillCursor = SpaghettNET.Persistence.Models.DiscordMemoryBackfillCursor;

namespace SpaghettNET.Bot.Services.Memory;

public sealed class DiscordMemoryBackfill(
    IDbContextFactory<BotContext> dbContextFactory,
    GatewayClient gatewayClient,
    DiscordMemoryService memoryService,
    DiscordMemoryEvaluator evaluator,
    ILogger<DiscordMemoryBackfill> logger)
{
    private const int BackfillBatchSize = 20;
    private const int EvaluationVersion = MemoryRetentionPolicy.Version;

    public async Task<DiscordMemoryGuildBackfillResult> BackfillGuildAsync(
        ulong guildId,
        int maxMessagesPerChannel,
        CancellationToken cancellationToken = default)
    {
        if (!memoryService.IsEnabled)
        {
            return new DiscordMemoryGuildBackfillResult(IsDisabled: true);
        }

        var guildTask = gatewayClient.Rest.GetGuildAsync(
            guildId,
            false,
            cancellationToken: cancellationToken);
        var channelsTask = gatewayClient.Rest.GetGuildChannelsAsync(
            guildId,
            cancellationToken: cancellationToken);
        var currentUserTask = gatewayClient.Rest.GetGuildUserAsync(
            guildId,
            gatewayClient.Id,
            cancellationToken: cancellationToken);
        await Task.WhenAll(guildTask, channelsTask, currentUserTask);

        var guild = await guildTask;
        var currentUser = await currentUserTask;
        await using var excludedContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var excludedChannelIds = await excludedContext.DiscordMemoryExcludedChannels
            .Where(channel => channel.GuildId == guildId)
            .Select(channel => channel.Id)
            .ToHashSetAsync(cancellationToken);
        var channels = (await channelsTask)
            .OfType<TextGuildChannel>()
            .Where(channel => !excludedChannelIds.Contains(channel.Id))
            .Where(channel =>
            {
                var permissions = currentUser.GetChannelPermissions(guild, channel);
                return permissions.HasFlag(Permissions.ViewChannel) &&
                       permissions.HasFlag(Permissions.ReadMessageHistory);
            })
            .OrderBy(channel => channel.Position)
            .ThenBy(channel => channel.Id)
            .ToArray();

        var fetched = 0;
        var evaluated = 0;
        var stored = 0;
        var alreadyEvaluated = 0;
        var ignored = 0;
        var completeChannels = 0;
        var failedChannels = 0;

        foreach (var channel in channels)
        {
            try
            {
                var result = await BackfillChannelAsync(
                    guildId,
                    channel.Id,
                    maxMessagesPerChannel,
                    cancellationToken);
                fetched += result.Fetched;
                evaluated += result.Evaluated;
                stored += result.Stored;
                alreadyEvaluated += result.AlreadyEvaluated;
                ignored += result.Ignored;
                completeChannels += result.IsComplete ? 1 : 0;
                failedChannels += result.Failed ? 1 : 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedChannels++;
                logger.LogWarning(
                    ex,
                    "Discord memory backfill failed for channel. GuildId: {GuildId}, ChannelId: {ChannelId}",
                    guildId,
                    channel.Id);
            }
        }

        return new DiscordMemoryGuildBackfillResult(
            channels.Length,
            completeChannels,
            failedChannels,
            fetched,
            evaluated,
            stored,
            alreadyEvaluated,
            ignored);
    }

    private async Task<DiscordMemoryBackfillResult> BackfillChannelAsync(
        ulong guildId,
        ulong channelId,
        int maxMessages,
        CancellationToken cancellationToken = default)
    {
        if (!memoryService.IsEnabled)
        {
            return new DiscordMemoryBackfillResult(IsDisabled: true);
        }

        await using var cursorContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var cursor = await cursorContext.DiscordMemoryBackfillCursors
            .SingleOrDefaultAsync(item => item.Id == channelId, cancellationToken);
        var cursorNeedsReevaluation = cursor is not null &&
                                      cursor.EvaluationVersion < EvaluationVersion;
        if (cursor?.IsComplete == true && !cursorNeedsReevaluation)
        {
            return new DiscordMemoryBackfillResult(IsComplete: true);
        }

        var pagination = new PaginationProperties<ulong>
        {
            From = cursorNeedsReevaluation ? null : cursor?.BeforeMessageId,
            Direction = PaginationDirection.Before,
            BatchSize = Math.Min(maxMessages, 100),
        };
        var fetchedMessages = new List<RestMessage>();
        var exhausted = true;
        await foreach (var message in gatewayClient.Rest
                           .GetMessagesAsync(channelId, pagination)
                           .WithCancellation(cancellationToken))
        {
            fetchedMessages.Add(message);
            if (fetchedMessages.Count >= maxMessages)
            {
                exhausted = false;
                break;
            }
        }

        // Discord pages newest first, but runs are read oldest first.
        var traversed = Enumerable.Reverse(fetchedMessages).ToList();
        var runs = MessageRun.Group(traversed);
        // The oldest run may continue past this page, so leave it for the next pass to see whole.
        if (!exhausted && runs.Count > 1)
        {
            var heldBack = runs[0];
            runs.RemoveAt(0);
            traversed.RemoveAll(message => message.Id <= heldBack.Last.Id);
        }

        // A run that may still be growing belongs to the live buffer.
        var liveCutoff = DateTimeOffset.UtcNow - MessageRun.MaxGap;
        runs.RemoveAll(run => run.Last.CreatedAt > liveCutoff);

        var fetched = 0;
        var evaluated = 0;
        var stored = 0;
        var alreadyEvaluated = 0;
        var ignored = 0;
        var failed = false;
        ulong? oldestTraversedMessageId = null;

        foreach (var batch in Enumerable.Reverse(runs).Chunk(BackfillBatchSize))
        {
            var result = await evaluator.EvaluateAsync(
                batch,
                guildId,
                channelId,
                $"discord-memory-backfill:{guildId}:{channelId}",
                cancellationToken);
            if (result is null)
            {
                failed = true;
                exhausted = false;
                break;
            }

            var walked = CountWalked(batch[^1].First.Id);
            fetched += walked;
            evaluated += result.Evaluated;
            stored += result.Stored;
            alreadyEvaluated += result.AlreadyEvaluated;
            ignored += result.Ignored + walked - batch.Sum(run => run.Messages.Count);
            oldestTraversedMessageId = batch[^1].First.Id;
        }

        if (!failed && traversed.Count > 0)
        {
            var walked = CountWalked(traversed[0].Id);
            fetched += walked;
            ignored += walked;
            oldestTraversedMessageId = traversed[0].Id;
        }

        int CountWalked(ulong oldestId) => traversed.Count(message =>
            message.Id >= oldestId && message.Id < (oldestTraversedMessageId ?? ulong.MaxValue));

        if (oldestTraversedMessageId.HasValue || exhausted)
        {
            cursor ??= new DiscordMemoryBackfillCursor
            {
                Id = channelId,
                GuildId = guildId,
                BeforeMessageId = oldestTraversedMessageId ?? 0,
                IsComplete = exhausted,
                EvaluationVersion = EvaluationVersion,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            if (cursorContext.Entry(cursor).State == EntityState.Detached)
            {
                cursorContext.DiscordMemoryBackfillCursors.Add(cursor);
            }

            if (oldestTraversedMessageId is { } messageId)
            {
                cursor.BeforeMessageId = messageId;
            }

            cursor.IsComplete = exhausted;
            cursor.EvaluationVersion = EvaluationVersion;
            cursor.UpdatedAt = DateTimeOffset.UtcNow;
            await cursorContext.SaveChangesAsync(cancellationToken);
        }

        return new DiscordMemoryBackfillResult(
            fetched,
            evaluated,
            stored,
            alreadyEvaluated,
            ignored,
            exhausted,
            failed);
    }
}

public sealed record DiscordMemoryBackfillResult(
    int Fetched = 0,
    int Evaluated = 0,
    int Stored = 0,
    int AlreadyEvaluated = 0,
    int Ignored = 0,
    bool IsComplete = false,
    bool Failed = false,
    bool IsDisabled = false);

public sealed record DiscordMemoryGuildBackfillResult(
    int Channels = 0,
    int CompleteChannels = 0,
    int FailedChannels = 0,
    int Fetched = 0,
    int Evaluated = 0,
    int Stored = 0,
    int AlreadyEvaluated = 0,
    int Ignored = 0,
    bool IsDisabled = false);
