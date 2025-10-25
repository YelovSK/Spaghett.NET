using Bot.API.Extensions;
using Bot.Persistence;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

namespace Bot.API.Handlers;

public class MessageCreateHandler(BotContext dbContext, GatewayClient gatewayClient) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message message)
    {
        await IncrementMessageCountAsync(message.Author);
        
        // Ignore this bot's messages
        if (message.Author.Id == gatewayClient.Id)
        {
            return;
        }
        
        await HandleSpecialWords(message);
        await HandleMentions(message);
        await HandleMessageLength(message);
        await HandleCapsLock(message);
    }

    private async Task IncrementMessageCountAsync(NetCord.User user)
    {
        var dbUser = await dbContext.Users.TryAddAsync(user.Id, user.Username);
        dbUser.MessagesSent++;
        await dbContext.SaveChangesAsync();
    }

    private async Task HandleSpecialWords(Message message)
    {
        var words = message.Content
            .SplitWords()
            .ToHashSet(StringComparer.InvariantCultureIgnoreCase);
        
        if (words.Contains("cat"))
        {
            await message.ReplyAsync("ur cat");
        }
    }
    
    private async Task HandleMentions(Message message)
    {
        if (message.MentionedUsers.Any(u => u.Id == gatewayClient.Id))
        {
            await message.ReplyAsync("stfu");
        }
        
        if (message.MentionEveryone)
        {
            await message.ReplyAsync("war crime detected");
        }
    }
    
    private async Task HandleMessageLength(Message message)
    {
        var specialLengths = new[] { 69, 322, 420, 1337 };
        var specialLength = specialLengths.SingleOrDefault(x => x == message.Content.Length);
        
        if (specialLength != 0)
        {
            await message.ReplyAsync($"poggers {specialLength} characters");
        }
    }
    
    private async Task HandleCapsLock(Message message)
    {
        if (message.Content.Length < 10)
        {
            return;
        }
        
        var isAllUpper = message.Content
            .Where(c => !char.IsWhiteSpace(c))
            .All(char.IsUpper);
        
        if (isAllUpper)
        {
            await message.ReplyAsync("y u shout");
        }
    }
}