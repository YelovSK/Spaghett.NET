using Microsoft.Extensions.Logging;

namespace Bot.API.Services;

public enum ModelCapability
{
    Vision,
}

public sealed record ModelMetadata(
    string Id,
    string? Name,
    int? ContextLength,
    decimal? InputPricePerMillionTokens,
    decimal? OutputPricePerMillionTokens,
    IReadOnlySet<ModelCapability> Capabilities);

public sealed record ModelMetadataRequest(
    string? BaseUrl,
    string? ApiKey,
    string Model);

public interface IModelMetadataProvider
{
    bool CanHandle(ModelMetadataRequest request);

    Task<ModelMetadata?> GetModelMetadataAsync(
        ModelMetadataRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<ModelCapability>> GetCapabilitiesAsync(
        ModelMetadataRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ModelMetadataResolver(
    IEnumerable<IModelMetadataProvider> providers,
    ILogger<ModelMetadataResolver> logger)
{
    public bool CanHandle(ModelMetadataRequest request) => GetProvider(request) is not null;

    public async Task<ModelMetadata?> GetModelMetadataAsync(
        ModelMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(request);
        if (provider is null)
        {
            return null;
        }

        try
        {
            return await provider.GetModelMetadataAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve model metadata for model {Model}.", request.Model);
            return null;
        }
    }

    public async Task<bool> SupportsAsync(
        ModelMetadataRequest request,
        ModelCapability capability,
        CancellationToken cancellationToken = default)
    {
        var metadata = await GetModelMetadataAsync(request, cancellationToken);
        return metadata?.Capabilities.Contains(capability) == true;
    }

    private IModelMetadataProvider? GetProvider(ModelMetadataRequest request) =>
        providers.FirstOrDefault(provider => provider.CanHandle(request));
}
