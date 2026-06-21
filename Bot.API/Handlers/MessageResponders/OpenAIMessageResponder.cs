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
    private const int EMOTE_CONTEXT_COUNT = 50;

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
        var emotes = await GetEmotesAsync(message);
        var response = await chatService.GetResponseAsync(message.Content, contextMessages, emotes);

        if (string.IsNullOrWhiteSpace(response))
        {
            return new MessageCreateResponse(MessageResponseType.ChannelMessage, "OpenAI-compatible chat did not answer", true);
        }

        return new MessageCreateResponse(
            MessageResponseType.ChannelMessage,
            InsertEmoteTokens(response, emotes),
            true);
    }

    private static string InsertEmoteTokens(string response, IReadOnlyList<OpenAIEmote> emotes)
    {
        if (emotes.Count == 0)
        {
            return response;
        }

        var emoteTokens = emotes
            .GroupBy(emote => emote.Name, StringComparer.InvariantCultureIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Token,
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

            messages.Add(new OpenAIContextMessage(contextMessage.Author.Username, contextMessage.Content));

            if (messages.Count == CONTEXT_MESSAGE_COUNT)
            {
                break;
            }
        }

        messages.Reverse();
        return messages;
    }

    private async Task<IReadOnlyList<OpenAIEmote>> GetEmotesAsync(Message message)
    {
        if (message.GuildId is not { } guildId)
        {
            return [];
        }

        var emojis = await gatewayClient.Rest.GetGuildEmojisAsync(guildId);

        return emojis
            .Where(emoji => emoji.Available != false)
            .OrderBy(emoji => emoji.Name)
            .Take(EMOTE_CONTEXT_COUNT)
            .Select(emoji => new OpenAIEmote(
                emoji.Name,
                emoji.Animated
                    ? $"<a:{emoji.Name}:{emoji.Id}>"
                    : $"<:{emoji.Name}:{emoji.Id}>"))
            .ToArray();
    }
}
