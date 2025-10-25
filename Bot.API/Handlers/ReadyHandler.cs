using Bot.API.Extensions;
using Bot.Persistence;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace Bot.API.Handlers;

public class ReadyHandler(BotContext dbContext, GatewayClient client) : IReadyGatewayHandler
{
    public async ValueTask HandleAsync(ReadyEventArgs arg)
    {
        foreach (var guildId in arg.GuildIds)
        {
            await foreach (var member in client.Rest.GetGuildUsersAsync(guildId))
            {
                await dbContext.Users.TryAddAsync(member.Id, member.Username);
            }
        }

        await dbContext.SaveChangesAsync();
    }
}