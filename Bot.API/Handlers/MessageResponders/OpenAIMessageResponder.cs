using System.Text.RegularExpressions;
using Bot.API.Services;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Bot.API.Handlers.MessageResponders;

public partial class OpenAIMessageResponder(
    GatewayClient gatewayClient,
    OpenAIChatService chatService) : IMessageCreateResponder
{
    [GeneratedRegex(@"<(?<name>[A-Za-z0-9_]+)>")]
    private static partial Regex EmoteRegex();

    [GeneratedRegex(@"<a?:(?<name>[A-Za-z0-9_]+):\d+>")]
    private static partial Regex DiscordEmoteRegex();

    public ValueTask<bool> ShouldRespondAsync(Message message)
    {
        var hasMention = message.MentionedUsers.Any(user => user.Id == gatewayClient.Id);
        var hasName = message.Content.StartsWith("spaghett", StringComparison.InvariantCultureIgnoreCase);

        var shouldRespond = chatService.IsEnabled && (hasMention || hasName);

        return ValueTask.FromResult(shouldRespond);
    }

    public async ValueTask<MessageCreateResponse?> GetResponseAsync(Message message)
    {
        var contextMessages = await GetContextMessagesAsync(message);
        var response = await chatService.GetResponseAsync(
            FormatContextMessageContent(message.Content, message.Attachments),
            contextMessages,
            BuildRequestContext(message));

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
        var contextMessageCount = chatService.ContextMessageCount;
        if (contextMessageCount == 0)
        {
            return [];
        }

        var pagination = new PaginationProperties<ulong>
        {
            From = message.Id,
            Direction = PaginationDirection.Before,
            BatchSize = contextMessageCount,
        };

        var messages = new List<OpenAIContextMessage>();

        await foreach (var contextMessage in gatewayClient.Rest.GetMessagesAsync(message.ChannelId, pagination))
        {
            var content = FormatContextMessageContent(contextMessage.Content, contextMessage.Attachments);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            messages.Add(new OpenAIContextMessage(
                contextMessage.CreatedAt,
                contextMessage.Author.Username,
                content));

            if (messages.Count == contextMessageCount)
            {
                break;
            }
        }

        messages.Reverse();
        return messages;
    }

    private static string NormalizeEmoteTokens(string messageContent) =>
        DiscordEmoteRegex().Replace(messageContent, match => $"<{match.Groups["name"].Value}>");

    private static OpenAIRequestContext BuildRequestContext(Message message)
    {
        var channelName = message.Channel is INamedChannel namedChannel ? namedChannel.Name : null;
        var channelTopic = message.Channel is TextGuildChannel textGuildChannel ? textGuildChannel.Topic : null;
        var authorName = message.Author.GlobalName ?? message.Author.Username;

        return new OpenAIRequestContext(
            message.Guild?.Name,
            message.ChannelId,
            channelName,
            channelTopic,
            authorName);
    }

    private static string FormatContextMessageContent(string content, IReadOnlyList<Attachment> attachments)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(content))
        {
            parts.Add(NormalizeEmoteTokens(content));
        }

        if (attachments.Count > 0)
        {
            parts.Add(FormatAttachments(attachments));
        }

        return string.Join(" ", parts);
    }

    private static string FormatAttachments(IReadOnlyList<Attachment> attachments)
    {
        var attachmentSummaries = attachments.Select(FormatAttachment);
        return $"[attachments: {attachments.Count}; {string.Join("; ", attachmentSummaries)}]";
    }

    private static string FormatAttachment(Attachment attachment)
    {
        var kind = attachment switch
        {
            ImageAttachment => "image",
            VoiceAttachment => "voice",
            _ when attachment.ContentType?.StartsWith("image/", StringComparison.InvariantCultureIgnoreCase) == true => "image",
            _ when attachment.ContentType?.StartsWith("video/", StringComparison.InvariantCultureIgnoreCase) == true => "video",
            _ => "file",
        };

        var details = new List<string> { kind };

        if (!string.IsNullOrWhiteSpace(attachment.FileName))
        {
            details.Add(attachment.FileName);
        }

        return string.Join(", ", details);
    }
}
