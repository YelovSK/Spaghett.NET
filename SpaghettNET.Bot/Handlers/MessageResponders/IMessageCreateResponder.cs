using NetCord.Gateway;

namespace SpaghettNET.Bot.Handlers.MessageResponders;

public interface IMessageCreateResponder
{
    ValueTask<bool> ShouldRespondAsync(Message message);
    ValueTask<MessageCreateResponse?> GetResponseAsync(Message message);
}

public sealed record MessageCreateResponse(
    MessageResponseType ResponseType,
    string Content,
    bool StopProcessing = false);

public enum MessageResponseType
{
    Reply,
    ChannelMessage,
}
