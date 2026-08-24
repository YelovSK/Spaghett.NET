using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace SpaghettNET.Bot.Services;

public partial class OpenAIReplyGenerator(
    GatewayClient gatewayClient,
    OpenAIOptions options,
    OpenAIChatRuntimeSettings runtimeSettings,
    ModelMetadataResolver modelMetadataResolver,
    OpenAIChatClient chatClient,
    ILogger<OpenAIReplyGenerator> logger)
{
    private const int ContextBootstrapDivisor = 2;
    private const int MaxContextImages = 4;
    private readonly ConcurrentDictionary<ulong, ChannelContext> _channelContexts = [];

    [GeneratedRegex(@"<(?<name>[A-Za-z0-9_]+)>")]
    private static partial Regex EmoteRegex();

    [GeneratedRegex(@"<a?:(?<name>[A-Za-z0-9_]+):\d+>")]
    private static partial Regex DiscordEmoteRegex();

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(options.BaseUrl) &&
        !string.IsNullOrWhiteSpace(runtimeSettings.Model) &&
        !string.IsNullOrWhiteSpace(options.SystemPromptPath);

    public async Task<string?> GetReplyAsync(Message message, CancellationToken cancellationToken = default)
    {
        var channelContext = _channelContexts.GetOrAdd(message.ChannelId, static _ => new ChannelContext());
        await channelContext.Lock.WaitAsync(cancellationToken);

        try
        {
            var messageContext = await GetContextMessagesAsync(message, channelContext, cancellationToken);
            var response = await GetChatResponseAsync(messageContext, cancellationToken);

            if (string.IsNullOrWhiteSpace(response))
            {
                logger.LogWarning(
                    "OpenAI responder produced no response. ChannelId: {ChannelId}, MessageId: {MessageId}, AuthorId: {AuthorId}, Prompt: {Prompt}",
                    message.ChannelId,
                    message.Id,
                    message.Author.Id,
                    FormatContextMessageContent(message.Content, message.Attachments));
                return null;
            }

            return await InsertEmoteTokensAsync(message, response);
        }
        finally
        {
            channelContext.Lock.Release();
        }
    }

    private async Task<string?> GetChatResponseAsync(
        OpenAIMessageContext messageContext,
        CancellationToken cancellationToken)
    {
        var model = runtimeSettings.Model;

        if (string.IsNullOrWhiteSpace(options.BaseUrl) ||
            string.IsNullOrWhiteSpace(model) ||
            string.IsNullOrWhiteSpace(options.SystemPromptPath))
        {
            logger.LogWarning(
                "OpenAI-compatible chat is not configured. BaseUrl configured: {HasBaseUrl}, Model configured: {HasModel}, SystemPromptPath configured: {HasSystemPromptPath}.",
                !string.IsNullOrWhiteSpace(options.BaseUrl),
                !string.IsNullOrWhiteSpace(model),
                !string.IsNullOrWhiteSpace(options.SystemPromptPath));
            return null;
        }

        var systemPrompt = await ReadSystemPromptAsync(options.SystemPromptPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            logger.LogWarning("OpenAI-compatible chat skipped because the system prompt was empty or could not be read.");
            return null;
        }

        var tools = BuildTools(options.Tools);
        var imageInputs = messageContext.Messages
            .SelectMany(contextMessage => contextMessage.ImageInputs)
            .OrderByDescending(imageInput => imageInput.SentAt)
            .Take(MaxContextImages)
            .ToArray();
        var supportedImageInputs = await GetSupportedImageInputsAsync(model, imageInputs, cancellationToken);
        var chatRequest = new OpenAIChatRequest(
            model,
            BuildMessages(systemPrompt, messageContext, supportedImageInputs),
            options.Temperature,
            options.MaxTokens,
            tools);

        return await chatClient.GetResponseAsync(chatRequest, messageContext.SessionId, cancellationToken);
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

    private async Task<OpenAIMessageContext> GetContextMessagesAsync(
        Message message,
        ChannelContext channelContext,
        CancellationToken cancellationToken)
    {
        var pagination = new PaginationProperties<ulong>
        {
            From = message.Id,
            Direction = PaginationDirection.Before,
            BatchSize = 100,
        };

        var messages = new List<OpenAIContextMessage> { CreateContextMessage(message) };
        var characterCount = GetFormattedLength(messages[0]);
        var anchorMessageId = channelContext.AnchorMessageId;
        var foundAnchor = anchorMessageId == message.Id;
        var bootstrapCharacterBudget = runtimeSettings.ContextCharacterBudget / ContextBootstrapDivisor;

        await foreach (var contextMessage in gatewayClient.Rest
                           .GetMessagesAsync(message.ChannelId, pagination)
                           .WithCancellation(cancellationToken))
        {
            var formattedMessage = CreateContextMessage(contextMessage);
            messages.Add(formattedMessage);
            characterCount += GetFormattedLength(formattedMessage);

            if (contextMessage.Id == anchorMessageId)
            {
                foundAnchor = true;
                break;
            }

            if (anchorMessageId.HasValue
                    ? characterCount > runtimeSettings.ContextCharacterBudget
                    : characterCount >= bootstrapCharacterBudget)
            {
                break;
            }
        }

        var foundCacheBreakpoint = messages.Any(contextMessage =>
            contextMessage.Id == channelContext.CacheBreakpointMessageId);

        if (!foundAnchor || !foundCacheBreakpoint || characterCount > runtimeSettings.ContextCharacterBudget)
        {
            messages = TakeNewestMessages(messages, bootstrapCharacterBudget);
            channelContext.AnchorMessageId = messages[^1].Id;
            channelContext.CacheBreakpointMessageId = messages[0].Id;
        }

        messages.Reverse();
        return new OpenAIMessageContext(
            FormatChannel(message) + Environment.NewLine,
            messages,
            channelContext.CacheBreakpointMessageId!.Value,
            $"discord:{message.ChannelId}:{channelContext.AnchorMessageId}");
    }

    private static List<OpenAIContextMessage> TakeNewestMessages(
        IReadOnlyList<OpenAIContextMessage> messages,
        int characterBudget)
    {
        var result = new List<OpenAIContextMessage>();
        var characterCount = 0;

        foreach (var message in messages)
        {
            var messageLength = GetFormattedLength(message);
            if (result.Count > 0 && characterCount + messageLength > characterBudget)
            {
                break;
            }

            result.Add(message);
            characterCount += messageLength;
        }

        return result;
    }

    private static int GetFormattedLength(OpenAIContextMessage message) =>
        FormatContextMessage(message).Length + Environment.NewLine.Length;

    private async Task<IReadOnlyList<OpenAIImageInput>?> GetSupportedImageInputsAsync(
        string model,
        IReadOnlyList<OpenAIImageInput>? imageInputs,
        CancellationToken cancellationToken)
    {
        if (imageInputs is not { Count: > 0 })
        {
            return null;
        }

        var supportsVision = await modelMetadataResolver.SupportsAsync(
            new ModelMetadataRequest(options.BaseUrl, options.ApiKey, model),
            ModelCapability.Vision,
            cancellationToken);

        if (supportsVision)
        {
            return imageInputs;
        }

        logger.LogInformation(
            "Skipping image input because model {Model} does not have the {Capability} capability.",
            model,
            ModelCapability.Vision);
        return null;
    }

    private async Task<string?> ReadSystemPromptAsync(string systemPromptPath, CancellationToken cancellationToken)
    {
        var resolvedPath = ResolveConfiguredPath(systemPromptPath);
        if (!File.Exists(resolvedPath))
        {
            logger.LogWarning("Configured system prompt file does not exist: {SystemPromptPath}", resolvedPath);
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(resolvedPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read configured system prompt file: {SystemPromptPath}", resolvedPath);
            return null;
        }
    }

    private static string ResolveConfiguredPath(string configuredPath)
    {
        if (Path.IsPathFullyQualified(configuredPath))
        {
            return configuredPath;
        }

        return Path.GetFullPath(configuredPath);
    }

    private static IReadOnlyList<OpenAIChatMessage> BuildMessages(
        string systemPrompt,
        OpenAIMessageContext messageContext,
        IReadOnlyList<OpenAIImageInput>? imageInputs)
    {
        return
        [
            new OpenAIChatMessage("system", systemPrompt),
            new OpenAIChatMessage("user", BuildUserMessageContent(messageContext, imageInputs)),
        ];
    }

    private static string FormatContextMessage(OpenAIContextMessage message) =>
        $"[{message.SentAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss 'UTC'}] {message.AuthorName}: {message.Content}";

    private static IReadOnlyList<OpenAIChatTool>? BuildTools(IReadOnlyList<OpenAIChatToolOptions>? toolOptions)
    {
        var tools = toolOptions?
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Type))
            .Select(tool => new OpenAIChatTool(tool.Type!))
            .ToArray();

        return tools is { Length: > 0 } ? tools : null;
    }

    private static object BuildUserMessageContent(
        OpenAIMessageContext messageContext,
        IReadOnlyList<OpenAIImageInput>? imageInputs)
    {
        var supportedImageUrls = imageInputs?
            .Select(imageInput => imageInput.Url)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var contentParts = new List<OpenAIChatContentPart>
        {
            new("text", Text: messageContext.ChannelHeader),
        };

        foreach (var message in messageContext.Messages)
        {
            var textPart = new OpenAIChatContentPart(
                "text",
                Text: FormatContextMessage(message) + Environment.NewLine);
            if (message.Id == messageContext.CacheBreakpointMessageId)
            {
                textPart = textPart with { CacheControl = new("ephemeral") };
            }

            contentParts.Add(textPart);
            contentParts.AddRange(message.ImageInputs
                .Where(imageInput => supportedImageUrls.Contains(imageInput.Url))
                .Select(imageInput => new OpenAIChatContentPart(
                    "image_url",
                    ImageUrl: new OpenAIChatImageUrl(imageInput.Url))));
        }

        return contentParts;
    }

    private static string NormalizeEmoteTokens(string messageContent) =>
        DiscordEmoteRegex().Replace(messageContent, match => $"<{match.Groups["name"].Value}>");

    private static string FormatChannel(Message message) =>
        message.Channel is INamedChannel namedChannel
            ? $"Discord channel: #{namedChannel.Name}"
            : $"Discord channel ID: {message.ChannelId}";

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

    private static OpenAIContextMessage CreateContextMessage(Message message) =>
        CreateContextMessage(
            message.Id,
            message.CreatedAt,
            message.Author,
            message.Content,
            message.Attachments);

    private static OpenAIContextMessage CreateContextMessage(RestMessage message) =>
        CreateContextMessage(
            message.Id,
            message.CreatedAt,
            message.Author,
            message.Content,
            message.Attachments);

    private static OpenAIContextMessage CreateContextMessage(
        ulong id,
        DateTimeOffset createdAt,
        User author,
        string content,
        IReadOnlyList<Attachment> attachments) =>
        new(
            id,
            createdAt,
            GetAuthorName(author),
            FormatContextMessageContent(content, attachments),
            GetImageInputs(createdAt, attachments).ToArray());

    private static IEnumerable<OpenAIImageInput> GetImageInputs(
        DateTimeOffset createdAt,
        IReadOnlyList<Attachment> attachments)
    {
        return attachments
            .Where(IsImageAttachment)
            .Where(attachment => !string.IsNullOrWhiteSpace(attachment.Url))
            .Select(attachment => new OpenAIImageInput(attachment.Url, createdAt));
    }

    private static bool IsImageAttachment(Attachment attachment) =>
        attachment is ImageAttachment ||
        attachment.ContentType?.StartsWith("image/", StringComparison.InvariantCultureIgnoreCase) == true;

    private static string GetAuthorName(User user) => user.GlobalName ?? user.Username;

    private sealed record OpenAIMessageContext(
        string ChannelHeader,
        IReadOnlyList<OpenAIContextMessage> Messages,
        ulong CacheBreakpointMessageId,
        string SessionId);

    private sealed record OpenAIContextMessage(
        ulong Id,
        DateTimeOffset SentAt,
        string AuthorName,
        string Content,
        IReadOnlyList<OpenAIImageInput> ImageInputs);

    private sealed class ChannelContext
    {
        public ulong? AnchorMessageId { get; set; }
        public ulong? CacheBreakpointMessageId { get; set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    private sealed record OpenAIImageInput(
        string Url,
        DateTimeOffset SentAt);
}
