using System.Globalization;
using System.Text;
using Bot.API.Constants;
using Bot.API.Extensions;
using Bot.Persistence;
using Bot.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace Bot.API.Modules;

public class MiscModule(BotContext dbContext) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("a", "Sends a random number of A characters. Up to 20")]
    public async Task<string> Messages()
    {
        var arr = Enumerable.Repeat('A', Random.Shared.Next(1, 20 + 1)).ToList(); 
        dbContext.UserAs.Add(new UserA
        {
            Id = Guid.NewGuid(),
            UserId = Context.User.Id,
            Length = arr.Count,
            CreatedOn = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        
        return string.Join(string.Empty, arr);
    }
    
    [SlashCommand("staats", "Stats for the A command")]
    public async Task<InteractionMessageProperties> Staats(NetCord.User user)
    {
        var stats = await dbContext.UserAs
            .Where(x => x.UserId == user.Id)
            .ToListAsync();

        if (stats.Count == 0)
        {
            return InteractionMessageProperties.Create().WithImage(ImageLinks.NO_STATS);
        }
        
        var average = stats.Select(x => x.Length).Average().ToString("0.##");
        var max = stats.Select(x => x.Length).Max();
        var min = stats.Select(x => x.Length).Min();
        var totalCount = stats.Select(x => x.Length).Sum();
        var first = stats.OrderBy(x => x.CreatedOn).Select(x => x.CreatedOn).FirstOrDefault().ToString(CultureInfo.InvariantCulture);
        var last = stats.OrderByDescending(x => x.CreatedOn).Select(x => x.CreatedOn).FirstOrDefault().ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine($"Average: {average}");
        sb.AppendLine($"Best: {Enumerable.Repeat("A", max).Join(string.Empty)}");
        sb.AppendLine($"Worst: {Enumerable.Repeat("A", min).Join(string.Empty)}");
        sb.AppendLine($"Total A-s count: {totalCount}");
        sb.AppendLine($"First: {first}");
        sb.AppendLine($"Last: {last}");

        return InteractionMessageProperties.Create().WithContent(sb.ToString());
    }

    [SlashCommand("goodbot", "compliment bot")]
    public string GoodBot() => "ty";
    
    [SlashCommand("badbot", "uncompliment bot")]
    public InteractionMessageProperties BadBot()
    {
        var avatar = Context.User.GetAvatarUrl();

        return InteractionMessageProperties
            .Create()
            .WithContent("look at your stupid fucking ugly face, go die")
            .WithImage(avatar!.ToString());
    }
    
    [SlashCommand("censor", "censors the message")]
    public string Censor(string message)
    {
        return message
            .Select(c => !char.IsWhiteSpace(c) && Random.Shared.Next(0, 4) == 0
                ? @"\*"
                : c.ToString())
            .Join(string.Empty);
    }
}