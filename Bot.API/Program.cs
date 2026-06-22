using Bot.Application.Services;
using Bot.API.Handlers.MessageResponders;
using Bot.API.Services;
using Bot.Infrastructure.Services;
using Bot.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services
    .AddDiscordGateway(options =>
    {
        options.Intents = GatewayIntents.GuildMessages | GatewayIntents.DirectMessages | GatewayIntents.MessageContent;
    })
    .AddApplicationCommands()
    .AddGatewayHandlers(typeof(Program).Assembly)
    .AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
    .AddSingleton<OpenAIChatService>()
    .AddSingleton<IMessageCreateResponder, OpenAIMessageResponder>()
    .AddSingleton<IMessageCreateResponder, SpecialWordResponder>()
    .AddSingleton<IMessageCreateResponder, BotMentionResponder>()
    .AddSingleton<IMessageCreateResponder, EveryoneMentionResponder>()
    .AddSingleton<IMessageCreateResponder, MessageLengthResponder>()
    .AddSingleton<IMessageCreateResponder, CapsLockResponder>()
    .AddSingleton<IImageService, ImageService>();

builder.Services
    .AddDbContextFactory<BotContext>(ConfigureBotContext);

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<ISystemService, WindowsService>();
}

var host = builder.Build();

// Add commands from modules
host.AddApplicationCommandModule<Bot.API.Modules.MiscModule>();
host.AddApplicationCommandModule<Bot.API.Modules.ImageModule>();
host.AddApplicationCommandModule<Bot.API.Modules.UserModule>();

if (OperatingSystem.IsWindows())
{
    host.AddApplicationCommandModule<Bot.API.Modules.SystemModule>();
}

// Apply DB migrations
var dbContextFactory = host.Services.GetRequiredService<IDbContextFactory<BotContext>>();
await using (var dbContext = await dbContextFactory.CreateDbContextAsync())
{
    await dbContext.Database.MigrateAsync();
}

await host.RunAsync();

void ConfigureBotContext(DbContextOptionsBuilder options)
{
    var connectionString = builder.Configuration.GetConnectionString("Bot");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaghettNET");
        connectionString = $"Data Source={Path.Combine(folder, "data.db")}";
    }

    EnsureSqliteDirectory(connectionString);
    options.UseSqlite(connectionString);
}

static void EnsureSqliteDirectory(string connectionString)
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
