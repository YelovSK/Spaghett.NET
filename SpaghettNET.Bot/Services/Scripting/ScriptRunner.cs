using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using SpaghettNET.Bot.Services.ChatTools;
using JsonParser = Jint.Native.Json.JsonParser;
using JsSerializer = Jint.Native.Json.JsonSerializer;

namespace SpaghettNET.Bot.Services.Scripting;

public enum ScriptActionKind
{
    Reply,
    Send,
    React,
    DeleteMessage,
    Timeout,
    LlmReply,
    LlmSend,
}

/// <summary>Something a script asked for. Scripts only record actions, the bot carries them out afterwards.</summary>
public sealed record ScriptAction(
    int ScriptId,
    ScriptActionKind Kind,
    string? Text = null,
    ulong? UserId = null,
    int? Seconds = null,
    ulong? ChannelId = null)
{
    public override string ToString() => Kind switch
    {
        ScriptActionKind.Reply => $"reply \"{Text}\"",
        ScriptActionKind.Send => $"send \"{Text}\" to <#{ChannelId}>",
        ScriptActionKind.React => $"react {Text}",
        ScriptActionKind.DeleteMessage => "delete the message",
        ScriptActionKind.Timeout => Seconds > 0 ? $"time out <@{UserId}> for {Seconds}s" : $"lift the timeout on <@{UserId}>",
        ScriptActionKind.LlmReply => $"LLM reply following \"{Text}\"",
        ScriptActionKind.LlmSend => $"LLM message in <#{ChannelId}> following \"{Text}\"",
        _ => Kind.ToString(),
    };
}

/// <param name="Error">Set when the script threw or broke a limit. Its actions and state changes are then discarded.</param>
public sealed record ScriptRunOutcome(IReadOnlyList<ScriptAction> Actions, string State, string? Error);

/// <summary>Runs one script for one event in a fresh, locked-down engine.</summary>
public static partial class ScriptRunner
{
    private const int MaxActions = 10;
    private const int MaxStateLength = 16_384;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private const string Prelude = """
        const on = __on;
        const api = Object.freeze({
            reply: __reply,
            send: __send,
            react: __react,
            deleteMessage: __deleteMessage,
            timeout: __timeout,
            llmReply: __llmReply,
            llmSend: __llmSend,
            store: Object.freeze({ get: __storeGet, set: __storeSet }),
        });
        """;

    [GeneratedRegex(@"^<a?:(?<name>\w+):(?<id>\d+)>$|^[<:]?(?<name>\w+)[>:]?$")]
    private static partial Regex EmoteRegex();

    /// <exception cref="Exception">The code has a syntax error; the message says where.</exception>
    public static Prepared<Acornima.Ast.Script> Compile(string code) => Engine.PrepareScript(code, strict: true);

    /// <param name="requireHandler">Fail when the script registers no handlers at all, to catch scripts that do nothing.</param>
    public static ScriptRunOutcome Run(
        int scriptId,
        Prepared<Acornima.Ast.Script> script,
        string state,
        string eventName,
        object payload,
        ScriptEventContext context,
        bool requireHandler = false)
    {
        var actions = new List<ScriptAction>();
        var store = JsonNode.Parse(state)!.AsObject();
        var handlers = new List<(string Event, JsValue Handler)>();
        var handling = false;

        void Record(ScriptAction action)
        {
            if (!handling)
            {
                throw new ScriptApiException("api can only be used inside on(...) handlers.");
            }

            if (actions.Count >= MaxActions)
            {
                throw new ScriptApiException($"A script can do at most {MaxActions} actions per event.");
            }

            actions.Add(action);
        }

        void RequireMessage(string call)
        {
            if (context.Message is null)
            {
                throw new ScriptApiException($"api.{call} only works in message handlers.");
            }
        }

        try
        {
            using var engine = new Engine(options => options
                .Strict()
                .TimeoutInterval(TimeSpan.FromMilliseconds(50))
                .MaxStatements(100_000)
                .LimitMemory(8_000_000)
                .LimitRecursion(100));

            engine.SetValue("__on", new Action<string, JsValue>((name, handler) =>
            {
                if (!ScriptEvents.All.Contains(name))
                {
                    throw new ScriptApiException(
                        $"Unknown event \"{name}\". Known events: {string.Join(", ", ScriptEvents.All)}.");
                }

                handlers.Add((name, handler));
            }));
            engine.SetValue("__reply", new Action<string>(text =>
            {
                RequireMessage("reply");
                Record(new ScriptAction(scriptId, ScriptActionKind.Reply, RequireText(text, "reply")));
            }));
            engine.SetValue("__send", new Action<string, string>((channel, text) =>
                Record(new ScriptAction(
                    scriptId,
                    ScriptActionKind.Send,
                    RequireText(text, "send"),
                    ChannelId: ResolveTextChannel(context.Guild, channel, "send")))));
            engine.SetValue("__llmSend", new Action<string, string>((channel, instruction) =>
            {
                RequireMessage("llmSend");
                Record(new ScriptAction(
                    scriptId,
                    ScriptActionKind.LlmSend,
                    RequireText(instruction, "llmSend"),
                    ChannelId: ResolveTextChannel(context.Guild, channel, "llmSend")));
            }));
            engine.SetValue("__react", new Action<string>(emoji =>
            {
                RequireMessage("react");
                Record(new ScriptAction(scriptId, ScriptActionKind.React, ResolveEmoji(context.Guild, emoji)));
            }));
            engine.SetValue("__deleteMessage", new Action(() =>
            {
                RequireMessage("deleteMessage");
                Record(new ScriptAction(scriptId, ScriptActionKind.DeleteMessage));
            }));
            engine.SetValue("__timeout", new Action<string, double>((userId, seconds) =>
            {
                if (!ulong.TryParse(userId, out var id))
                {
                    throw new ScriptApiException("api.timeout needs a user id string, like msg.author.id.");
                }

                if (seconds is < 0 or > ChatToolHelper.MaxTimeoutSeconds)
                {
                    throw new ScriptApiException($"api.timeout seconds must be between 0 and {ChatToolHelper.MaxTimeoutSeconds}.");
                }

                Record(new ScriptAction(scriptId, ScriptActionKind.Timeout, UserId: id, Seconds: (int)seconds));
            }));
            engine.SetValue("__llmReply", new Action<string>(instruction =>
            {
                RequireMessage("llmReply");
                Record(new ScriptAction(scriptId, ScriptActionKind.LlmReply, RequireText(instruction, "llmReply")));
            }));
            engine.SetValue("__storeGet", new Func<string, JsValue>(key =>
                store[key] is { } value ? new JsonParser(engine).Parse(value.ToJsonString()) : JsValue.Undefined));
            engine.SetValue("__storeSet", new Action<string, JsValue>((key, value) =>
            {
                if (value.IsNull() || value.IsUndefined())
                {
                    store.Remove(key);
                    return;
                }

                var json = new JsSerializer(engine).Serialize(value);
                store[key] = json.IsUndefined()
                    ? throw new ScriptApiException("api.store.set only takes JSON values.")
                    : JsonNode.Parse(json.AsString());
            }));

            engine.Execute(Prelude);
            engine.Execute(script);

            if (requireHandler && handlers.Count == 0)
            {
                return new ScriptRunOutcome([], state, "The script registers no handlers. Use on(\"message\", msg => { ... }).");
            }

            handling = true;
            var payloadValue = new JsonParser(engine).Parse(JsonSerializer.Serialize(payload, PayloadJsonOptions));
            foreach (var (_, handler) in handlers.Where(handler => handler.Event == eventName))
            {
                engine.Invoke(handler, payloadValue);
            }

            var newState = store.ToJsonString();
            return newState.Length > MaxStateLength
                ? new ScriptRunOutcome([], state, $"api.store grew past {MaxStateLength} characters.")
                : new ScriptRunOutcome(actions, newState, null);
        }
        catch (Exception ex)
        {
            return new ScriptRunOutcome([], state, ex.Message);
        }
    }

    private static string RequireText(string? text, string call) =>
        string.IsNullOrWhiteSpace(text) ? throw new ScriptApiException($"api.{call} needs non-empty text.") : text;

    private static ulong ResolveTextChannel(NetCord.Gateway.Guild guild, string channel, string call)
    {
        var (resolved, error) = ChatToolHelper.ResolveChannel(guild, RequireText(channel, call));
        return resolved is NetCord.TextChannel
            ? resolved.Id
            : throw new ScriptApiException(error ?? $"api.{call} can't post in {channel}, it isn't a text channel.");
    }

    /// <returns>name:id for a server emote, or the input for a unicode emoji.</returns>
    private static string ResolveEmoji(NetCord.Gateway.Guild guild, string emoji)
    {
        emoji = RequireText(emoji, "react").Trim();
        if (EmoteRegex().Match(emoji) is not { Success: true } match)
        {
            return emoji;
        }

        if (match.Groups["id"].Success)
        {
            return $"{match.Groups["name"].Value}:{match.Groups["id"].Value}";
        }

        var name = match.Groups["name"].Value;
        return guild.Emojis.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.InvariantCultureIgnoreCase)) is { } guildEmoji
            ? $"{guildEmoji.Name}:{guildEmoji.Id}"
            : throw new ScriptApiException($"No server emote named \"{name}\". Use a unicode emoji or an existing emote name.");
    }

    private sealed class ScriptApiException(string message) : Exception(message);
}
