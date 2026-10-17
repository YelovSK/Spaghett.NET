using System.Text.Json;
using NetCord;
using NetCord.Gateway;
using SpaghettNET.Bot.Services.Scripting;
using SpaghettNET.Persistence.Models;
using SpaghettNET.Bot.Services.Chat;

namespace SpaghettNET.Bot.Services.ChatTools;

public sealed class CreateScriptTool(ScriptService scriptService) : IChatFunctionTool
{
    public string Name => "create_script";

    public string Description => $$"""
        Creates a persistent JavaScript script that reacts to events in this server, for automations and pranks the
        other tools can't do. Only works when the person asking has the Manage Server permission.
        The code is checked and dry-run against the current message before it's saved; errors come back to you.

        Register handlers with on(event, handler). Events: "message" (any new message from someone else).
        on("message", msg => { ... }) where msg is:
          { id, content, createdAt (ms), hasAttachments,
            author: { id, name, username, isBot }, channel: { id, name }, mentions: [userId], mentionsEveryone }
        All ids are strings. author.name is what people see (nickname or display name).

        api (only usable inside handlers, max 10 calls per event):
          api.reply(text)               reply to the message
          api.send(channel, text)       post a new message in a channel of this server, by id (preferred, survives
                                        renames; look ids up with list_channels) or exact name
          api.react(emoji)              unicode emoji or a server emote name
          api.deleteMessage()
          api.timeout(userId, seconds)  0 lifts a timeout, max 28 days
          api.llmReply(instruction)     you write a reply following the instruction (costs a model call, use sparingly)
          api.llmSend(channel, instruction)
                                        like llmReply, but posted as a new message in another channel
          api.store.get(key) / api.store.set(key, value)
                                        persistent JSON state for this script; set(key, undefined) removes the key
        Write server emotes in text as <emote_name>, same as in chat.
        Plain JS only: no network, timers, or imports. Each event gets 50ms. Use member ids or names you looked up
        instead of guessing, and filter with regexes like /\bword\b/i.

        Example, times Averso out for longer each time he says "bark":
          on("message", msg => {
            if (msg.author.name !== "Averso" || !/\bbark\b/i.test(msg.content)) return;
            const barks = (api.store.get("barks") ?? 0) + 1;
            api.store.set("barks", barks);
            api.timeout(msg.author.id, 60 * barks);
          });
        """;

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            description = new { type = "string", description = "One line saying what the script does, shown when listing." },
            code = new { type = "string" },
            expires_in_minutes = new { type = "integer", description = "Delete the script after this long. Omit for permanent." },
        },
        required = new[] { "description", "code" },
    };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (ChatToolHelper.Deny(message, Permissions.ManageGuild, "Manage Server") is { } denial)
        {
            return denial;
        }

        if (ChatToolHelper.GetString(arguments, "description") is not { } description ||
            ChatToolHelper.GetString(arguments, "code") is not { } code)
        {
            return "description and code are required.";
        }

        var expiresInMinutes = ChatToolHelper.GetInt(arguments, "expires_in_minutes");
        if (expiresInMinutes is <= 0)
        {
            return "expires_in_minutes must be positive.";
        }

        const string emptyState = "{}";
        var dryRun = ScriptService.DryRun(
            code,
            emptyState,
            ScriptEvents.Message,
            ScriptEvents.MessagePayload(message),
            ScriptEventContext.ForMessage(message));
        if (dryRun.Error is not null)
        {
            return $"Not saved. {dryRun.Error}";
        }

        var script = new BotScript
        {
            GuildId = message.GuildId!.Value,
            Description = description,
            Code = code,
            State = emptyState,
            CreatedById = message.Author.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresInMinutes is { } minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null,
        };
        await scriptService.AddAsync(script);

        return $"Created script #{script.Id}. {ScriptToolHelper.DescribeDryRun(dryRun)}";
    }
}

public sealed class UpdateScriptTool(ScriptService scriptService) : IChatFunctionTool
{
    public string Name => "update_script";

    public string Description =>
        "Replaces a script's code, keeping its stored state, e.g. to fix a failing script. Same API as create_script. " +
        "Only works when the person asking has the Manage Server permission.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            id = new { type = "integer" },
            code = new { type = "string" },
            description = new { type = "string", description = "New one-line description, if what it does changed." },
        },
        required = new[] { "id", "code" },
    };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (ChatToolHelper.Deny(message, Permissions.ManageGuild, "Manage Server") is { } denial)
        {
            return denial;
        }

        if (ChatToolHelper.GetInt(arguments, "id") is not { } id ||
            ChatToolHelper.GetString(arguments, "code") is not { } code)
        {
            return "id and code are required.";
        }

        var guildId = message.GuildId!.Value;
        var existing = (await scriptService.GetScriptsAsync(guildId)).FirstOrDefault(script => script.Id == id);
        if (existing is null)
        {
            return $"There is no script #{id}.";
        }

        var dryRun = ScriptService.DryRun(
            code,
            existing.State,
            ScriptEvents.Message,
            ScriptEvents.MessagePayload(message),
            ScriptEventContext.ForMessage(message));
        if (dryRun.Error is not null)
        {
            return $"Not saved. {dryRun.Error}";
        }

        return await scriptService.UpdateAsync(guildId, id, code, ChatToolHelper.GetString(arguments, "description"))
            ? $"Updated script #{id}. {ScriptToolHelper.DescribeDryRun(dryRun)}"
            : $"There is no script #{id}.";
    }
}

public sealed class ListScriptsTool(ScriptService scriptService) : IChatFunctionTool
{
    public string Name => "list_scripts";
    public string Description => "Lists this server's scripts with their code, stored state and any errors.";
    public object Parameters => new { type = "object", properties = new { } };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (message.GuildId is not { } guildId)
        {
            return "Scripts only exist in servers.";
        }

        var scripts = await scriptService.GetScriptsAsync(guildId);
        return scripts.Count == 0
            ? "There are no scripts."
            : string.Join(Environment.NewLine + Environment.NewLine, scripts.Select(Describe));
    }

    private static string Describe(BotScript script)
    {
        var lines = new List<string> { $"Script #{script.Id}: {script.Description} (by <@{script.CreatedById}>)" };
        if (script.ExpiresAt is { } expiresAt)
        {
            lines.Add($"Expires <t:{expiresAt.ToUnixTimeSeconds()}:R>");
        }

        if (script.IsDisabled)
        {
            lines.Add("DISABLED by hand. Turn it back on with set_script_enabled.");
        }
        else if (script.FailureCount >= ScriptService.MaxFailures)
        {
            lines.Add($"DISABLED after {script.FailureCount} failures in a row. Fix it with update_script, or turn it back on with set_script_enabled if the cause is gone.");
        }

        if (script.LastError is not null)
        {
            lines.Add($"Last error ({script.FailureCount} in a row): {script.LastError}");
        }

        lines.Add($"State: {script.State}");
        lines.Add($"Code:{Environment.NewLine}{script.Code}");
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class DeleteScriptTool(ScriptService scriptService) : IChatFunctionTool
{
    public string Name => "delete_script";

    public string Description =>
        "Deletes a script by id. Only works when the person asking has the Manage Server permission.";

    public object Parameters => new
    {
        type = "object",
        properties = new { id = new { type = "integer" } },
        required = new[] { "id" },
    };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (ChatToolHelper.Deny(message, Permissions.ManageGuild, "Manage Server") is { } denial)
        {
            return denial;
        }

        if (ChatToolHelper.GetInt(arguments, "id") is not { } id)
        {
            return "id is required.";
        }

        return await scriptService.DeleteAsync(message.GuildId!.Value, id)
            ? $"Deleted script #{id}."
            : $"There is no script #{id}.";
    }
}

public sealed class SetScriptEnabledTool(ScriptService scriptService) : IChatFunctionTool
{
    public string Name => "set_script_enabled";

    public string Description =>
        "Turns a script off without deleting it, or back on. Turning it on also clears its failures, so it revives " +
        "a script that stopped after failing too often. Only works when the person asking has the Manage Server permission.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            id = new { type = "integer" },
            enabled = new { type = "boolean" },
        },
        required = new[] { "id", "enabled" },
    };

    public async Task<string> InvokeAsync(JsonElement arguments, Message message, CancellationToken cancellationToken)
    {
        if (ChatToolHelper.Deny(message, Permissions.ManageGuild, "Manage Server") is { } denial)
        {
            return denial;
        }

        if (ChatToolHelper.GetInt(arguments, "id") is not { } id ||
            ChatToolHelper.GetBool(arguments, "enabled") is not { } enabled)
        {
            return "id and enabled are required.";
        }

        return await scriptService.SetEnabledAsync(message.GuildId!.Value, id, enabled)
            ? $"{(enabled ? "Enabled" : "Disabled")} script #{id}."
            : $"There is no script #{id}.";
    }
}

internal static class ScriptToolHelper
{
    public static string DescribeDryRun(ScriptRunOutcome dryRun) =>
        dryRun.Actions.Count == 0
            ? "Dry run on the current message: no actions."
            : $"Dry run on the current message would: {string.Join("; ", dryRun.Actions)}.";
}
