using SpaghettNET.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SpaghettNET.Persistence;

public class BotContext : DbContext
{
    public BotContext(DbContextOptions<BotContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<UserA> UserAs { get; set; }
    public DbSet<Color> Colors { get; set; }
    public DbSet<DiscordMemory> DiscordMemories { get; set; }
    public DbSet<DiscordMemoryEvaluation> DiscordMemoryEvaluations { get; set; }
    public DbSet<DiscordMemoryBackfillCursor> DiscordMemoryBackfillCursors { get; set; }
    public DbSet<DiscordMemoryExcludedChannel> DiscordMemoryExcludedChannels { get; set; }
    public DbSet<MessageRule> MessageRules { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DiscordMemory>()
            .HasIndex(memory => memory.MessageId)
            .IsUnique();
        modelBuilder.Entity<DiscordMemory>()
            .HasIndex(memory => new { memory.GuildId, memory.MessageId });

        // Every ulong here is a Discord snowflake, which fits in a signed 64-bit integer.
        // SQLite refuses to sort or compare ulong, so store them as long instead.
        var snowflakeConverter = new ValueConverter<ulong, long>(
            snowflake => (long)snowflake,
            value => (ulong)value);
        foreach (var property in modelBuilder.Model
                     .GetEntityTypes()
                     .SelectMany(entityType => entityType.GetProperties())
                     .Where(property => property.ClrType == typeof(ulong) || property.ClrType == typeof(ulong?)))
        {
            property.SetValueConverter(snowflakeConverter);
        }
    }
}
