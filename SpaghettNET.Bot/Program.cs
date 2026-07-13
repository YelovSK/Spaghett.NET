using SpaghettNET.Bot.Handlers.MessageResponders;
using SpaghettNET.Bot.Services;
using SpaghettNET.Platform.Services;
using SpaghettNET.Persistence;
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
    .AddSingleton(builder.Configuration.GetSection("OpenAI").Get<OpenAIOptions>() ?? new OpenAIOptions())
    .AddSingleton<OpenAIChatRuntimeSettings>()
    .AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
    .AddSingleton<IModelMetadataProvider, OpenRouterModelMetadataProvider>()
    .AddSingleton<ModelMetadataResolver>()
    .AddSingleton<OpenRouterManagementClient>()
    .AddSingleton<OpenAIChatClient>()
    .AddSingleton<OpenAIReplyGenerator>()
    .AddSingleton<IMessageCreateResponder, OpenAIMessageResponder>()
    .AddSingleton<IMessageCreateResponder, SpecialWordResponder>()
    .AddSingleton<IMessageCreateResponder, EveryoneMentionResponder>()
    .AddSingleton<IMessageCreateResponder, MessageLengthResponder>()
    .AddSingleton<IMessageCreateResponder, CapsLockResponder>()
    .AddSingleton<ImageService>();

builder.Services
    .AddDbContextFactory<BotContext>(ConfigureBotContext);

if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<ISystemService, WindowsService>();
}

var host = builder.Build();

// Add commands from modules
host.AddApplicationCommandModule<SpaghettNET.Bot.Modules.MiscModule>();
host.AddApplicationCommandModule<SpaghettNET.Bot.Modules.ImageModule>();
host.AddApplicationCommandModule<SpaghettNET.Bot.Modules.UserModule>();
host.AddApplicationCommandModule<SpaghettNET.Bot.Modules.OpenAIModelModule>();

if (OperatingSystem.IsWindows())
{
    host.AddApplicationCommandModule<SpaghettNET.Bot.Modules.SystemModule>();
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
