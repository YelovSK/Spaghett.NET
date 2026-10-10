using System.Text.Json;
using System.Text.RegularExpressions;
using NetCord;
using NetCord.Gateway;

namespace SpaghettNET.Bot.Services.ChatTools;

internal static partial class ChatToolHelper
{
    /// <summary>The longest timeout Discord allows.</summary>
    public const int MaxTimeoutSeconds = 28 * 24 * 60 * 60;

    [GeneratedRegex(@"^<@!?(?<id>\d+)>$|^(?<id>\d+)$")]
    private static partial Regex UserRegex();

    [GeneratedRegex(@"^<#(?<id>\d+)>$|^(?<id>\d+)$")]
    private static partial Regex ChannelRegex();

    private const int MaxUserCandidates = 10;

    /// <summary>
    /// Resolves a channel id, mention or name to a channel of this server only, so ids from other servers the bot
    /// is in can't be used. The error tells the model how to recover.
    /// </summary>
    public static (IGuildChannel? Channel, string? Error) ResolveChannel(Guild guild, string channel)
    {
        IGuildChannel? found;
        if (ChannelRegex().Match(channel) is { Success: true } match)
        {
            found = guild.Channels.GetValueOrDefault(ulong.Parse(match.Groups["id"].Value));
        }
        else
        {
            // Models often write "random thought" for #random-thought.
            var name = channel.TrimStart('#').Trim().Replace(' ', '-');
            found = guild.Channels.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.InvariantCultureIgnoreCase));
        }

        return found is not null
            ? (found, null)
            : (null, $"No channel {channel} in this server. Call list_channels to see the real names.");
    }

    /// <summary>Resolves a user id, mention or name. The error tells the model how to recover.</summary>
    public static async Task<(ulong? Id, string? Error)> ResolveUserAsync(
        GatewayClient gatewayClient,
        Message message,
        string user,
        CancellationToken cancellationToken)
    {
        if (UserRegex().Match(user) is { Success: true } match)
        {
            return (ulong.Parse(match.Groups["id"].Value), null);
        }

        var name = user.TrimStart('@').Trim();
        // Discord's search matches usernames and nicknames by prefix, not display names.
        var candidates = await gatewayClient.Rest.FindGuildUserAsync(
            message.GuildId!.Value,
            name,
            MaxUserCandidates,
            cancellationToken: cancellationToken);
        var exact = candidates
            .Where(candidate => new[] { candidate.Username, candidate.GlobalName, candidate.Nickname }
                .Any(candidateName => string.Equals(candidateName, name, StringComparison.InvariantCultureIgnoreCase)))
            .ToArray();

        if (exact.Length == 1)
        {
            return (exact[0].Id, null);
        }

        if (candidates.Count == 1)
        {
            return (candidates[0].Id, null);
        }

        if (candidates.Count == 0)
        {
            return (null, $"No member named {user}. Call list_members to see everyone's names and ids.");
        }

        return (null, $"Several members match {user}: " +
                      string.Join(", ", candidates.Select(candidate =>
                          $"{candidate.Nickname ?? candidate.GlobalName ?? candidate.Username} (id {candidate.Id})")) +
                      ". Ask which one is meant, or retry with the id if it's clear.");
    }

    /// <returns>Why the author may not do this, or null when they may.</returns>
    public static string? Deny(Message message, Permissions permission, string permissionName)
    {
        if (message.Guild is not { } guild || message.Author is not GuildUser author)
        {
            return "This only works in servers.";
        }

        var permissions = author.GetPermissions(guild);
        return author.Id == guild.OwnerId ||
               permissions.HasFlag(Permissions.Administrator) ||
               permissions.HasFlag(permission)
            ? null
            : $"Refused: the person asking does not have the {permissionName} permission.";
    }

    public static string? GetString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    public static double? GetDouble(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    public static int? GetInt(JsonElement arguments, string name) =>
        GetDouble(arguments, name) is { } number ? (int)Math.Clamp(number, int.MinValue, int.MaxValue) : null;
}
