namespace SpaghettNET.Persistence.Models;

/// <summary>Runs <see cref="Action"/> on messages that match every condition that is set.</summary>
public sealed class MessageRule
{
    public int Id { get; set; }
    public required ulong GuildId { get; set; }

    public ulong? UserId { get; set; }
    public ulong? ChannelId { get; set; }
    /// <summary>Case-insensitive regex matched against the message content.</summary>
    public string? Pattern { get; set; }
    /// <summary>Probability from 0 to 1 that the rule fires when everything else matches.</summary>
    public double? Chance { get; set; }

    public required MessageRuleAction Action { get; set; }
    /// <summary>Reply text, reaction emoji, or the instruction for an LLM reply, depending on <see cref="Action"/>.</summary>
    public string? Text { get; set; }
    public int? TimeoutSeconds { get; set; }

    public required ulong CreatedById { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public enum MessageRuleAction
{
    Reply,
    React,
    Delete,
    Timeout,
    LlmReply,
}
