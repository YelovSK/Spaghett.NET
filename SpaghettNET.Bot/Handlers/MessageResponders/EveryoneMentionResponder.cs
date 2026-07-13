using NetCord.Gateway;

namespace SpaghettNET.Bot.Handlers.MessageResponders;

public class EveryoneMentionResponder : IMessageCreateResponder
{
    public ValueTask<bool> ShouldRespondAsync(Message message) =>
        ValueTask.FromResult(message.MentionEveryone);

    public ValueTask<MessageCreateResponse?> GetResponseAsync(Message message) =>
        ValueTask.FromResult<MessageCreateResponse?>(new MessageCreateResponse(MessageResponseType.Reply, "war crime detected"));
}
