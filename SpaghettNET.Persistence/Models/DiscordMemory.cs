namespace SpaghettNET.Persistence.Models;

public sealed class DiscordMemory : Entity<Guid>
{
    public required ulong GuildId { get; set; }
    public required ulong ChannelId { get; set; }
    public required string ChannelName { get; set; }
    public required ulong MessageId { get; set; }
    public required ulong AuthorId { get; set; }
    public required string AuthorName { get; set; }
    public required string Content { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
}
