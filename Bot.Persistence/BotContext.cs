using Bot.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace Bot.Persistence;

public class BotContext : DbContext
{
    public BotContext(DbContextOptions<BotContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<UserA> UserAs { get; set; }
    public DbSet<Color> Colors { get; set; }
}
