using NetCord;
using NetCord.Gateway;

namespace SpaghettNET.Bot.Services.Scripting;

/// <summary>
/// The events scripts can register for with on(event, handler).
/// A new hook is a new name here, a payload builder, and a call to <see cref="ScriptService.RunAsync"/> from its gateway handler.
/// </summary>
public static class ScriptEvents
{
    public const string Message = "message";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Message };

    /// <summary>What the message handler receives as msg. Ids are strings because JS numbers can't hold snowflakes.</summary>
    public static object MessagePayload(Message message) => new
    {
        id = message.Id.ToString(),
        content = message.Content,
        createdAt = message.CreatedAt.ToUnixTimeMilliseconds(),
        author = new
        {
            id = message.Author.Id.ToString(),
            name = (message.Author as GuildUser)?.Nickname ?? message.Author.GlobalName ?? message.Author.Username,
            username = message.Author.Username,
            isBot = message.Author.IsBot,
        },
        channel = new
        {
            id = message.ChannelId.ToString(),
            name = (message.Channel as INamedChannel)?.Name,
        },
        mentions = message.MentionedUsers.Select(user => user.Id.ToString()).ToArray(),
        mentionsEveryone = message.MentionEveryone,
        hasAttachments = message.Attachments.Count > 0,
    };
}

/// <summary>
/// What an event lets scripts act on. Message-bound api calls like reply and react need <see cref="Message"/>,
/// so events without a message get those calls rejected with a clear error.
/// </summary>
public sealed record ScriptEventContext(Guild Guild, Message? Message)
{
    public static ScriptEventContext ForMessage(Message message) => new(message.Guild!, message);
}
