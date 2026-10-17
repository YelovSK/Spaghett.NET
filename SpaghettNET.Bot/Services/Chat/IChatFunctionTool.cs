using System.Text.Json;
using NetCord.Gateway;

namespace SpaghettNET.Bot.Services.Chat;

public interface IChatFunctionTool
{
    string Name { get; }
    string Description { get; }

    /// <summary>JSON schema of the arguments object.</summary>
    object Parameters { get; }

    /// <param name="message">The Discord message the bot is replying to, for permission checks and context.</param>
    /// <returns>Text the model reads as the tool result.</returns>
    Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken);
}
