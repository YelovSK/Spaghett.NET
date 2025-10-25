using Bot.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace Bot.API.Extensions;

public static partial class Extensions
{
    /// <summary>
    /// Does NOT call SaveChanges.
    /// </summary>
    public static async Task<User> TryAddAsync(this DbSet<User> users, ulong userId, string name)
    {
        var user = await users.SingleOrDefaultAsync(x => x.Id == userId);
        if (user is not null)
        {
            return user;
        }

        user = new User
        {
            Id = userId,
            Username = name,
            MessagesSent = 0,
        };
        users.Add(user);

        return user;
    }
}