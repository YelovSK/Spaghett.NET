using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SpaghettNET.Bot.Services.Chat;

public sealed class OpenAIChatClient(
    HttpClient httpClient,
    OpenAIOptions options,
    ILogger<OpenAIChatClient> logger)
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal async Task<OpenAIChatMessage?> GetResponseAsync(
        OpenAIChatRequest chatRequest,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{NormalizeBaseUrl(options.BaseUrl!).TrimEnd('/')}/chat/completions"))
        {
            Content = JsonContent.Create(chatRequest, options: JsonSerializerOptions),
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        request.Headers.Add("x-session-id", sessionId);

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

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
                        "Failed OpenAI-compatible chat request body: {RequestBody}",
                        await request.Content!.ReadAsStringAsync(cancellationToken));
                }

                return null;
            }

            var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
                JsonSerializerOptions,
                cancellationToken);
            var message = completion?.Choices?.FirstOrDefault()?.Message;

            if (message is null ||
                (string.IsNullOrWhiteSpace(message.Content) && message.ToolCalls is not { Count: > 0 }))
            {
                logger.LogWarning(
                    "OpenAI-compatible chat returned no message content. Choices: {ChoiceCount}.",
                    completion?.Choices.Count ?? 0);
                return null;
            }

            // Unknown fields like reasoning_details must be echoed back on tool-call turns, some providers reject the follow-up otherwise.
            return new OpenAIChatMessage("assistant", message.Content, message.ToolCalls)
            {
                AdditionalProperties = message.AdditionalProperties,
            };
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

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice> Choices,
        [property: JsonPropertyName("usage")] ChatCompletionUsage? Usage);

    private sealed record ChatCompletionUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("prompt_tokens_details")] PromptTokensDetails? PromptTokensDetails);

    private sealed record PromptTokensDetails(
        [property: JsonPropertyName("cached_tokens")] int CachedTokens,
        [property: JsonPropertyName("cache_write_tokens")] int CacheWriteTokens);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatCompletionResponseMessage Message);

    private sealed record ChatCompletionResponseMessage(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<OpenAIToolCall>? ToolCalls)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }
}

internal sealed record OpenAIChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OpenAIChatMessage> Messages,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    [property: JsonPropertyName("tools")] IReadOnlyList<OpenAIChatTool>? Tools,
    [property: JsonPropertyName("tool_choice")] string? ToolChoice = null);

internal sealed record OpenAIChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] object? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<OpenAIToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

internal sealed record OpenAIToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OpenAIToolCallFunction Function);

internal sealed record OpenAIToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string? Arguments);

internal sealed record OpenAIChatContentPart(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("image_url")] OpenAIChatImageUrl? ImageUrl = null,
    [property: JsonPropertyName("cache_control")] OpenAICacheControl? CacheControl = null);

internal sealed record OpenAICacheControl(
    [property: JsonPropertyName("type")] string Type);

internal sealed record OpenAIChatImageUrl(
    [property: JsonPropertyName("url")] string Url);

internal sealed record OpenAIChatTool(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OpenAIChatFunction? Function = null);

internal sealed record OpenAIChatFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] object Parameters);
