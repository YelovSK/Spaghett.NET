using Bot.Application.Services;
using Bot.Infrastructure.Services;
using Bot.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddDiscordGateway(options =>
    {
        options.Intents = GatewayIntents.GuildMessages | GatewayIntents.DirectMessages | GatewayIntents.MessageContent;
    })
    .AddApplicationCommands()
    .AddGatewayHandlers(typeof(Program).Assembly)
    .AddSingleton<IImageService, ImageService>()
    .AddDbContext<BotContext>();

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
using (var scope = host.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<BotContext>();
    await dbContext.Database.MigrateAsync();
}

await host.RunAsync();
