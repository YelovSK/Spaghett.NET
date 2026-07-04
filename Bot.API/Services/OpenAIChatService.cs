using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Bot.API.Services;

public class OpenAIChatService(
    HttpClient httpClient,
    OpenAIOptions options,
    OpenAIChatRuntimeSettings runtimeSettings,
    ModelMetadataResolver modelCapabilityResolver,
    ILogger<OpenAIChatService> logger)
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool IsEnabled
    {
        get
        {
            return !string.IsNullOrWhiteSpace(options.BaseUrl) &&
                   !string.IsNullOrWhiteSpace(runtimeSettings.Model) &&
                   !string.IsNullOrWhiteSpace(options.SystemPromptPath);
        }
    }

    public async Task<string?> GetResponseAsync(
        string prompt,
        IReadOnlyList<OpenAIContextMessage>? contextMessages = null,
        OpenAIRequestContext? requestContext = null,
        IReadOnlyList<OpenAIImageInput>? imageInputs = null,
        CancellationToken cancellationToken = default)
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

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{NormalizeBaseUrl(options.BaseUrl).TrimEnd('/')}/chat/completions"))
        {
            Content = JsonContent.Create(new ChatCompletionRequest(
                model,
                BuildMessages(prompt, BuildSystemPrompt(systemPrompt, model, tools, requestContext), contextMessages, supportedImageInputs),
                options.Temperature,
                options.MaxTokens,
                tools),
                options: JsonSerializerOptions),
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "OpenAI-compatible chat request failed with status code {StatusCode}. Response body: {ResponseBody}",
                    response.StatusCode,
                    responseBody);
                return null;
            }

            var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
                JsonSerializerOptions,
                cancellationToken);
            var content = completion?.Choices?.FirstOrDefault()?.Message.Content?.Trim();

            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogWarning(
                    "OpenAI-compatible chat returned no message content. Choices: {ChoiceCount}.",
                    completion?.Choices.Count ?? 0);
            }

            return content;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "OpenAI-compatible chat request failed.");
            return null;
        }
        catch (TaskCanceledException ex)
        {
            logger.LogWarning(ex, "OpenAI-compatible chat request timed out or was canceled.");
            return null;
        }
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmedBaseUrl = baseUrl.TrimEnd('/');

        if (trimmedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmedBaseUrl;
        }

        return trimmedBaseUrl + "/v1";
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

        var supportsVision = await modelCapabilityResolver.SupportsAsync(
            new ModelMetadataRequest(options.BaseUrl, options.ApiKey, model),
            ModelCapability.Vision,
            cancellationToken);

        if (supportsVision)
        {
            return imageInputs;
        }

        logger.LogInformation("Skipping image input because model {Model} does not have the {Capability} capability.", model, ModelCapability.Vision);
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

    private string ResolveConfiguredPath(string configuredPath)
    {
        if (Path.IsPathFullyQualified(configuredPath))
        {
            return configuredPath;
        }

        return Path.GetFullPath(configuredPath);
    }

    private static IReadOnlyList<ChatMessage> BuildMessages(
        string prompt,
        string? systemPrompt,
        IReadOnlyList<OpenAIContextMessage>? contextMessages,
        IReadOnlyList<OpenAIImageInput>? imageInputs)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new ChatMessage("system", systemPrompt));
        }

        if (contextMessages is { Count: > 0 })
        {
            messages.Add(new ChatMessage(
                "user",
                string.Join(
                    Environment.NewLine,
                    ["Recent Discord messages before the user's question:", .. contextMessages.Select(FormatContextMessage)])));
        }

        messages.Add(new ChatMessage("user", BuildUserMessageContent(prompt, imageInputs)));
        return messages;
    }

    private static string BuildSystemPrompt(
        string systemPrompt,
        string model,
        IReadOnlyList<ChatTool>? tools,
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

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double? Temperature,
        [property: JsonPropertyName("max_tokens")] int? MaxTokens,
        [property: JsonPropertyName("tools")] IReadOnlyList<ChatTool>? Tools);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] object Content);

    private sealed record ChatContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text = null,
        [property: JsonPropertyName("image_url")] ChatImageUrl? ImageUrl = null);

    private sealed record ChatImageUrl(
        [property: JsonPropertyName("url")] string Url);

    private sealed record ChatTool(
        [property: JsonPropertyName("type")] string Type);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice> Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatCompletionResponseMessage Message);

    private sealed record ChatCompletionResponseMessage(
        [property: JsonPropertyName("content")] string? Content);

    private static string FormatContextMessage(OpenAIContextMessage message) =>
        $"[{message.SentAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss 'UTC'}] {message.AuthorName}: {message.Content}";

    private static IReadOnlyList<ChatTool>? BuildTools(IReadOnlyList<OpenAIChatToolOptions>? toolOptions)
    {
        var tools = toolOptions?
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Type))
            .Select(tool => new ChatTool(tool.Type!))
            .ToArray();

        return tools is { Length: > 0 } ? tools : null;
    }

    private static string FormatAvailableTools(IReadOnlyList<ChatTool>? tools) =>
        tools is { Count: > 0 }
            ? string.Join(", ", tools.Select(tool => tool.Type))
            : "none";

    private static object BuildUserMessageContent(string prompt, IReadOnlyList<OpenAIImageInput>? imageInputs)
    {
        var validImageInputs = imageInputs?
            .Where(imageInput => !string.IsNullOrWhiteSpace(imageInput.Url))
            .ToArray();

        if (validImageInputs is not { Length: > 0 })
        {
            return prompt;
        }

        var contentParts = new List<ChatContentPart> { new("text", Text: prompt) };

        for (var i = 0; i < validImageInputs.Length; i++)
        {
            var imageInput = validImageInputs[i];
            contentParts.Add(new ChatContentPart("text", Text: FormatImageInputContext(imageInput, i + 1)));
            contentParts.Add(new ChatContentPart("image_url", ImageUrl: new ChatImageUrl(imageInput.Url)));
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
}

public sealed record OpenAIContextMessage(DateTimeOffset SentAt, string AuthorName, string Content);

public sealed record OpenAIRequestContext(
    string? GuildName,
    ulong ChannelId,
    string? ChannelName,
    string? ChannelTopic,
    string AuthorName);

public sealed record OpenAIImageInput(
    string Url,
    DateTimeOffset SentAt,
    string AuthorName,
    string? MessageContent,
    string? FileName);
