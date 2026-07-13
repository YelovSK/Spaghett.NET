using SpaghettNET.Bot.Extensions;
using NetCord.Gateway;

namespace SpaghettNET.Bot.Handlers.MessageResponders;

public class SpecialWordResponder : IMessageCreateResponder
{
    public ValueTask<bool> ShouldRespondAsync(Message message)
    {
        var words = message.Content
            .SplitWords()
            .ToHashSet(StringComparer.InvariantCultureIgnoreCase);

        return ValueTask.FromResult(words.Contains("cat"));
    }

    public ValueTask<MessageCreateResponse?> GetResponseAsync(Message message) =>
        ValueTask.FromResult<MessageCreateResponse?>(new MessageCreateResponse(MessageResponseType.Reply, "ur cat"));
}
