using DiscordMemory = SpaghettNET.Persistence.Models.DiscordMemory;

namespace SpaghettNET.Bot.Services;

internal static class DiscordMemoryShared
{
    internal const int MaxPoolSize = 2000;
    internal const int MaxChoiceOptions = ChoiceQuestion.MaxOptions - 1; // leaves room for NoOptionKey
    internal const string NoOptionKey = "none";

    internal static string FormatMemory(DiscordMemory memory) =>
        $"[{memory.CreatedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss 'UTC'}] {memory.AuthorName} in {memory.ChannelName}: {memory.Content}";

    internal static string Truncate(string value, int length)
    {
        var singleLine = value.ReplaceLineEndings(" ");
        return singleLine.Length <= length ? singleLine : singleLine[..length] + "...";
    }
}
