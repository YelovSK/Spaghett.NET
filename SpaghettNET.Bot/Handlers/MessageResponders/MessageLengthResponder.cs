using NetCord.Gateway;

namespace SpaghettNET.Bot.Handlers.MessageResponders;

public class MessageLengthResponder : IMessageCreateResponder
{
    private static readonly int[] SpecialLengths = [69, 322, 420, 1337];

    public ValueTask<bool> ShouldRespondAsync(Message message) =>
        ValueTask.FromResult(SpecialLengths.Contains(message.Content.Length));

    public ValueTask<MessageCreateResponse?> GetResponseAsync(Message message) =>
        ValueTask.FromResult<MessageCreateResponse?>(new MessageCreateResponse(
            MessageResponseType.Reply,
            $"poggers {message.Content.Length} characters"));
}
