using Bot.API.Extensions;
using Bot.Persistence;
using NetCord;
using NetCord.Hosting.Gateway;

namespace Bot.API.Handlers;

public class MemberHandler(BotContext dbContext) : IGuildUserAddGatewayHandler
{
    public async ValueTask HandleAsync(GuildUser arg)
    {
        await dbContext.Users.TryAddAsync(arg.Id, arg.Username);
        await dbContext.SaveChangesAsync();
    }
}