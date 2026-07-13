using NetCord.Gateway;

namespace SpaghettNET.Bot.Handlers.MessageResponders;

public class CapsLockResponder : IMessageCreateResponder
{
    public ValueTask<bool> ShouldRespondAsync(Message message)
    {
        if (message.Content.Length < 10)
        {
            return ValueTask.FromResult(false);
        }

        var isAllUpper = message.Content
            .Where(c => !char.IsWhiteSpace(c))
            .All(char.IsUpper);

        return ValueTask.FromResult(isAllUpper);
    }

    public ValueTask<MessageCreateResponse?> GetResponseAsync(Message message) =>
        ValueTask.FromResult<MessageCreateResponse?>(new MessageCreateResponse(MessageResponseType.Reply, "y u shout"));
}
