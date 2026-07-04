namespace Bot.API.Services;

public sealed class OpenAIChatRuntimeSettings(OpenAIOptions options)
{
    public string? Model { get; private set; } = options.Model;
    public int ContextMessageCount { get; private set; } = options.ContextMessageCount ?? 10;
    public int ContextImagesCount { get; private set; } = 4;

    public void SetModel(string model) => Model = model;
    public void ResetModel() => Model = options.Model;

    public void SetContextMessageCount(int count) => ContextMessageCount = Math.Max(0, count);
    public void SetContextImagesCount(int count) => ContextImagesCount = Math.Max(0, count);
}
