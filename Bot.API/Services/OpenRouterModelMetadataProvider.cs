using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Bot.API.Services;

public sealed class OpenRouterModelMetadataProvider(
    HttpClient httpClient,
    ILogger<OpenRouterModelMetadataProvider> logger)
    : IModelMetadataProvider
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, CacheEntry> _metadataByCacheKey = new(StringComparer.InvariantCultureIgnoreCase);

    public bool CanHandle(ModelMetadataRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return false;
        }

        return Uri.TryCreate(NormalizeBaseUrl(request.BaseUrl), UriKind.Absolute, out var uri) &&
               (uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlySet<ModelCapability>> GetCapabilitiesAsync(
        ModelMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        var metadata = await GetModelMetadataAsync(request, cancellationToken);
        return metadata?.Capabilities ?? new HashSet<ModelCapability>();
    }

    public async Task<ModelMetadata?> GetModelMetadataAsync(
        ModelMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl) || string.IsNullOrWhiteSpace(request.Model))
        {
            return null;
        }

        var cacheKey = BuildCacheKey(request.BaseUrl, request.Model);
        var now = DateTimeOffset.UtcNow;

        if (_metadataByCacheKey.TryGetValue(cacheKey, out var cachedMetadata) &&
            cachedMetadata.ExpiresAt > now)
        {
            return cachedMetadata.Metadata;
        }

        var openRouterModel = await GetOpenRouterModelAsync(request, cancellationToken);
        if (openRouterModel?.Data is null)
        {
            return null;
        }

        var metadata = ToModelMetadata(openRouterModel.Data, request.Model);
        _metadataByCacheKey[cacheKey] = new CacheEntry(metadata, now.Add(CacheDuration));

        return metadata;
    }

    private async Task<OpenRouterModelResponse?> GetOpenRouterModelAsync(
        ModelMetadataRequest request,
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

    private ModelMetadata ToModelMetadata(OpenRouterModelData modelData, string model)
    {
        return new ModelMetadata(
            modelData.Id ?? model,
            modelData.Name,
            modelData.ContextLength,
            ParsePricePerMillionTokens(modelData.Pricing?.Prompt),
            ParsePricePerMillionTokens(modelData.Pricing?.Completion),
            GetCapabilities(modelData, model));
    }

    private IReadOnlySet<ModelCapability> GetCapabilities(OpenRouterModelData modelData, string model)
    {
        var inputModalities = modelData.Architecture?.InputModalities;
        if (inputModalities?.Contains("image", StringComparer.InvariantCultureIgnoreCase) != true)
        {
            logger.LogInformation(
                "OpenRouter model {Model} image input support: false. Input modalities: {InputModalities}",
                model,
                inputModalities is { Count: > 0 } ? string.Join(", ", inputModalities) : "unknown");

            return new HashSet<ModelCapability>();
        }

        logger.LogInformation(
            "OpenRouter model {Model} image input support: true. Input modalities: {InputModalities}",
            model,
            string.Join(", ", inputModalities));

        return new HashSet<ModelCapability> { ModelCapability.Vision };
    }

    private static string BuildCacheKey(string baseUrl, string model) =>
        $"openrouter:{NormalizeBaseUrl(baseUrl).TrimEnd('/')}:{model}";

    private static decimal? ParsePricePerMillionTokens(string? pricePerToken)
    {
        if (!decimal.TryParse(pricePerToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedPrice))
        {
            return null;
        }

        return parsedPrice * 1_000_000m;
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

    private sealed record CacheEntry(
        ModelMetadata Metadata,
        DateTimeOffset ExpiresAt);

    private sealed record OpenRouterModelResponse(
        [property: JsonPropertyName("data")] OpenRouterModelData? Data);

    private sealed record OpenRouterModelData(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("context_length")] int? ContextLength,
        [property: JsonPropertyName("pricing")] OpenRouterModelPricing? Pricing,
        [property: JsonPropertyName("architecture")] OpenRouterModelArchitecture? Architecture);

    private sealed record OpenRouterModelPricing(
        [property: JsonPropertyName("prompt")] string? Prompt,
        [property: JsonPropertyName("completion")] string? Completion);

    private sealed record OpenRouterModelArchitecture(
        [property: JsonPropertyName("input_modalities")] IReadOnlyList<string>? InputModalities);
}
