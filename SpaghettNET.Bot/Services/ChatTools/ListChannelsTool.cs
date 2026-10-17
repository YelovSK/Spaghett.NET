using System.Text.Json;
using NetCord;
using NetCord.Gateway;
using SpaghettNET.Bot.Services.Chat;

namespace SpaghettNET.Bot.Services.ChatTools;

public sealed class ListChannelsTool : IChatFunctionTool
{
    public string Name => "list_channels";

    public string Description =>
        "Lists this server's channels that the person asking can see, with ids and categories. " +
        "Use it to find the channel someone means by a loose name.";

    public object Parameters => new { type = "object", properties = new { } };

    public Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (message.Guild is not { } guild || message.Author is not GuildUser author)
        {
            return Task.FromResult("This only works in servers.");
        }

        var lines = guild.Channels.Values
            .Where(channel => channel is not CategoryGuildChannel)
            .Where(channel => author.Id == guild.OwnerId ||
                              author.GetChannelPermissions(guild, channel.Id) is var permissions &&
                              (permissions.HasFlag(Permissions.Administrator) ||
                               permissions.HasFlag(Permissions.ViewChannel)))
            .OrderBy(channel => channel.Position)
            .Select(channel =>
            {
                var (kind, parentId) = channel switch
                {
                    IVoiceGuildChannel voice => ("voice", voice.ParentId),
                    ForumGuildChannel forum => ("forum", forum.ParentId),
                    TextGuildChannel text => ("text", text.ParentId),
                    _ => ("other", null),
                };
                var category = parentId is { } id && guild.Channels.TryGetValue(id, out var parent)
                    ? $", category: {parent.Name}"
                    : "";
                return $"#{channel.Name}, id: {channel.Id}, {kind}{category}";
            });

        return Task.FromResult(string.Join(Environment.NewLine, lines));
    }
}
