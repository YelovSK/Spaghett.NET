using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SpaghettNET.Bot.Services;

public sealed class OpenRouterManagementClient(
    HttpClient httpClient,
    OpenAIOptions options,
    ILogger<OpenRouterManagementClient> logger)
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public bool CanHandleConfiguredProvider()
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return false;
        }

        return Uri.TryCreate(NormalizeBaseUrl(options.BaseUrl), UriKind.Absolute, out var uri) &&
               (uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<decimal?> GetRemainingCreditsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanHandleConfiguredProvider() || string.IsNullOrWhiteSpace(options.ManagementApiKey))
        {
            return null;
        }

        var creditsUri = new Uri($"{NormalizeBaseUrl(options.BaseUrl!).TrimEnd('/')}/credits");
        using var request = new HttpRequestMessage(HttpMethod.Get, creditsUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ManagementApiKey);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "OpenRouter credits request failed with status code {StatusCode}. Response body: {ResponseBody}",
                    response.StatusCode,
                    responseBody);
                return null;
            }

            var credits = await response.Content.ReadFromJsonAsync<OpenRouterCreditsResponse>(
                JsonSerializerOptions,
                cancellationToken);

            return credits?.Data is { } data
                ? data.TotalCredits - data.TotalUsage
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "OpenRouter credits request failed.");
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

    private sealed record OpenRouterCreditsResponse(
        [property: JsonPropertyName("data")] OpenRouterCreditsData? Data);

    private sealed record OpenRouterCreditsData(
        [property: JsonPropertyName("total_credits")] decimal TotalCredits,
        [property: JsonPropertyName("total_usage")] decimal TotalUsage);
}
