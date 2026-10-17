using System.Text.Json;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using SpaghettNET.Bot.Services.Chat;

namespace SpaghettNET.Bot.Services.ChatTools;

public sealed class ReadChannelTool(GatewayClient gatewayClient) : IChatFunctionTool
{
    private const int DefaultCount = 30;
    private const int MaxCount = 100;
    // Keeps a channel full of walls of text from flooding the context.
    private const int CharacterBudget = 12_000;

    public string Name => "read_channel";

    public string Description =>
        "Reads the most recent messages from another channel in this server. " +
        "Only works for channels the person asking can read.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            channel = new { type = "string", description = "The channel's name, or id if you know it." },
            count = new { type = "integer", description = $"How many messages, default {DefaultCount}, max {MaxCount}." },
        },
        required = new[] { "channel" },
    };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (message.Guild is not { } guild || message.Author is not GuildUser author)
        {
            return "This only works in servers.";
        }

        if (ChatToolHelper.GetString(arguments, "channel") is not { } channel)
        {
            return "channel is required.";
        }

        var (resolved, error) = ChatToolHelper.ResolveChannel(guild, channel);
        if (resolved is null)
        {
            return error!;
        }

        var channelId = resolved.Id;

        // The bot may see channels the asker can't, so it must not read them out.
        var permissions = author.GetChannelPermissions(guild, channelId);
        if (author.Id != guild.OwnerId &&
            !permissions.HasFlag(Permissions.Administrator) &&
            !permissions.HasFlag(Permissions.ViewChannel | Permissions.ReadMessageHistory))
        {
            return "Refused: the person asking can't read that channel.";
        }

        var count = Math.Clamp(ChatToolHelper.GetInt(arguments, "count") ?? DefaultCount, 1, MaxCount);
        var lines = new List<string>();
        var characters = 0;
        await foreach (var channelMessage in gatewayClient.Rest
                           .GetMessagesAsync(channelId, new PaginationProperties<ulong> { BatchSize = count })
                           .WithCancellation(cancellationToken))
        {
            var line = OpenAIReplyGenerator.FormatMessage(channelMessage);
            characters += line.Length;
            if (lines.Count == count || (lines.Count > 0 && characters > CharacterBudget))
            {
                break;
            }

            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            return "The channel has no messages.";
        }

        lines.Reverse();
        return $"Last {lines.Count} messages in <#{channelId}>, oldest first:{Environment.NewLine}" +
               string.Join(Environment.NewLine, lines);
    }
}
