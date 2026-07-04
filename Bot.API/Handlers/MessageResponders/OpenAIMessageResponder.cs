using System.Text.RegularExpressions;
using Bot.API.Services;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Bot.API.Handlers.MessageResponders;

public partial class OpenAIMessageResponder(
    GatewayClient gatewayClient,
    ILogger<OpenAIMessageResponder> logger,
    OpenAIChatRuntimeSettings runtimeSettings,
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
        var messageContext = await GetContextMessagesAsync(message);
        var imageInputs = BuildImageInputs(message, messageContext.ImageInputs);
        var response = await chatService.GetResponseAsync(
            FormatContextMessageContent(message.Content, message.Attachments),
            messageContext.Messages,
            BuildRequestContext(message),
            imageInputs);

        if (string.IsNullOrWhiteSpace(response))
        {
            logger.LogWarning(
                "OpenAI responder produced no response. ChannelId: {ChannelId}, MessageId: {MessageId}, AuthorId: {AuthorId}, Prompt: {Prompt}",
                message.ChannelId,
                message.Id,
                message.Author.Id,
                FormatContextMessageContent(message.Content, message.Attachments));

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

    private async Task<OpenAIMessageContext> GetContextMessagesAsync(Message message)
    {
        var contextMessageCount = runtimeSettings.ContextMessageCount;
        if (contextMessageCount == 0)
        {
            return new OpenAIMessageContext([], []);
        }

        var pagination = new PaginationProperties<ulong>
        {
            From = message.Id,
            Direction = PaginationDirection.Before,
            BatchSize = contextMessageCount,
        };

        var messages = new List<OpenAIContextMessage>();
        var imageInputs = new List<OpenAIImageInput>();

        await foreach (var contextMessage in gatewayClient.Rest.GetMessagesAsync(message.ChannelId, pagination))
        {
            imageInputs.AddRange(GetImageInputs(contextMessage));

            var content = FormatContextMessageContent(contextMessage.Content, contextMessage.Attachments);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            messages.Add(new OpenAIContextMessage(
                contextMessage.CreatedAt,
                GetAuthorName(contextMessage.Author),
                content));

            if (messages.Count == contextMessageCount)
            {
                break;
            }
        }

        messages.Reverse();
        return new OpenAIMessageContext(messages, imageInputs.OrderBy(imageInput => imageInput.SentAt).ToArray());
    }

    private static string NormalizeEmoteTokens(string messageContent) =>
        DiscordEmoteRegex().Replace(messageContent, match => $"<{match.Groups["name"].Value}>");

    private static OpenAIRequestContext BuildRequestContext(Message message)
    {
        var channelName = message.Channel is INamedChannel namedChannel ? namedChannel.Name : null;
        var channelTopic = message.Channel is TextGuildChannel textGuildChannel ? textGuildChannel.Topic : null;

        return new OpenAIRequestContext(
            message.Guild?.Name,
            message.ChannelId,
            channelName,
            channelTopic,
            GetAuthorName(message.Author));
    }

    private static string FormatContextMessageContent(string content, IReadOnlyList<Attachment> attachments)
    {
        var parts = new List<string>();
        var messageContent = FormatMessageTextContent(content);

        if (!string.IsNullOrWhiteSpace(messageContent))
        {
            parts.Add(messageContent);
        }

        if (attachments.Count > 0)
        {
            parts.Add(FormatAttachments(attachments));
        }

        return string.Join(" ", parts);
    }

    private static string? FormatMessageTextContent(string content)
    {
        var normalizedContent = NormalizeEmoteTokens(content).Trim();
        return string.IsNullOrWhiteSpace(normalizedContent) ? null : normalizedContent;
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

    private IReadOnlyList<OpenAIImageInput>? BuildImageInputs(
        Message currentMessage,
        IReadOnlyList<OpenAIImageInput> contextImageInputs)
    {
        var currentImageInputs = GetImageInputs(currentMessage)
            .Take(runtimeSettings.ContextImagesCount)
            .ToArray();
        var contextImageInputCount = Math.Max(0, runtimeSettings.ContextImagesCount - currentImageInputs.Length);
        var recentContextImageInputs = contextImageInputs
            .OrderByDescending(imageInput => imageInput.SentAt)
            .Take(contextImageInputCount)
            .OrderBy(imageInput => imageInput.SentAt)
            .ToArray();
        var imageInputs = recentContextImageInputs
            .Concat(currentImageInputs)
            .ToArray();

        return imageInputs.Length == 0 ? null : imageInputs;
    }

    private static IEnumerable<OpenAIImageInput> GetImageInputs(Message message) =>
        GetImageInputs(message.CreatedAt, message.Author, message.Content, message.Attachments);

    private static IEnumerable<OpenAIImageInput> GetImageInputs(RestMessage message) =>
        GetImageInputs(message.CreatedAt, message.Author, message.Content, message.Attachments);

    private static IEnumerable<OpenAIImageInput> GetImageInputs(
        DateTimeOffset createdAt,
        User author,
        string content,
        IReadOnlyList<Attachment> attachments)
    {
        var messageContent = FormatMessageTextContent(content);
        var authorName = GetAuthorName(author);

        return attachments
            .Where(IsImageAttachment)
            .Where(attachment => !string.IsNullOrWhiteSpace(attachment.Url))
            .Select(attachment => new OpenAIImageInput(
                attachment.Url,
                createdAt,
                authorName,
                messageContent,
                attachment.FileName));
    }

    private static bool IsImageAttachment(Attachment attachment) =>
        attachment is ImageAttachment ||
        attachment.ContentType?.StartsWith("image/", StringComparison.InvariantCultureIgnoreCase) == true;

    private static string GetAuthorName(User user) => user.GlobalName ?? user.Username;

    private sealed record OpenAIMessageContext(
        IReadOnlyList<OpenAIContextMessage> Messages,
        IReadOnlyList<OpenAIImageInput> ImageInputs);
}
