using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Rest;
using SpaghettNET.Bot.Services;
using SpaghettNET.Bot.Services.Scripting;
using SpaghettNET.Bot.Services.Chat;

namespace SpaghettNET.Bot.Handlers.MessageResponders;

/// <summary>The "message" hook: runs scripts on each server message and carries out what they asked for.</summary>
public class ScriptResponder(
    GatewayClient gatewayClient,
    ScriptService scriptService,
    OpenAIReplyGenerator replyGenerator,
    DiscordMessageSender messageSender,
    ILogger<ScriptResponder> logger) : IMessageCreateResponder
{
    // Scripts run once in ShouldRespondAsync so the handler only shows typing when they actually do something;
    // their actions wait here for GetResponseAsync.
    private readonly ConditionalWeakTable<Message, IReadOnlyList<ScriptAction>> _planned = new();

    public async ValueTask<bool> ShouldRespondAsync(Message message)
    {
        if (message.Guild is null)
        {
            return false;
        }

        var actions = await scriptService.RunAsync(
            ScriptEvents.Message,
            ScriptEvents.MessagePayload(message),
            ScriptEventContext.ForMessage(message));
        if (actions.Count == 0)
        {
            return false;
        }

        _planned.AddOrUpdate(message, actions);
        return true;
    }

    public async ValueTask<MessageCreateResponse?> GetResponseAsync(Message message)
    {
        if (!_planned.TryGetValue(message, out var actions))
        {
            return null;
        }

        _planned.Remove(message);
        var replies = new List<string>();
        var deleted = false;

        foreach (var action in actions)
        {
            try
            {
                switch (action.Kind)
                {
                    case ScriptActionKind.Reply:
                        replies.Add(action.Text!);
                        break;

                    case ScriptActionKind.Send:
                        await messageSender.SendAsync(action.ChannelId!.Value, action.Text!);
                        break;

                    case ScriptActionKind.LlmReply:
                        if (await replyGenerator.GetReplyAsync(message, action.Text) is { } reply)
                        {
                            replies.Add(reply);
                        }

                        break;

                    case ScriptActionKind.LlmSend:
                        if (await replyGenerator.GetReplyAsync(message, action.Text) is { } generated)
                        {
                            await messageSender.SendAsync(action.ChannelId!.Value, generated);
                        }

                        break;

                    case ScriptActionKind.React when !deleted:
                        await message.AddReactionAsync(ParseEmoji(action.Text!));
                        break;

                    case ScriptActionKind.DeleteMessage when !deleted:
                        await message.DeleteAsync();
                        deleted = true;
                        break;

                    case ScriptActionKind.Timeout:
                        await gatewayClient.Rest.ModifyGuildUserAsync(
                            message.GuildId!.Value,
                            action.UserId!.Value,
                            user => user.TimeOutUntil = action.Seconds > 0
                                ? DateTimeOffset.UtcNow.AddSeconds(action.Seconds.Value)
                                : null);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Script {ScriptId} action {Action} failed.", action.ScriptId, action);
            }
        }

        if (replies.Count == 0 && !deleted)
        {
            return null;
        }

        // Script replies sit alongside the bot's other responses, but a deleted message can't be responded to.
        return new MessageCreateResponse(
            deleted ? MessageResponseType.ChannelMessage : MessageResponseType.Reply,
            replies.Count > 0 ? string.Join(Environment.NewLine, replies) : null,
            StopProcessing: deleted);
    }

    /// <summary>Unicode emoji are kept as-is, server emotes arrive as name:id.</summary>
    private static ReactionEmojiProperties ParseEmoji(string emoji) =>
        emoji.Split(':') is [var name, var id] && ulong.TryParse(id, out var emojiId)
            ? new ReactionEmojiProperties(name, emojiId)
            : new ReactionEmojiProperties(emoji);
}
