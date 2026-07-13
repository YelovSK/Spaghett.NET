using Bot.API.Services;
using NetCord.Gateway;

namespace Bot.API.Handlers.MessageResponders;

public class OpenAIMessageResponder(
    GatewayClient gatewayClient,
    OpenAIReplyGenerator replyGenerator) : IMessageCreateResponder
{
    public ValueTask<bool> ShouldRespondAsync(Message message)
    {
        var hasMention = message.MentionedUsers.Any(user => user.Id == gatewayClient.Id);
        var hasName = message.Content.StartsWith("spaghett", StringComparison.InvariantCultureIgnoreCase);

        return ValueTask.FromResult(replyGenerator.IsEnabled && (hasMention || hasName));
    }

    public async ValueTask<MessageCreateResponse?> GetResponseAsync(Message message)
    {
        var response = await replyGenerator.GetReplyAsync(message);

        return string.IsNullOrWhiteSpace(response)
            ? new MessageCreateResponse(
                MessageResponseType.ChannelMessage,
                "OpenAI-compatible chat did not answer",
                true)
            : new MessageCreateResponse(MessageResponseType.ChannelMessage, response, true);
    }
}
