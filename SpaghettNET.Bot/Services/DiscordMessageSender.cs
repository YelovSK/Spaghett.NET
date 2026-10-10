using System.Text.RegularExpressions;
using NetCord.Gateway;
using NetCord.Rest;

namespace SpaghettNET.Bot.Services;

/// <summary>
/// The one way the bot posts text: expands &lt;emote_name&gt; tokens into emotes Discord renders and splits
/// anything over Discord's 2000 character limit, whoever wrote the text.
/// </summary>
public sealed partial class DiscordMessageSender(GatewayClient gatewayClient)
{
    private const int MessageLimit = 2000;

    [GeneratedRegex(@"<(?<name>[A-Za-z0-9_]+)>")]
    private static partial Regex EmoteTokenRegex();

    /// <param name="replyTo">Reply to this message. Only the first chunk is a reply, so the rest don't each ping.</param>
    public async Task SendAsync(ulong channelId, string text, Message? replyTo = null)
    {
        var guild = replyTo is not null
            ? replyTo.Guild
            : gatewayClient.Cache.Guilds.Values.FirstOrDefault(guild => guild.Channels.ContainsKey(channelId));
        var chunks = Split(guild is null ? text : ExpandEmotes(guild, text));

        for (var i = 0; i < chunks.Count; i++)
        {
            if (i == 0 && replyTo is not null)
            {
                await replyTo.ReplyAsync(chunks[i]);
            }
            else
            {
                await gatewayClient.Rest.SendMessageAsync(channelId, chunks[i]);
            }
        }
    }

    private static string ExpandEmotes(Guild guild, string text) =>
        EmoteTokenRegex().Replace(text, match =>
            guild.Emojis.Values.FirstOrDefault(emoji =>
                    emoji.Available != false &&
                    string.Equals(emoji.Name, match.Groups["name"].Value, StringComparison.InvariantCultureIgnoreCase))
                is { } emoji
                ? emoji.Animated ? $"<a:{emoji.Name}:{emoji.Id}>" : $"<:{emoji.Name}:{emoji.Id}>"
                : match.Value);

    internal static List<string> Split(string content, int limit = MessageLimit)
    {
        var chunks = new List<string>();
        var remaining = content.AsSpan().Trim();

        while (remaining.Length > limit)
        {
            var window = remaining[..limit];
            var cut = window.LastIndexOf('\n');
            if (cut <= 0)
            {
                cut = window.LastIndexOf(' ');
            }

            if (cut <= 0)
            {
                cut = limit;
            }

            chunks.Add(remaining[..cut].TrimEnd().ToString());
            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0 || chunks.Count == 0)
        {
            chunks.Add(remaining.ToString());
        }

        return chunks;
    }
}
