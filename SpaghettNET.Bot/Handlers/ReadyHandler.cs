using SpaghettNET.Bot.Extensions;
using SpaghettNET.Persistence;
using Microsoft.EntityFrameworkCore;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace SpaghettNET.Bot.Handlers;

public class ReadyHandler(IDbContextFactory<BotContext> dbContextFactory, GatewayClient client) : IReadyGatewayHandler
{
    public async ValueTask HandleAsync(ReadyEventArgs arg)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

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
