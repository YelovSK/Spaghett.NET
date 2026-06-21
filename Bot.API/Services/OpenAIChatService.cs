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
                   !string.IsNullOrWhiteSpace(options.Model);
        }
    }

    public async Task<string?> GetResponseAsync(
        string prompt,
        IReadOnlyList<OpenAIContextMessage>? contextMessages = null,
        IReadOnlyList<OpenAIEmote>? emotes = null,
        CancellationToken cancellationToken = default)
    {
        var options = configuration.GetSection("OpenAI").Get<OpenAIOptions>() ?? new OpenAIOptions();
        if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.Model))
        {
            logger.LogInformation("OpenAI-compatible chat is not configured. Set OpenAI:BaseUrl and OpenAI:Model to enable it.");
            return null;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{NormalizeBaseUrl(options.BaseUrl).TrimEnd('/')}/chat/completions"))
        {
            Content = JsonContent.Create(new ChatCompletionRequest(
                options.Model,
                BuildMessages(prompt, options.SystemPrompt, contextMessages, emotes),
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

    private static IReadOnlyList<ChatMessage> BuildMessages(
        string prompt,
        string? systemPrompt,
        IReadOnlyList<OpenAIContextMessage>? contextMessages,
        IReadOnlyList<OpenAIEmote>? emotes)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new ChatMessage("system", systemPrompt));
        }

        if (emotes is { Count: > 0 })
        {
            messages.Add(new ChatMessage(
                "system",
                string.Join(
                    Environment.NewLine,
                    "Available server emotes. Prefer these over normal Unicode emojis when an emote fits.",
                    string.Join(", ", emotes.Select(emote => emote.Name).Distinct(StringComparer.InvariantCultureIgnoreCase)),
                    "To send a server emote, write only <emote_name>. For example, write <kekw>.")));
        }

        if (contextMessages is { Count: > 0 })
        {
            messages.Add(new ChatMessage(
                "user",
                string.Join(
                    Environment.NewLine,
                    ["Recent Discord messages before the user's question:", .. contextMessages.Select(message => $"{message.AuthorName}: {message.Content}")])));
        }

        messages.Add(new ChatMessage("user", prompt));
        return messages;
    }

    private sealed class OpenAIOptions
    {
        public string? BaseUrl { get; init; }
        public string? ApiKey { get; init; }
        public string? Model { get; init; }
        public string? SystemPrompt { get; init; }
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
}

public sealed record OpenAIContextMessage(string AuthorName, string Content);

public sealed record OpenAIEmote(string Name, string Token);
