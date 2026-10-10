using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using SpaghettNET.Persistence;
using SpaghettNET.Persistence.Models;

namespace SpaghettNET.Bot.Services;

public sealed class MessageRuleService(
    IDbContextFactory<BotContext> dbContextFactory,
    ILogger<MessageRuleService> logger)
{
    // Patterns come from the LLM, so a catastrophic regex must not stall message handling.
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IReadOnlyList<ActiveRule>? _rules;

    public static Regex CreatePattern(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout);

    public async Task<IReadOnlyList<MessageRule>> GetRulesAsync(ulong guildId) =>
        (await GetActiveRulesAsync())
            .Where(rule => rule.Rule.GuildId == guildId && !IsExpired(rule.Rule))
            .Select(rule => rule.Rule)
            .ToArray();

    public async Task AddAsync(MessageRule rule)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        dbContext.MessageRules.Add(rule);
        await dbContext.SaveChangesAsync();
        await ReloadAsync();
    }

    public async Task<bool> DeleteAsync(ulong guildId, int ruleId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var deleted = await dbContext.MessageRules
            .Where(rule => rule.GuildId == guildId && rule.Id == ruleId)
            .ExecuteDeleteAsync();
        await ReloadAsync();
        return deleted > 0;
    }

    /// <summary>Rules whose conditions match, except <see cref="MessageRule.Chance"/>, which the caller rolls.</summary>
    public async Task<IReadOnlyList<MessageRule>> GetMatchingRulesAsync(Message message)
    {
        if (message.GuildId is not { } guildId)
        {
            return [];
        }

        return (await GetActiveRulesAsync())
            .Where(rule => Matches(rule, guildId, message))
            .Select(rule => rule.Rule)
            .ToArray();
    }

    private bool Matches(ActiveRule activeRule, ulong guildId, Message message)
    {
        var rule = activeRule.Rule;
        if (rule.GuildId != guildId ||
            IsExpired(rule) ||
            rule.UserId is { } userId && userId != message.Author.Id ||
            rule.ChannelId is { } channelId && channelId != message.ChannelId)
        {
            return false;
        }

        try
        {
            if (activeRule.Pattern?.IsMatch(message.Content) == false)
            {
                return false;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            logger.LogWarning("Message rule {RuleId} pattern timed out.", rule.Id);
            return false;
        }

        return true;
    }

    private static bool IsExpired(MessageRule rule) => rule.ExpiresAt <= DateTimeOffset.UtcNow;

    private async Task<IReadOnlyList<ActiveRule>> GetActiveRulesAsync() => _rules ?? await ReloadAsync();

    private async Task<IReadOnlyList<ActiveRule>> ReloadAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync();
            var rules = await dbContext.MessageRules.ToListAsync();

            // SQLite cannot compare DateTimeOffset, so expired rules are cleaned up here instead of in the query.
            var expired = rules.Where(IsExpired).ToArray();
            if (expired.Length > 0)
            {
                dbContext.MessageRules.RemoveRange(expired);
                await dbContext.SaveChangesAsync();
            }

            return _rules = rules
                .Except(expired)
                .Select(rule => new ActiveRule(rule, rule.Pattern is null ? null : CreatePattern(rule.Pattern)))
                .ToArray();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private sealed record ActiveRule(MessageRule Rule, Regex? Pattern);
}
