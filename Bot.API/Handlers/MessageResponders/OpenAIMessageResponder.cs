using System.Text.RegularExpressions;
using Bot.API.Services;
using NetCord.Gateway;
using NetCord.Rest;

namespace Bot.API.Handlers.MessageResponders;

public partial class OpenAIMessageResponder(
    GatewayClient gatewayClient,
    OpenAIChatService chatService) : IMessageCreateResponder
{
    private const int CONTEXT_MESSAGE_COUNT = 10;

    [GeneratedRegex(@"<(?<name>[A-Za-z0-9_]+)>")]
    private static partial Regex EmoteRegex();

    public ValueTask<bool> ShouldRespondAsync(Message message)
    {
        var shouldRespond =
            chatService.IsEnabled &&
            message.Content.StartsWith("spaghett", StringComparison.InvariantCultureIgnoreCase);

        return ValueTask.FromResult(shouldRespond);
    }

    public async ValueTask<MessageCreateResponse?> GetResponseAsync(Message message)
    {
        var contextMessages = await GetContextMessagesAsync(message);
        var response = await chatService.GetResponseAsync(message.Content, contextMessages);

        if (string.IsNullOrWhiteSpace(response))
        {
            return new MessageCreateResponse(MessageResponseType.ChannelMessage, "OpenAI-compatible chat did not answer", true);
        }

        return new MessageCreateResponse(
            MessageResponseType.ChannelMessage,
            await InsertEmoteTokensAsync(message, response),
            true);
    }

    private async Task<string> InsertEmoteTokensAsync(Message sourceMessage, string response)
    {
        if (sourceMessage.GuildId is not { } guildId)
        {
            return response;
        }

        var matches = EmoteRegex().Matches(response);
        if (matches.Count == 0)
        {
            return response;
        }

        var requestedEmoteNames = matches
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.InvariantCultureIgnoreCase);

        var emojis = await gatewayClient.Rest.GetGuildEmojisAsync(guildId);
        var emoteTokens = emojis
            .Where(emoji => emoji.Available != false)
            .Where(emoji => requestedEmoteNames.Contains(emoji.Name))
            .GroupBy(emoji => emoji.Name, StringComparer.InvariantCultureIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var emoji = group.First();
                    return emoji.Animated
                        ? $"<a:{emoji.Name}:{emoji.Id}>"
                        : $"<:{emoji.Name}:{emoji.Id}>";
                },
                StringComparer.InvariantCultureIgnoreCase);

        return EmoteRegex().Replace(response, match =>
            emoteTokens.TryGetValue(match.Groups["name"].Value, out var token)
                ? token
                : match.Value);
    }

    private async Task<IReadOnlyList<OpenAIContextMessage>> GetContextMessagesAsync(Message message)
    {
        var pagination = new PaginationProperties<ulong>
        {
            From = message.Id,
            Direction = PaginationDirection.Before,
            BatchSize = CONTEXT_MESSAGE_COUNT,
        };

        var messages = new List<OpenAIContextMessage>();

        await foreach (var contextMessage in gatewayClient.Rest.GetMessagesAsync(message.ChannelId, pagination))
        {
            if (string.IsNullOrWhiteSpace(contextMessage.Content))
            {
                continue;
            }

            messages.Add(new OpenAIContextMessage(contextMessage.CreatedAt, contextMessage.Author.Username, contextMessage.Content));

            if (messages.Count == CONTEXT_MESSAGE_COUNT)
            {
                break;
            }
        }

        messages.Reverse();
        return messages;
    }
}
