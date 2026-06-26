using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Bot.API.Services;

public sealed class OpenRouterModelCapabilityProvider(
    HttpClient httpClient,
    ILogger<OpenRouterModelCapabilityProvider> logger)
    : IOpenAIModelCapabilityProvider
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, CacheEntry> _capabilitiesByCacheKey = new(StringComparer.InvariantCultureIgnoreCase);

    public bool CanHandle(OpenAIModelCapabilityRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return false;
        }

        return Uri.TryCreate(NormalizeBaseUrl(request.BaseUrl), UriKind.Absolute, out var uri) &&
               (uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlySet<OpenAIModelCapability>> GetCapabilitiesAsync(
        OpenAIModelCapabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl) || string.IsNullOrWhiteSpace(request.Model))
        {
            return new HashSet<OpenAIModelCapability>();
        }

        var cacheKey = BuildCacheKey(request.BaseUrl, request.Model);
        var now = DateTimeOffset.UtcNow;

        if (_capabilitiesByCacheKey.TryGetValue(cacheKey, out var cachedCapabilities) &&
            cachedCapabilities.ExpiresAt > now)
        {
            return cachedCapabilities.Capabilities;
        }

        var modelMetadata = await GetModelMetadataAsync(request, cancellationToken);
        if (modelMetadata?.Data is null)
        {
            return new HashSet<OpenAIModelCapability>();
        }

        var capabilities = GetCapabilities(modelMetadata.Data, request.Model);
        _capabilitiesByCacheKey[cacheKey] = new CacheEntry(capabilities, now.Add(CacheDuration));

        return capabilities;
    }

    private async Task<OpenRouterModelResponse?> GetModelMetadataAsync(
        OpenAIModelCapabilityRequest request,
        CancellationToken cancellationToken)
    {
        var modelUri = new Uri($"{NormalizeBaseUrl(request.BaseUrl!).TrimEnd('/')}/model/{request.Model}");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, modelUri);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        }

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "OpenRouter model metadata request failed with status code {StatusCode}. Response body: {ResponseBody}",
                response.StatusCode,
                responseBody);

            return null;
        }

        return await response.Content.ReadFromJsonAsync<OpenRouterModelResponse>(
            JsonSerializerOptions,
            cancellationToken);
    }

    private IReadOnlySet<OpenAIModelCapability> GetCapabilities(OpenRouterModelData modelData, string model)
    {
        var inputModalities = modelData.Architecture?.InputModalities;
        if (inputModalities?.Contains("image", StringComparer.InvariantCultureIgnoreCase) != true)
        {
            logger.LogInformation(
                "OpenRouter model {Model} image input support: false. Input modalities: {InputModalities}",
                model,
                inputModalities is { Count: > 0 } ? string.Join(", ", inputModalities) : "unknown");

            return new HashSet<OpenAIModelCapability>();
        }

        logger.LogInformation(
            "OpenRouter model {Model} image input support: true. Input modalities: {InputModalities}",
            model,
            string.Join(", ", inputModalities));

        return new HashSet<OpenAIModelCapability> { OpenAIModelCapability.Vision };
    }

    private static string BuildCacheKey(string baseUrl, string model) =>
        $"openrouter:{NormalizeBaseUrl(baseUrl).TrimEnd('/')}:{model}";

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmedBaseUrl = baseUrl.TrimEnd('/');

        if (trimmedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmedBaseUrl;
        }

        return trimmedBaseUrl + "/v1";
    }

    private sealed record CacheEntry(
        IReadOnlySet<OpenAIModelCapability> Capabilities,
        DateTimeOffset ExpiresAt);

    private sealed record OpenRouterModelResponse(
        [property: JsonPropertyName("data")] OpenRouterModelData? Data);

    private sealed record OpenRouterModelData(
        [property: JsonPropertyName("architecture")] OpenRouterModelArchitecture? Architecture);

    private sealed record OpenRouterModelArchitecture(
        [property: JsonPropertyName("input_modalities")] IReadOnlyList<string>? InputModalities);
}
