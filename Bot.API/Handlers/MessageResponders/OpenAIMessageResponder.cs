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
        var latestImageInput = GetLatestImageInput(message.Attachments, messageContext.ImageCandidates);
        var response = await chatService.GetResponseAsync(
            FormatContextMessageContent(message.Content, message.Attachments),
            messageContext.Messages,
            BuildRequestContext(message),
            latestImageInput is null ? null : [latestImageInput]);

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
        var contextMessageCount = chatService.ContextMessageCount;
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
        var imageCandidates = new List<OpenAIImageCandidate>();

        await foreach (var contextMessage in gatewayClient.Rest.GetMessagesAsync(message.ChannelId, pagination))
        {
            imageCandidates.AddRange(GetImageCandidates(contextMessage.Attachments));

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
        return new OpenAIMessageContext(messages, imageCandidates);
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

    private static OpenAIImageInput? GetLatestImageInput(
        IReadOnlyList<Attachment> currentMessageAttachments,
        IReadOnlyList<OpenAIImageCandidate> contextImageCandidates)
    {
        var latestImage = GetImageCandidates(currentMessageAttachments)
            .Concat(contextImageCandidates)
            .MaxBy(candidate => candidate.CreatedAt);

        return latestImage is null ? null : new OpenAIImageInput(latestImage.Url);
    }

    private static IEnumerable<OpenAIImageCandidate> GetImageCandidates(IReadOnlyList<Attachment> attachments) =>
        attachments
            .Where(IsImageAttachment)
            .Where(attachment => !string.IsNullOrWhiteSpace(attachment.Url))
            .Select(attachment => new OpenAIImageCandidate(attachment.CreatedAt, attachment.Url));

    private static bool IsImageAttachment(Attachment attachment) =>
        attachment is ImageAttachment ||
        attachment.ContentType?.StartsWith("image/", StringComparison.InvariantCultureIgnoreCase) == true;

    private sealed record OpenAIMessageContext(
        IReadOnlyList<OpenAIContextMessage> Messages,
        IReadOnlyList<OpenAIImageCandidate> ImageCandidates);

    private sealed record OpenAIImageCandidate(DateTimeOffset CreatedAt, string Url);
}
