using Bot.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Bot.Persistence;

public class BotContext : DbContext
{
    private const string BOT_NAME = "SpaghettNET";
    private const string DB_NAME = "data.db";
    private readonly IConfiguration? _configuration;

    public BotContext()
    {
    }

    public BotContext(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public DbSet<User> Users { get; set; }
    public DbSet<UserA> UserAs { get; set; }
    public DbSet<Color> Colors { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (options.IsConfigured)
        {
            return;
        }

        var connectionString = GetConnectionString();
        EnsureSqliteDirectory(connectionString);

        options.UseSqlite(connectionString);
    }

    private string GetConnectionString()
    {
        var configuredConnectionString = _configuration?.GetConnectionString("Bot");
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        var configuredDatabasePath = _configuration?["Bot:DatabasePath"];
        if (!string.IsNullOrWhiteSpace(configuredDatabasePath))
        {
            return $"Data Source={configuredDatabasePath}";
        }

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), BOT_NAME);
        var dbPath = Path.Combine(folder, DB_NAME);

        return $"Data Source={dbPath}";
    }

    private static void EnsureSqliteDirectory(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource) || dataSource is ":memory:")
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
