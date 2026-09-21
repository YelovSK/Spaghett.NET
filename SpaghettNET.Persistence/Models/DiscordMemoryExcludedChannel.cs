namespace SpaghettNET.Persistence.Models;

/// <summary>A channel whose messages are neither remembered nor recalled. Id is the channel id.</summary>
public sealed class DiscordMemoryExcludedChannel : Entity<ulong>
{
    public required ulong GuildId { get; set; }
    public required string ChannelName { get; set; }
    public required DateTimeOffset ExcludedAt { get; set; }
}
