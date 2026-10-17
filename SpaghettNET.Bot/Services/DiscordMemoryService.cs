using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SpaghettNET.Persistence;
using DiscordMemory = SpaghettNET.Persistence.Models.DiscordMemory;
using DiscordMemoryExcludedChannel = SpaghettNET.Persistence.Models.DiscordMemoryExcludedChannel;

namespace SpaghettNET.Bot.Services;

public sealed class DiscordMemoryService(
    IDbContextFactory<BotContext> dbContextFactory,
    OpenAIOptions options,
    ILogger<DiscordMemoryService> logger)
{
    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(options.ApiKey) &&
        !string.IsNullOrWhiteSpace(options.MemoryModel) &&
        Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.EndsWith("openrouter.ai", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops remembering and recalling a channel. Existing memories are kept but hidden.</summary>
    public async Task<bool> ExcludeChannelAsync(
        ulong guildId,
        ulong channelId,
        string channelName,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await dbContext.DiscordMemoryExcludedChannels
                .AnyAsync(channel => channel.Id == channelId, cancellationToken))
        {
            return false;
        }

        dbContext.DiscordMemoryExcludedChannels.Add(new DiscordMemoryExcludedChannel
        {
            Id = channelId,
            GuildId = guildId,
            ChannelName = channelName,
            ExcludedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Excluded channel from memory. GuildId: {GuildId}, ChannelId: {ChannelId}",
            guildId,
            channelId);
        return true;
    }

    /// <summary>Resumes remembering a channel, restoring any memories it already had.</summary>
    public async Task<bool> IncludeChannelAsync(
        ulong channelId,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var removed = await dbContext.DiscordMemoryExcludedChannels
            .Where(channel => channel.Id == channelId)
            .ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            logger.LogInformation("Included channel in memory again. ChannelId: {ChannelId}", channelId);
        }

        return removed > 0;
    }

    /// <summary>The newest memories eligible for recall, newest first.</summary>
    public async Task<IReadOnlyList<DiscordMemory>> GetLatestMemoriesAsync(
        ulong guildId,
        int count,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var excludedChannelIds = dbContext.DiscordMemoryExcludedChannels
            .Where(channel => channel.GuildId == guildId)
            .Select(channel => channel.Id);
        return await dbContext.DiscordMemories
            .AsNoTracking()
            .Where(memory => memory.GuildId == guildId &&
                             !excludedChannelIds.Contains(memory.ChannelId))
            // Snowflakes are ordered by creation time, and SQLite cannot sort a DateTimeOffset.
            .OrderByDescending(memory => memory.LastMessageId)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountMemoriesAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.DiscordMemories.CountAsync(
            memory => memory.GuildId == guildId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<(ulong ChannelId, string ChannelName)>> GetExcludedChannelsAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.DiscordMemoryExcludedChannels
            .AsNoTracking()
            .Where(channel => channel.GuildId == guildId)
            .OrderBy(channel => channel.ChannelName)
            .Select(channel => new ValueTuple<ulong, string>(channel.Id, channel.ChannelName))
            .ToListAsync(cancellationToken);
    }
}
