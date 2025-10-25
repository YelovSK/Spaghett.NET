using Bot.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace Bot.Persistence;

public class BotContext : DbContext
{
    private const string BOT_NAME = "SpaghettNET";
    private const string DB_NAME = "data.db";
    
    public DbSet<User> Users { get; set; }
    public DbSet<UserA> UserAs { get; set; }
    public DbSet<Color> Colors { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), BOT_NAME);
        Directory.CreateDirectory(folder);
        var dbPath = Path.Combine(folder, DB_NAME); 
        
        options.UseSqlite($"Data Source={dbPath}");
    }
}
