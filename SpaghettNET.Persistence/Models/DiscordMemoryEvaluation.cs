namespace SpaghettNET.Persistence.Models;

public sealed class DiscordMemoryEvaluation : Entity<ulong>
{
    public required ulong GuildId { get; set; }
    public required ulong ChannelId { get; set; }
    public required int EvaluationVersion { get; set; }
    public required bool WasStored { get; set; }
    public required DateTimeOffset EvaluatedAt { get; set; }
}
