namespace SpaghettNET.Bot.Services.Chat;

public sealed class OpenAIChatRuntimeSettings(OpenAIOptions options)
{
    public string? Model { get; private set; } = options.Model;
    public int ContextCharacterBudget { get; } = Math.Max(4, options.ContextCharacterBudget ?? 32_000);
    public int MemoryCharacterBudget { get; } = Math.Max(4, options.MemoryCharacterBudget ?? 60_000);

    public void SetModel(string model) => Model = model;
    public void ResetModel() => Model = options.Model;
}
