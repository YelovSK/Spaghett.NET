using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bot.API.Services;

public class OpenAIChatService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<OpenAIChatService> logger)
{
    public bool IsEnabled
    {
        get
        {
            var options = configuration.GetSection("OpenAI").Get<OpenAIOptions>();
            return !string.IsNullOrWhiteSpace(options?.BaseUrl) &&
                   !string.IsNullOrWhiteSpace(options.Model) &&
                   !string.IsNullOrWhiteSpace(options.SystemPromptPath);
        }
    }

    public async Task<string?> GetResponseAsync(
        string prompt,
        IReadOnlyList<OpenAIContextMessage>? contextMessages = null,
        CancellationToken cancellationToken = default)
    {
        var options = configuration.GetSection("OpenAI").Get<OpenAIOptions>() ?? new OpenAIOptions();
        if (string.IsNullOrWhiteSpace(options.BaseUrl) ||
            string.IsNullOrWhiteSpace(options.Model) ||
            string.IsNullOrWhiteSpace(options.SystemPromptPath))
        {
            logger.LogInformation("OpenAI-compatible chat is not configured. Set OpenAI:BaseUrl, OpenAI:Model, and OpenAI:SystemPromptPath to enable it.");
            return null;
        }

        var systemPrompt = await ReadSystemPromptAsync(options.SystemPromptPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            return null;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{NormalizeBaseUrl(options.BaseUrl).TrimEnd('/')}/chat/completions"))
        {
            Content = JsonContent.Create(new ChatCompletionRequest(
                options.Model,
                BuildMessages(prompt, systemPrompt, contextMessages),
                options.Temperature,
                options.MaxTokens)),
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
                logger.LogWarning("OpenAI-compatible chat request failed with status code {StatusCode}.", response.StatusCode);
                return null;
            }

            var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken);
            var content = completion?.Choices.FirstOrDefault()?.Message.Content?.Trim();

            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogWarning("OpenAI-compatible chat returned no message content.");
            }

            return content;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
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
        IReadOnlyList<OpenAIContextMessage>? contextMessages)
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

        messages.Add(new ChatMessage("user", prompt));
        return messages;
    }

    private sealed class OpenAIOptions
    {
        public string? BaseUrl { get; init; }
        public string? ApiKey { get; init; }
        public string? Model { get; init; }
        public string? SystemPromptPath { get; init; }
        public double? Temperature { get; init; }
        public int? MaxTokens { get; init; }
    }

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double? Temperature,
        [property: JsonPropertyName("max_tokens")] int? MaxTokens);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice> Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);

    private static string FormatContextMessage(OpenAIContextMessage message) =>
        $"[{message.SentAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss 'UTC'}] {message.AuthorName}: {message.Content}";
}

public sealed record OpenAIContextMessage(DateTimeOffset SentAt, string AuthorName, string Content);
