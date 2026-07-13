using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

namespace Bot.API.Services;

public partial class OpenAIReplyGenerator(
    GatewayClient gatewayClient,
    OpenAIOptions options,
    OpenAIChatRuntimeSettings runtimeSettings,
    ModelMetadataResolver modelMetadataResolver,
    OpenAIChatClient chatClient,
    ILogger<OpenAIReplyGenerator> logger)
{
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
        var messageContext = await GetContextMessagesAsync(message);
        var imageInputs = BuildImageInputs(message, messageContext.ImageInputs);
        var prompt = FormatContextMessageContent(message.Content, message.Attachments);
        var response = await GetChatResponseAsync(
            prompt,
            messageContext.Messages,
            BuildRequestContext(message),
            imageInputs,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(response))
        {
            logger.LogWarning(
                "OpenAI responder produced no response. ChannelId: {ChannelId}, MessageId: {MessageId}, AuthorId: {AuthorId}, Prompt: {Prompt}",
                message.ChannelId,
                message.Id,
                message.Author.Id,
                prompt);
            return null;
        }

        return await InsertEmoteTokensAsync(message, response);
    }

    private async Task<string?> GetChatResponseAsync(
        string prompt,
        IReadOnlyList<OpenAIContextMessage>? contextMessages,
        OpenAIRequestContext? requestContext,
        IReadOnlyList<OpenAIImageInput>? imageInputs,
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
        var supportedImageInputs = await GetSupportedImageInputsAsync(model, imageInputs, cancellationToken);
        var chatRequest = new OpenAIChatRequest(
            model,
            BuildMessages(
                prompt,
                BuildSystemPrompt(systemPrompt, model, tools, requestContext),
                contextMessages,
                requestContext,
                supportedImageInputs),
            options.Temperature,
            options.MaxTokens,
            tools);

        return await chatClient.GetResponseAsync(chatRequest, cancellationToken);
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
        string prompt,
        string? systemPrompt,
        IReadOnlyList<OpenAIContextMessage>? contextMessages,
        OpenAIRequestContext? requestContext,
        IReadOnlyList<OpenAIImageInput>? imageInputs)
    {
        var messages = new List<OpenAIChatMessage>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new OpenAIChatMessage("system", systemPrompt));
        }

        if (contextMessages is { Count: > 0 })
        {
            messages.Add(new OpenAIChatMessage(
                "user",
                string.Join(
                    Environment.NewLine,
                    ["Recent Discord channel messages:", .. contextMessages.Select(FormatContextMessage)])));
        }

        messages.Add(new OpenAIChatMessage(
            "user",
            BuildUserMessageContent(FormatCurrentMessage(prompt, requestContext), imageInputs)));
        return messages;
    }

    private static string BuildSystemPrompt(
        string systemPrompt,
        string model,
        IReadOnlyList<OpenAIChatTool>? tools,
        OpenAIRequestContext? requestContext)
    {
        var contextLines = new List<string>
        {
            string.Empty,
            "Runtime context:",
            $"- Model: {model}",
            $"- Current UTC time: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}",
            $"- Available tools: {FormatAvailableTools(tools)}",
        };

        if (!string.IsNullOrWhiteSpace(requestContext?.ChannelName))
        {
            contextLines.Add($"- Discord channel: #{requestContext.ChannelName}");
        }

        return systemPrompt.TrimEnd() + Environment.NewLine + string.Join(Environment.NewLine, contextLines);
    }

    private static string FormatContextMessage(OpenAIContextMessage message) =>
        $"[{message.SentAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss 'UTC'}] {message.AuthorName}: {message.Content}";

    private static string FormatCurrentMessage(string prompt, OpenAIRequestContext? requestContext)
    {
        if (string.IsNullOrWhiteSpace(requestContext?.AuthorName))
        {
            return prompt;
        }

        return string.Join(
            Environment.NewLine,
            "Discord message that triggered this response:",
            $"{requestContext.AuthorName}: {prompt}");
    }

    private static IReadOnlyList<OpenAIChatTool>? BuildTools(IReadOnlyList<OpenAIChatToolOptions>? toolOptions)
    {
        var tools = toolOptions?
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Type))
            .Select(tool => new OpenAIChatTool(tool.Type!))
            .ToArray();

        return tools is { Length: > 0 } ? tools : null;
    }

    private static string FormatAvailableTools(IReadOnlyList<OpenAIChatTool>? tools) =>
        tools is { Count: > 0 }
            ? string.Join(", ", tools.Select(tool => tool.Type))
            : "none";

    private static object BuildUserMessageContent(
        string prompt,
        IReadOnlyList<OpenAIImageInput>? imageInputs)
    {
        var validImageInputs = imageInputs?
            .Where(imageInput => !string.IsNullOrWhiteSpace(imageInput.Url))
            .ToArray();

        if (validImageInputs is not { Length: > 0 })
        {
            return prompt;
        }

        var contentParts = new List<OpenAIChatContentPart> { new("text", Text: prompt) };

        for (var i = 0; i < validImageInputs.Length; i++)
        {
            var imageInput = validImageInputs[i];
            contentParts.Add(new OpenAIChatContentPart("text", Text: FormatImageInputContext(imageInput, i + 1)));
            contentParts.Add(new OpenAIChatContentPart(
                "image_url",
                ImageUrl: new OpenAIChatImageUrl(imageInput.Url)));
        }

        return contentParts;
    }

    private static string FormatImageInputContext(OpenAIImageInput imageInput, int index)
    {
        var lines = new List<string>
        {
            $"Image {index} is attached to this Discord message:",
            $"[{imageInput.SentAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss 'UTC'}] {imageInput.AuthorName}: {FormatImageMessageContent(imageInput.MessageContent)}",
        };

        if (!string.IsNullOrWhiteSpace(imageInput.FileName))
        {
            lines.Add($"Attachment filename: {imageInput.FileName}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatImageMessageContent(string? messageContent) =>
        string.IsNullOrWhiteSpace(messageContent) ? "(no message text)" : messageContent;

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

    private sealed record OpenAIContextMessage(DateTimeOffset SentAt, string AuthorName, string Content);

    private sealed record OpenAIRequestContext(
        string? GuildName,
        ulong ChannelId,
        string? ChannelName,
        string? ChannelTopic,
        string AuthorName);

    private sealed record OpenAIImageInput(
        string Url,
        DateTimeOffset SentAt,
        string AuthorName,
        string? MessageContent,
        string? FileName);
}
