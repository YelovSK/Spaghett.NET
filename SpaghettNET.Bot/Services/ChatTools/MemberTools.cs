using System.Text.Json;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using SpaghettNET.Bot.Services.Chat;

namespace SpaghettNET.Bot.Services.ChatTools;

public sealed class FindMemberTool(GatewayClient gatewayClient) : IChatFunctionTool
{
    private const int Limit = 10;

    public string Name => "find_member";

    public string Description =>
        "Finds server members whose username or nickname starts with the given name. Returns their ids for mentions and rules.";

    public object Parameters => new
    {
        type = "object",
        properties = new { name = new { type = "string" } },
        required = new[] { "name" },
    };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (message.Guild is not { } guild)
        {
            return "Members only exist in servers.";
        }

        if (ChatToolHelper.GetString(arguments, "name") is not { } name)
        {
            return "name is required.";
        }

        var users = await gatewayClient.Rest.FindGuildUserAsync(
            guild.Id,
            name.TrimStart('@'),
            Limit,
            cancellationToken: cancellationToken);
        return users.Count == 0
            ? $"No member matches {name}."
            : string.Join(Environment.NewLine, users.Select(user => MemberToolHelper.Format(user, guild)));
    }
}

public sealed class ListMembersTool(GatewayClient gatewayClient) : IChatFunctionTool
{
    private const int Limit = 20;

    public string Name => "list_members";
    public string Description => $"Lists up to {Limit} server members with their ids and online status.";
    public object Parameters => new { type = "object", properties = new { } };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (message.Guild is not { } guild)
        {
            return "Members only exist in servers.";
        }

        var lines = new List<string>();
        await foreach (var user in gatewayClient.Rest
                           .GetGuildUsersAsync(guild.Id, new PaginationProperties<ulong> { BatchSize = Limit })
                           .WithCancellation(cancellationToken))
        {
            lines.Add(MemberToolHelper.Format(user, guild));
            if (lines.Count == Limit)
            {
                break;
            }
        }

        if (guild.UserCount > lines.Count)
        {
            lines.Add($"Showing {lines.Count} of {guild.UserCount} members. Use find_member for anyone else.");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class TimeoutMemberTool(GatewayClient gatewayClient) : IChatFunctionTool
{
    public string Name => "timeout_member";

    public string Description =>
        "Times out a server member right now, or lifts their timeout with 0 seconds. " +
        "Only works when the person asking has the Moderate Members permission.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            user = new { type = "string", description = "The member's name, or id if you know it." },
            seconds = new { type = "integer", description = "0 lifts the timeout. Max 28 days." },
        },
        required = new[] { "user", "seconds" },
    };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (ChatToolHelper.Deny(message, Permissions.ModerateUsers, "Moderate Members") is { } denial)
        {
            return denial;
        }

        if (ChatToolHelper.GetString(arguments, "user") is not { } user)
        {
            return "user is required.";
        }

        var (resolvedId, error) = await ChatToolHelper.ResolveUserAsync(gatewayClient, message, user, cancellationToken);
        if (resolvedId is not { } userId)
        {
            return error!;
        }

        var seconds = ChatToolHelper.GetInt(arguments, "seconds");
        if (seconds is not (>= 0 and <= ChatToolHelper.MaxTimeoutSeconds))
        {
            return $"seconds must be between 0 and {ChatToolHelper.MaxTimeoutSeconds}.";
        }

        await gatewayClient.Rest.ModifyGuildUserAsync(
            message.GuildId!.Value,
            userId,
            options => options.TimeOutUntil = seconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(seconds.Value) : null,
            cancellationToken: cancellationToken);

        return seconds > 0
            ? $"Timed out <@{userId}> for {TimeSpan.FromSeconds(seconds.Value)}."
            : $"Lifted the timeout on <@{userId}>.";
    }
}

internal static class MemberToolHelper
{
    public static string Format(GuildUser user, Guild guild)
    {
        // Offline members have no presence at all.
        var status = guild.Presences.TryGetValue(user.Id, out var presence) ? presence.Status : UserStatusType.Offline;
        var parts = new List<string> { $"username: {user.Username}" };
        if (user.GlobalName is { } globalName) parts.Add($"display name: {globalName}");
        if (user.Nickname is { } nickname) parts.Add($"server nickname: {nickname}");
        parts.Add($"id: {user.Id}");
        parts.Add($"status: {status.ToString().ToLowerInvariant()}");
        if (user.IsBot) parts.Add("bot");

        return string.Join(", ", parts);
    }
}
