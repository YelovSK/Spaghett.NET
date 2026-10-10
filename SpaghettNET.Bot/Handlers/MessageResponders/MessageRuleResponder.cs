using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Rest;
using SpaghettNET.Bot.Services;
using SpaghettNET.Persistence.Models;

namespace SpaghettNET.Bot.Handlers.MessageResponders;

public class MessageRuleResponder(
    GatewayClient gatewayClient,
    MessageRuleService ruleService,
    OpenAIReplyGenerator replyGenerator,
    ILogger<MessageRuleResponder> logger) : IMessageCreateResponder
{
    public async ValueTask<bool> ShouldRespondAsync(Message message) =>
        (await ruleService.GetMatchingRulesAsync(message)).Count > 0;

    public async ValueTask<MessageCreateResponse?> GetResponseAsync(Message message)
    {
        var rules = (await ruleService.GetMatchingRulesAsync(message))
            .Where(rule => rule.Chance is not { } chance || Random.Shared.NextDouble() < chance)
            .ToArray();
        var replies = new List<string>();
        var deleted = false;

        foreach (var rule in rules)
        {
            try
            {
                switch (rule.Action)
                {
                    case MessageRuleAction.Reply:
                        replies.Add(rule.Text!);
                        break;

                    case MessageRuleAction.LlmReply:
                        if (await replyGenerator.GetReplyAsync(message, rule.Text) is { } reply)
                        {
                            replies.Add(reply);
                        }

                        break;

                    case MessageRuleAction.React:
                        await message.AddReactionAsync(ParseEmoji(rule.Text!));
                        break;

                    case MessageRuleAction.Delete when !deleted:
                        await message.DeleteAsync();
                        deleted = true;
                        break;

                    case MessageRuleAction.Timeout:
                        await gatewayClient.Rest.ModifyGuildUserAsync(
                            rule.GuildId,
                            message.Author.Id,
                            user => user.TimeOutUntil = DateTimeOffset.UtcNow.AddSeconds(rule.TimeoutSeconds!.Value));
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Message rule {RuleId} ({Action}) failed.", rule.Id, rule.Action);
            }
        }

        if (replies.Count == 0 && !deleted)
        {
            return null;
        }

        // A rule that talks replaces the normal bot reply, and a deleted message can't be replied to.
        return new MessageCreateResponse(
            deleted ? MessageResponseType.ChannelMessage : MessageResponseType.Reply,
            replies.Count > 0 ? string.Join(Environment.NewLine, replies) : null,
            StopProcessing: true);
    }

    /// <summary>Unicode emoji are stored as-is, custom emoji as name:id.</summary>
    private static ReactionEmojiProperties ParseEmoji(string emoji) =>
        emoji.Split(':') is [var name, var id] && ulong.TryParse(id, out var emojiId)
            ? new ReactionEmojiProperties(name, emojiId)
            : new ReactionEmojiProperties(emoji);
}
