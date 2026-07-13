using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SpaghettNET.Bot.Services;

public sealed class OpenAIChatClient(
    HttpClient httpClient,
    OpenAIOptions options,
    ILogger<OpenAIChatClient> logger)
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal async Task<string?> GetResponseAsync(
        OpenAIChatRequest chatRequest,
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

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice> Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatCompletionResponseMessage Message);

    private sealed record ChatCompletionResponseMessage(
        [property: JsonPropertyName("content")] string? Content);
}

internal sealed record OpenAIChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OpenAIChatMessage> Messages,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    [property: JsonPropertyName("tools")] IReadOnlyList<OpenAIChatTool>? Tools);

internal sealed record OpenAIChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] object Content);

internal sealed record OpenAIChatContentPart(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("image_url")] OpenAIChatImageUrl? ImageUrl = null);

internal sealed record OpenAIChatImageUrl(
    [property: JsonPropertyName("url")] string Url);

internal sealed record OpenAIChatTool(
    [property: JsonPropertyName("type")] string Type);
