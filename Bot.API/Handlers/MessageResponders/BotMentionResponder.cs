using NetCord.Gateway;

namespace Bot.API.Handlers.MessageResponders;

public class BotMentionResponder(GatewayClient gatewayClient) : IMessageCreateResponder
{
    public ValueTask<bool> ShouldRespondAsync(Message message) =>
        ValueTask.FromResult(message.MentionedUsers.Any(user => user.Id == gatewayClient.Id));

    public ValueTask<MessageCreateResponse?> GetResponseAsync(Message message) =>
        ValueTask.FromResult<MessageCreateResponse?>(new MessageCreateResponse(MessageResponseType.Reply, "stfu"));
}
