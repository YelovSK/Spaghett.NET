using System.Text;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using SpaghettNET.Bot.Services.Scripting;
using SpaghettNET.Persistence.Models;

namespace SpaghettNET.Bot.Modules;

[SlashCommand("script", "Manages the scripts the bot runs", Contexts = [InteractionContextType.Guild])]
public class ScriptModule(
    ScriptService scriptService,
    ILogger<ScriptModule> logger) : ApplicationCommandModule<ApplicationCommandContext>
{
    // Discord caps a message at 2000 characters; the rest is headroom for the code fence.
    private const int MessageBudget = 1900;
    private const int MaxStateLength = 300;

    [SubSlashCommand("list", "Lists this server's scripts")]
    public Task List() =>
        RespondAsync(async guildId =>
        {
            var scripts = await scriptService.GetScriptsAsync(guildId);
            if (scripts.Count == 0)
            {
                return new Reply("There are no scripts.");
            }

            var lines = scripts.Select(script =>
            {
                var line = $"**#{script.Id}** {script.Description} · by <@{script.CreatedById}>";
                if (script.ExpiresAt is { } expiresAt)
                {
                    line += $" · expires <t:{expiresAt.ToUnixTimeSeconds()}:R>";
                }

                return line + Status(script);
            });
            return new Reply($"Use `/script show` with a number to see the code.\n{string.Join('\n', lines)}");
        });

    [SubSlashCommand("show", "Shows a script's code, state and errors")]
    public Task Show(
        [SlashCommandParameter(Description = "The script's number from /script list")]
        int id) =>
        RespondAsync(async guildId =>
        {
            var script = (await scriptService.GetScriptsAsync(guildId)).FirstOrDefault(script => script.Id == id);
            if (script is null)
            {
                return new Reply($"There is no script #{id}.");
            }

            var header = new StringBuilder($"**#{script.Id}** {script.Description}{Status(script)}\n");
            header.Append($"By <@{script.CreatedById}> <t:{script.CreatedAt.ToUnixTimeSeconds()}:R>");
            if (script.ExpiresAt is { } expiresAt)
            {
                header.Append($", expires <t:{expiresAt.ToUnixTimeSeconds()}:R>");
            }

            header.Append('\n');
            if (script.LastError is not null)
            {
                header.Append($"Last error: `{script.LastError}`\n");
            }

            var state = script.State.Length <= MaxStateLength ? script.State : script.State[..MaxStateLength] + "…";
            header.Append($"State: `{state}`\n");

            var inline = $"{header}```js\n{script.Code}\n```";
            return inline.Length <= MessageBudget
                ? new Reply(inline)
                : new Reply($"{header}Code is too long for a message, see the attached file.", script.Code);
        });

    [SubSlashCommand("delete", "Deletes a script")]
    public Task Delete(
        [SlashCommandParameter(Description = "The script's number from /script list")]
        int id) =>
        RespondAsync(async guildId =>
        {
            // Discord only applies default permissions to the whole command, and list/show are for everyone.
            if (Context.Interaction.User is not GuildInteractionUser { Permissions: var permissions } ||
                !permissions.HasFlag(Permissions.ManageGuild))
            {
                return new Reply("Deleting scripts needs the Manage Server permission.");
            }

            return await scriptService.DeleteAsync(guildId, id)
                ? new Reply($"Deleted script #{id}.")
                : new Reply($"There is no script #{id}.");
        });

    private static string Status(BotScript script) =>
        script.FailureCount >= ScriptService.MaxFailures ? $" · ⛔ disabled after {script.FailureCount} failures"
        : script.FailureCount > 0 ? $" · ⚠️ failing ({script.FailureCount} in a row)"
        : "";

    private async Task RespondAsync(Func<ulong, Task<Reply>> handler)
    {
        await Context.Interaction.SendResponseAsync(
            InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        Reply reply;
        try
        {
            reply = Context.Interaction.GuildId is { } guildId
                ? await handler(guildId)
                : new Reply("This command only works in a server channel.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Script command failed.");
            reply = new Reply("That failed.");
        }

        await Context.Interaction.ModifyResponseAsync(message =>
        {
            message.Content = reply.Content;
            if (reply.Code is not null)
            {
                message.Attachments =
                [
                    new AttachmentProperties("script.js", new MemoryStream(Encoding.UTF8.GetBytes(reply.Code))),
                ];
            }
        });
    }

    /// <param name="Code">Sent as a file when it doesn't fit in the message.</param>
    private sealed record Reply(string Content, string? Code = null);
}
