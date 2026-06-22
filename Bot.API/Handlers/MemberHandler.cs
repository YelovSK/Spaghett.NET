using Bot.API.Extensions;
using Bot.Persistence;
using Microsoft.EntityFrameworkCore;
using NetCord;
using NetCord.Hosting.Gateway;

namespace Bot.API.Handlers;

public class MemberHandler(IDbContextFactory<BotContext> dbContextFactory) : IGuildUserAddGatewayHandler
{
    public async ValueTask HandleAsync(GuildUser arg)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        await dbContext.Users.TryAddAsync(arg.Id, arg.Username);
        await dbContext.SaveChangesAsync();
    }
}
