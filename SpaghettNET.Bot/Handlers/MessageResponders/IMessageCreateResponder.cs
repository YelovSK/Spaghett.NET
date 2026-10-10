using NetCord.Gateway;

namespace SpaghettNET.Bot.Handlers.MessageResponders;

public interface IMessageCreateResponder
{
    ValueTask<bool> ShouldRespondAsync(Message message);
    ValueTask<MessageCreateResponse?> GetResponseAsync(Message message);
}

/// <param name="Content">Null when the message was handled without anything to send.</param>
public sealed record MessageCreateResponse(
    MessageResponseType ResponseType,
    string? Content,
    bool StopProcessing = false);

public enum MessageResponseType
{
    Reply,
    ChannelMessage,
}
