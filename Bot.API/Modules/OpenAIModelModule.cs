using System.Globalization;
using Bot.API.Services;
using NetCord.Services.ApplicationCommands;

namespace Bot.API.Modules;

[SlashCommand("model", "Shows or changes the LLM model")]
public class OpenAIModelModule(
    OpenAIChatRuntimeSettings runtimeSettings,
    OpenAIOptions options,
    ModelMetadataResolver modelMetadataResolver,
    OpenRouterManagementClient creditsClient) : ApplicationCommandModule<ApplicationCommandContext>
{
    private const int EstimatedInputTokensPerResponse = 2_000;
    private const int EstimatedOutputTokensPerResponse = 500;
    private const decimal TokensPerMillion = 1_000_000m;

    [SubSlashCommand("show", "Shows the current LLM model")]
    public string Show()
    {
        return runtimeSettings.Model ?? "No model is selected";
    }

    [SubSlashCommand("set", "Changes the LLM model until the bot restarts")]
    public async Task<string> Set(
        [SlashCommandParameter(Description = "The model identifier to use")]
        string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "Model cannot be empty.";
        }

        var requestedModel = model.Trim();
        var request = new ModelMetadataRequest(options.BaseUrl, options.ApiKey, requestedModel);
        if (!modelMetadataResolver.CanHandle(request))
        {
            runtimeSettings.SetModel(requestedModel);
            return $"Model changed to {requestedModel}. Could not validate it because the configured provider does not expose model metadata.";
        }

        var metadata = await modelMetadataResolver.GetModelMetadataAsync(request);
        if (metadata is null)
        {
            return $"Could not find model {requestedModel}. Model was not changed.";
        }

        runtimeSettings.SetModel(metadata.Id);
        return FormatModelChanged(metadata);
    }

    [SubSlashCommand("reset", "Resets the LLM model to the configured default")]
    public string Reset()
    {
        runtimeSettings.ResetModel();

        if (string.IsNullOrWhiteSpace(runtimeSettings.Model))
        {
            return "Model reset. No default model is configured, so chat is disabled until a model is set.";
        }

        return $"Model reset to {runtimeSettings.Model}.";
    }

    [SubSlashCommand("balance", "Shows remaining OpenRouter credit and estimated responses")]
    public async Task<string> Balance()
    {
        if (!creditsClient.CanHandleConfiguredProvider())
        {
            return "The configured OpenAI-compatible provider is not OpenRouter.";
        }

        if (string.IsNullOrWhiteSpace(options.ManagementApiKey))
        {
            return "OpenRouter management API key is not configured.";
        }

        var remainingCredits = await creditsClient.GetRemainingCreditsAsync();
        if (remainingCredits is null)
        {
            return "Could not get the remaining OpenRouter credit.";
        }

        var lines = new List<string>
        {
            $"Remaining OpenRouter credit: {FormatCredit(remainingCredits.Value)}",
        };

        var model = runtimeSettings.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            lines.Add("Could not estimate responses because no model is selected.");
            return string.Join(Environment.NewLine, lines);
        }

        var metadata = await modelMetadataResolver.GetModelMetadataAsync(
            new ModelMetadataRequest(options.BaseUrl, options.ApiKey, model));
        if (metadata?.InputPricePerMillionTokens is not { } inputPrice ||
            metadata.OutputPricePerMillionTokens is not { } outputPrice)
        {
            lines.Add($"Could not estimate responses because pricing for {model} is unavailable.");
            return string.Join(Environment.NewLine, lines);
        }

        var estimatedResponseCost =
            inputPrice * EstimatedInputTokensPerResponse / TokensPerMillion +
            outputPrice * EstimatedOutputTokensPerResponse / TokensPerMillion;

        if (estimatedResponseCost == 0)
        {
            lines.Add($"Estimated responses with {model}: credit is not the limiting factor because the model is free.");
            return string.Join(Environment.NewLine, lines);
        }

        var estimatedResponses = decimal.Floor(Math.Max(0, remainingCredits.Value) / estimatedResponseCost);
        lines.Add($"Estimated ~{estimatedResponses.ToString("N0", CultureInfo.InvariantCulture)} responses with {model}.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatModelChanged(ModelMetadata metadata)
    {
        var lines = new List<string> { $"Model changed to {metadata.Id}." };

        if (!string.IsNullOrWhiteSpace(metadata.Name) &&
            !metadata.Name.Equals(metadata.Id, StringComparison.InvariantCultureIgnoreCase))
        {
            lines.Add($"Name: {metadata.Name}");
        }

        if (metadata.ContextLength is { } contextLength)
        {
            lines.Add($"Context: {contextLength.ToString("N0", CultureInfo.InvariantCulture)} tokens");
        }

        if (metadata.InputPricePerMillionTokens is { } inputPrice)
        {
            lines.Add($"Input: {FormatPrice(inputPrice)} / 1M tokens");
        }

        if (metadata.OutputPricePerMillionTokens is { } outputPrice)
        {
            lines.Add($"Output: {FormatPrice(outputPrice)} / 1M tokens");
        }

        lines.Add($"Vision: {(metadata.Capabilities.Contains(ModelCapability.Vision) ? "yes" : "no")}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatPrice(decimal price) => price == 0
        ? "$0"
        : $"${price.ToString("0.######", CultureInfo.InvariantCulture)}";

    private static string FormatCredit(decimal credit) =>
        $"${credit.ToString("0.00##", CultureInfo.InvariantCulture)}";
}
