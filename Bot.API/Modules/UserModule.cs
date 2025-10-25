using Bot.Persistence;
using Microsoft.EntityFrameworkCore;
using NetCord.Services.ApplicationCommands;

namespace Bot.API.Modules;

public class UserModule(BotContext dbContext) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("messages", "Get the number of messages sent by the user")]
    public async Task<string> Messages(NetCord.User user)
    {
        return await dbContext.Users
            .Where(x => x.Id == user.Id)
            .Select(u => u.MessagesSent)
            .DefaultIfEmpty(0)
            .Select(x => x.ToString())
            .SingleAsync();
    }
}
