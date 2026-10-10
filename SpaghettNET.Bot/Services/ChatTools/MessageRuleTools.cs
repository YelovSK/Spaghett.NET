using System.Text.Json;
using System.Text.RegularExpressions;
using NetCord;
using NetCord.Gateway;
using SpaghettNET.Persistence.Models;

namespace SpaghettNET.Bot.Services.ChatTools;

public sealed partial class CreateMessageRuleTool(
    GatewayClient gatewayClient,
    MessageRuleService ruleService) : IChatFunctionTool
{
    [GeneratedRegex(@"^<a?:(?<name>\w+):(?<id>\d+)>$|^[<:]?(?<name>\w+)[>:]?$")]
    private static partial Regex CustomEmojiRegex();

    public string Name => "create_message_rule";

    public string Description =>
        "Creates a persistent rule that runs an action on future messages in this server matching every condition given. " +
        "Only works when the person asking has the Manage Server permission. Give at least one condition.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                @enum = new[] { "reply", "react", "delete", "timeout", "llm_reply" },
                description = "reply: send fixed text. react: add an emoji. delete: delete the message. " +
                              "timeout: time out the author. llm_reply: you write a reply following an instruction.",
            },
            text = new
            {
                type = "string",
                description = "reply: the text. react: a unicode emoji or a server emote name. llm_reply: the instruction.",
            },
            timeout_seconds = new { type = "integer", description = "Required for timeout. Max 28 days." },
            user = new { type = "string", description = "Only messages from this member: their name, or id if you know it." },
            channel = new { type = "string", description = "Only messages in this channel: its name, or id if you know it." },
            pattern = new
            {
                type = "string",
                description = @"Case-insensitive .NET regex the message must match. Use \b for whole words.",
            },
            chance = new { type = "number", description = "Probability from 0 to 1 that the rule fires when matched." },
            expires_in_minutes = new { type = "integer", description = "Delete the rule after this long. Omit for permanent." },
        },
        required = new[] { "action" },
    };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (ChatToolHelper.Deny(message, Permissions.ManageGuild, "Manage Server") is { } denial)
        {
            return denial;
        }

        var action = ChatToolHelper.GetString(arguments, "action") switch
        {
            "reply" => MessageRuleAction.Reply,
            "react" => MessageRuleAction.React,
            "delete" => MessageRuleAction.Delete,
            "timeout" => MessageRuleAction.Timeout,
            "llm_reply" => MessageRuleAction.LlmReply,
            _ => (MessageRuleAction?)null,
        };
        if (action is null)
        {
            return "Unknown action.";
        }

        var text = ChatToolHelper.GetString(arguments, "text");
        var timeoutSeconds = ChatToolHelper.GetInt(arguments, "timeout_seconds");
        var pattern = ChatToolHelper.GetString(arguments, "pattern");
        var chance = ChatToolHelper.GetDouble(arguments, "chance");
        var expiresInMinutes = ChatToolHelper.GetInt(arguments, "expires_in_minutes");
        var userText = ChatToolHelper.GetString(arguments, "user");
        var channelText = ChatToolHelper.GetString(arguments, "channel");

        if (action is MessageRuleAction.Reply or MessageRuleAction.React or MessageRuleAction.LlmReply &&
            string.IsNullOrWhiteSpace(text))
        {
            return "This action needs text.";
        }

        if (action is MessageRuleAction.Timeout && timeoutSeconds is not (> 0 and <= ChatToolHelper.MaxTimeoutSeconds))
        {
            return $"timeout_seconds must be between 1 and {ChatToolHelper.MaxTimeoutSeconds}.";
        }

        if (chance is not null and (<= 0 or > 1))
        {
            return "chance must be above 0 and at most 1.";
        }

        if (expiresInMinutes is <= 0)
        {
            return "expires_in_minutes must be positive.";
        }

        if (userText is null && channelText is null && pattern is null && chance is null)
        {
            return "Give at least one condition (user, channel, pattern or chance).";
        }

        if (pattern is not null)
        {
            try
            {
                MessageRuleService.CreatePattern(pattern);
            }
            catch (ArgumentException ex)
            {
                return $"Invalid pattern: {ex.Message}";
            }
        }

        var guildId = message.GuildId!.Value;
        var (userId, userError) = userText is null
            ? (null, null)
            : await ChatToolHelper.ResolveUserAsync(gatewayClient, message, userText, cancellationToken);
        if (userError is not null)
        {
            return userError;
        }

        var (channelId, channelError) = channelText is null
            ? (null, null)
            : ChatToolHelper.ResolveChannel(message, channelText);
        if (channelError is not null)
        {
            return channelError;
        }

        if (action is MessageRuleAction.React)
        {
            text = await ResolveEmojiAsync(guildId, text!);
            if (text is null)
            {
                return "Unknown emoji. Use a unicode emoji or the name of one of this server's emotes.";
            }
        }

        var rule = new MessageRule
        {
            GuildId = guildId,
            UserId = userId,
            ChannelId = channelId,
            Pattern = pattern,
            Chance = chance,
            Action = action.Value,
            Text = action is MessageRuleAction.Delete or MessageRuleAction.Timeout ? null : text,
            TimeoutSeconds = action is MessageRuleAction.Timeout ? timeoutSeconds : null,
            CreatedById = message.Author.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresInMinutes is { } minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null,
        };
        await ruleService.AddAsync(rule);

        return $"Created {MessageRuleToolHelper.Describe(rule)}";
    }

    /// <returns>name:id for a server emote, the input for a unicode emoji, or null when it names no emote.</returns>
    private async Task<string?> ResolveEmojiAsync(ulong guildId, string emoji)
    {
        if (CustomEmojiRegex().Match(emoji.Trim()) is not { Success: true } match)
        {
            return emoji.Trim();
        }

        if (match.Groups["id"].Success)
        {
            return $"{match.Groups["name"].Value}:{match.Groups["id"].Value}";
        }

        var name = match.Groups["name"].Value;
        var emojis = await gatewayClient.Rest.GetGuildEmojisAsync(guildId);
        return emojis.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.InvariantCultureIgnoreCase)) is { } guildEmoji
            ? $"{guildEmoji.Name}:{guildEmoji.Id}"
            : null;
    }
}

public sealed class ListMessageRulesTool(MessageRuleService ruleService) : IChatFunctionTool
{
    public string Name => "list_message_rules";
    public string Description => "Lists the active message rules in this server.";
    public object Parameters => new { type = "object", properties = new { } };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (message.GuildId is not { } guildId)
        {
            return "Rules only exist in servers.";
        }

        var rules = await ruleService.GetRulesAsync(guildId);
        return rules.Count == 0
            ? "There are no rules."
            : string.Join(Environment.NewLine, rules.Select(MessageRuleToolHelper.Describe));
    }
}

public sealed class DeleteMessageRuleTool(MessageRuleService ruleService) : IChatFunctionTool
{
    public string Name => "delete_message_rule";

    public string Description =>
        "Deletes a message rule by id. Only works when the person asking has the Manage Server permission.";

    public object Parameters => new
    {
        type = "object",
        properties = new { id = new { type = "integer" } },
        required = new[] { "id" },
    };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (ChatToolHelper.Deny(message, Permissions.ManageGuild, "Manage Server") is { } denial)
        {
            return denial;
        }

        if (ChatToolHelper.GetInt(arguments, "id") is not { } id)
        {
            return "id is required.";
        }

        return await ruleService.DeleteAsync(message.GuildId!.Value, id)
            ? $"Deleted rule #{id}."
            : $"There is no rule #{id}.";
    }
}

internal static class MessageRuleToolHelper
{
    public static string Describe(MessageRule rule)
    {
        var conditions = new List<string>();
        if (rule.UserId is { } userId) conditions.Add($"from <@{userId}>");
        if (rule.ChannelId is { } channelId) conditions.Add($"in <#{channelId}>");
        if (rule.Pattern is { } pattern) conditions.Add($"matching /{pattern}/");
        if (rule.Chance is { } chance) conditions.Add($"{chance:P0} of the time");
        if (rule.ExpiresAt is { } expiresAt) conditions.Add($"until <t:{expiresAt.ToUnixTimeSeconds()}:R>");

        var action = rule.Action switch
        {
            MessageRuleAction.Timeout => $"timeout for {TimeSpan.FromSeconds(rule.TimeoutSeconds ?? 0)}",
            MessageRuleAction.Delete => "delete",
            _ => $"{rule.Action} \"{rule.Text}\"",
        };

        return $"rule #{rule.Id}: {action} on messages {string.Join(", ", conditions)} (by <@{rule.CreatedById}>)";
    }
}
