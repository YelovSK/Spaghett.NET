namespace SpaghettNET.Persistence.Models;

/// <summary>JavaScript written by the LLM that registers handlers for Discord events with on(event, handler).</summary>
public sealed class BotScript
{
    public int Id { get; set; }
    public required ulong GuildId { get; set; }
    public required string Description { get; set; }
    public required string Code { get; set; }
    /// <summary>JSON object behind the script's api.store.</summary>
    public required string State { get; set; }

    public required ulong CreatedById { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Turned off by hand, as opposed to stopped by <see cref="FailureCount"/>.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Consecutive failed runs. The script stops running once this hits the limit.</summary>
    public int FailureCount { get; set; }
    public string? LastError { get; set; }
}
