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
    // Discord's largest page.
    private const int PageSize = 100;
    // Time goes into waiting on decision requests, so a few channels at once keeps the API busy
    // without hammering it.
    private const int MaxParallelChannels = 4;
    private const int EvaluationVersion = MemoryRetentionPolicy.Version;

    /// <summary>
    /// Walks every readable channel back to <paramref name="since"/>, continuing from where earlier
    /// passes stopped, and evaluates what it finds for memories.
    /// </summary>
    public async Task<DiscordMemoryGuildBackfillResult> BackfillGuildAsync(
        ulong guildId,
        DateTimeOffset since,
        IProgress<DiscordMemoryBackfillProgress>? progress = null,
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

        var finishedChannels = 0;
        var fetched = 0;
        var stored = 0;
        void Report() => progress?.Report(new DiscordMemoryBackfillProgress(
            Volatile.Read(ref finishedChannels),
            channels.Length,
            Volatile.Read(ref fetched),
            Volatile.Read(ref stored)));

        var results = new DiscordMemoryBackfillResult[channels.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, channels.Length),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelChannels, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                results[index] = await TryBackfillChannelAsync(
                    guildId,
                    channels[index].Id,
                    since,
                    page =>
                    {
                        Interlocked.Add(ref fetched, page.Fetched);
                        Interlocked.Add(ref stored, page.Stored);
                        Report();
                    },
                    token);
                Interlocked.Increment(ref finishedChannels);
                Report();
            });

        return new DiscordMemoryGuildBackfillResult(
            channels.Length,
            results.Count(result => result.IsComplete),
            results.Count(result => result.Failed),
            results.Sum(result => result.Fetched),
            results.Sum(result => result.Evaluated),
            results.Sum(result => result.Stored),
            results.Sum(result => result.AlreadyEvaluated),
            results.Sum(result => result.Ignored));
    }

    private async Task<DiscordMemoryBackfillResult> TryBackfillChannelAsync(
        ulong guildId,
        ulong channelId,
        DateTimeOffset since,
        Action<DiscordMemoryBackfillResult> onPage,
        CancellationToken cancellationToken)
    {
        var total = new DiscordMemoryBackfillResult();
        try
        {
            while (true)
            {
                var (page, hasMore) = await BackfillPageAsync(guildId, channelId, since, cancellationToken);
                total = total.Add(page);
                onPage(page);
                if (!hasMore)
                {
                    return total;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Discord memory backfill failed for channel. GuildId: {GuildId}, ChannelId: {ChannelId}",
                guildId,
                channelId);
            return total with { Failed = true };
        }
    }

    /// <summary>Evaluates the next page of older messages and moves the channel's cursor past it.</summary>
    /// <returns>The page's result, and whether older messages remain to walk before <paramref name="since"/>.</returns>
    private async Task<(DiscordMemoryBackfillResult Page, bool HasMore)> BackfillPageAsync(
        ulong guildId,
        ulong channelId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        await using var cursorContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var cursor = await cursorContext.DiscordMemoryBackfillCursors
            .SingleOrDefaultAsync(item => item.Id == channelId, cancellationToken);
        var cursorNeedsReevaluation = cursor is not null &&
                                      cursor.EvaluationVersion < EvaluationVersion;
        if (cursor?.IsComplete == true && !cursorNeedsReevaluation)
        {
            return (new DiscordMemoryBackfillResult(IsComplete: true), false);
        }

        var pagination = new PaginationProperties<ulong>
        {
            From = cursorNeedsReevaluation ? null : cursor?.BeforeMessageId,
            Direction = PaginationDirection.Before,
            BatchSize = PageSize,
        };
        var fetchedMessages = new List<RestMessage>();
        var exhausted = true;
        var reachedSince = false;
        await foreach (var message in gatewayClient.Rest
                           .GetMessagesAsync(channelId, pagination)
                           .WithCancellation(cancellationToken))
        {
            if (message.CreatedAt < since)
            {
                exhausted = false;
                reachedSince = true;
                break;
            }

            fetchedMessages.Add(message);
            if (fetchedMessages.Count >= PageSize)
            {
                exhausted = false;
                break;
            }
        }

        // Discord pages newest first, but runs are read oldest first.
        var traversed = Enumerable.Reverse(fetchedMessages).ToList();
        var runs = MessageRun.Group(traversed);
        // The oldest run may continue onto the next page, so leave it for that page to see whole.
        if (!exhausted && !reachedSince && runs.Count > 1)
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

        var page = new DiscordMemoryBackfillResult(
            fetched,
            evaluated,
            stored,
            alreadyEvaluated,
            ignored,
            exhausted,
            failed);
        // A page that moved nothing forward would only be fetched again.
        var hasMore = !exhausted && !reachedSince && !failed && oldestTraversedMessageId.HasValue;
        return (page, hasMore);
    }
}

public sealed record DiscordMemoryBackfillResult(
    int Fetched = 0,
    int Evaluated = 0,
    int Stored = 0,
    int AlreadyEvaluated = 0,
    int Ignored = 0,
    bool IsComplete = false,
    bool Failed = false)
{
    /// <summary>Adds a later page's counts; its flags describe where the channel now stands.</summary>
    public DiscordMemoryBackfillResult Add(DiscordMemoryBackfillResult page) => new(
        Fetched + page.Fetched,
        Evaluated + page.Evaluated,
        Stored + page.Stored,
        AlreadyEvaluated + page.AlreadyEvaluated,
        Ignored + page.Ignored,
        page.IsComplete,
        page.Failed);
}

public sealed record DiscordMemoryBackfillProgress(
    int FinishedChannels,
    int Channels,
    int Fetched,
    int Stored);

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
