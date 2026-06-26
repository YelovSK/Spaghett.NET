namespace Bot.API.Services;

public sealed class OpenAIOptions
{
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? Model { get; init; }
    public string? SystemPromptPath { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public int? ContextMessageCount { get; init; }
    public IReadOnlyList<OpenAIChatToolOptions>? Tools { get; init; }
}

public sealed class OpenAIChatToolOptions
{
    public string? Type { get; init; }
}
