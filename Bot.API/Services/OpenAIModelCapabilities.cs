using Microsoft.Extensions.Logging;

namespace Bot.API.Services;

public enum OpenAIModelCapability
{
    Vision,
}

public sealed record OpenAIModelCapabilityRequest(
    string? BaseUrl,
    string? ApiKey,
    string Model);

public interface IOpenAIModelCapabilityProvider
{
    bool CanHandle(OpenAIModelCapabilityRequest request);

    Task<IReadOnlySet<OpenAIModelCapability>> GetCapabilitiesAsync(
        OpenAIModelCapabilityRequest request,
        CancellationToken cancellationToken = default);
}

public interface IOpenAIModelCapabilityResolver
{
    Task<bool> SupportsAsync(
        OpenAIModelCapabilityRequest request,
        OpenAIModelCapability capability,
        CancellationToken cancellationToken = default);
}

public sealed class OpenAIModelCapabilityResolver(
    IEnumerable<IOpenAIModelCapabilityProvider> providers,
    ILogger<OpenAIModelCapabilityResolver> logger)
    : IOpenAIModelCapabilityResolver
{
    public async Task<bool> SupportsAsync(
        OpenAIModelCapabilityRequest request,
        OpenAIModelCapability capability,
        CancellationToken cancellationToken = default)
    {
        var provider = providers.FirstOrDefault(provider => provider.CanHandle(request));
        if (provider is null)
        {
            return false;
        }

        try
        {
            var capabilities = await provider.GetCapabilitiesAsync(request, cancellationToken);
            return capabilities.Contains(capability);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to resolve model capabilities for model {Model}. Treating capability {Capability} as unsupported.",
                request.Model,
                capability);

            return false;
        }
    }
}
