namespace SpaghettNET.Persistence.Models;

public sealed class DiscordMemoryBackfillCursor : Entity<ulong>
{
    public required ulong GuildId { get; set; }
    public required ulong BeforeMessageId { get; set; }
    public required bool IsComplete { get; set; }
    public required int EvaluationVersion { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
