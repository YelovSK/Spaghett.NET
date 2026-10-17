using SpaghettNET.Bot.Handlers.MessageResponders;
using SpaghettNET.Bot.Services;
using SpaghettNET.Bot.Services.ChatTools;
using SpaghettNET.Bot.Services.Scripting;
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
        options.Intents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages | GatewayIntents.MessageContent | GatewayIntents.GuildPresences | GatewayIntents.GuildEmojisAndStickers;
    })
    .AddApplicationCommands()
    .AddGatewayHandlers(typeof(Program).Assembly)
    .AddSingleton(builder.Configuration.GetSection("OpenAI").Get<OpenAIOptions>() ?? new OpenAIOptions())
    .AddSingleton<OpenAIChatRuntimeSettings>()
    .AddSingleton<ModelMetadataResolver>()
    .AddSingleton<DiscordMemoryService>()
    .AddSingleton<DiscordMemoryRunBuffer>()
    .AddSingleton<OpenAIReplyGenerator>()
    .AddSingleton<DiscordMessageSender>()
    .AddSingleton<ScriptService>()
    .AddSingleton<IChatFunctionTool, CreateScriptTool>()
    .AddSingleton<IChatFunctionTool, UpdateScriptTool>()
    .AddSingleton<IChatFunctionTool, ListScriptsTool>()
    .AddSingleton<IChatFunctionTool, DeleteScriptTool>()
    .AddSingleton<IChatFunctionTool, FindMemberTool>()
    .AddSingleton<IChatFunctionTool, ListMembersTool>()
    .AddSingleton<IChatFunctionTool, TimeoutMemberTool>()
    .AddSingleton<IChatFunctionTool, ReadChannelTool>()
    .AddSingleton<IChatFunctionTool, ListChannelsTool>()
    // Scripts go first so pinging the bot can't skip them.
    .AddSingleton<IMessageCreateResponder, ScriptResponder>()
    .AddSingleton<IMessageCreateResponder, OpenAIMessageResponder>()
    .AddSingleton<ImageService>();

builder.Services.AddHttpClient<OpenAIChatClient>(client =>
    client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddHttpClient<DecisionClient>(client =>
    client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<OpenRouterManagementClient>();
builder.Services.AddHttpClient<IModelMetadataProvider, OpenRouterModelMetadataProvider>();

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
host.AddApplicationCommandModule<SpaghettNET.Bot.Modules.MemoryModule>();
host.AddApplicationCommandModule<SpaghettNET.Bot.Modules.ScriptModule>();

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
