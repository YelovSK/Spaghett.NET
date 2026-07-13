using SpaghettNET.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace SpaghettNET.Persistence;

public class BotContext : DbContext
{
    public BotContext(DbContextOptions<BotContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<UserA> UserAs { get; set; }
    public DbSet<Color> Colors { get; set; }
}
