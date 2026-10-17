using Jint;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SpaghettNET.Persistence;
using SpaghettNET.Persistence.Models;

namespace SpaghettNET.Bot.Services.Scripting;

public sealed class ScriptService(
    IDbContextFactory<BotContext> dbContextFactory,
    ILogger<ScriptService> logger)
{
    public const int MaxFailures = 5;

    // Runs are serialized so concurrent events can't race on a script's state.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<LoadedScript>? _scripts;

    /// <summary>Runs every active script's handlers for the event and returns what they asked for.</summary>
    public async Task<IReadOnlyList<ScriptAction>> RunAsync(string eventName, object payload, ScriptEventContext context)
    {
        await _lock.WaitAsync();
        try
        {
            var actions = new List<ScriptAction>();
            foreach (var loaded in await GetLoadedAsync())
            {
                var script = loaded.Script;
                if (script.GuildId != context.Guild.Id ||
                    IsExpired(script) ||
                    script.IsDisabled ||
                    script.FailureCount >= MaxFailures)
                {
                    continue;
                }

                var outcome = ScriptRunner.Run(script.Id, loaded.Prepared, script.State, eventName, payload, context);
                if (outcome.Error is not null)
                {
                    logger.LogWarning("Script {ScriptId} failed on {Event}: {Error}", script.Id, eventName, outcome.Error);
                    await SaveRunAsync(script, script.State, script.FailureCount + 1, outcome.Error);
                    continue;
                }

                actions.AddRange(outcome.Actions);
                if (outcome.State != script.State || script.FailureCount > 0)
                {
                    await SaveRunAsync(script, outcome.State, 0, script.LastError);
                }
            }

            return actions;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Compiles the code and runs it once against an event without saving anything or acting on Discord.</summary>
    public static ScriptRunOutcome DryRun(string code, string state, string eventName, object payload, ScriptEventContext context)
    {
        Prepared<Acornima.Ast.Script> prepared;
        try
        {
            prepared = ScriptRunner.Compile(code);
        }
        catch (Exception ex)
        {
            return new ScriptRunOutcome([], state, $"Syntax error: {ex.Message}");
        }

        return ScriptRunner.Run(0, prepared, state, eventName, payload, context, requireHandler: true);
    }

    /// <summary>Failing scripts stay listed so the error can be read and fixed; only expired ones are hidden.</summary>
    public async Task<IReadOnlyList<BotScript>> GetScriptsAsync(ulong guildId) =>
        (await GetLoadedAsync())
            .Select(loaded => loaded.Script)
            .Where(script => script.GuildId == guildId && !IsExpired(script))
            .ToArray();

    public Task AddAsync(BotScript script) =>
        WriteAsync(async dbContext =>
        {
            dbContext.BotScripts.Add(script);
            return await dbContext.SaveChangesAsync();
        });

    /// <summary>Replaces the code but keeps the stored state, and gives the script a fresh set of failures.</summary>
    public async Task<bool> UpdateAsync(ulong guildId, int scriptId, string code, string? description) =>
        await WriteAsync(dbContext => dbContext.BotScripts
            .Where(script => script.GuildId == guildId && script.Id == scriptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(script => script.Code, code)
                .SetProperty(script => script.Description, script => description ?? script.Description)
                .SetProperty(script => script.FailureCount, 0)
                .SetProperty(script => script.LastError, (string?)null))) > 0;

    /// <summary>Enabling also gives the script a fresh set of failures, so it can recover from being stopped by them.</summary>
    public async Task<bool> SetEnabledAsync(ulong guildId, int scriptId, bool enabled) =>
        await WriteAsync(dbContext => dbContext.BotScripts
            .Where(script => script.GuildId == guildId && script.Id == scriptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(script => script.IsDisabled, !enabled)
                .SetProperty(script => script.FailureCount, script => enabled ? 0 : script.FailureCount))) > 0;

    public async Task<bool> DeleteAsync(ulong guildId, int scriptId) =>
        await WriteAsync(dbContext => dbContext.BotScripts
            .Where(script => script.GuildId == guildId && script.Id == scriptId)
            .ExecuteDeleteAsync()) > 0;

    /// <returns>The number of rows the write touched.</returns>
    private async Task<int> WriteAsync(Func<BotContext, Task<int>> write)
    {
        await _lock.WaitAsync();
        try
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync();
            var rows = await write(dbContext);
            await ReloadAsync();
            return rows;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveRunAsync(BotScript script, string state, int failureCount, string? lastError)
    {
        script.State = state;
        script.FailureCount = failureCount;
        script.LastError = lastError;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.BotScripts
            .Where(candidate => candidate.Id == script.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.State, state)
                .SetProperty(candidate => candidate.FailureCount, failureCount)
                .SetProperty(candidate => candidate.LastError, lastError));
    }

    private static bool IsExpired(BotScript script) => script.ExpiresAt <= DateTimeOffset.UtcNow;

    private async Task<IReadOnlyList<LoadedScript>> GetLoadedAsync() => _scripts ?? await ReloadAsync();

    private async Task<IReadOnlyList<LoadedScript>> ReloadAsync()
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var scripts = await dbContext.BotScripts.ToListAsync();

        // SQLite cannot compare DateTimeOffset, so expired scripts are cleaned up here instead of in the query.
        var expired = scripts.Where(IsExpired).ToArray();
        if (expired.Length > 0)
        {
            dbContext.BotScripts.RemoveRange(expired);
            await dbContext.SaveChangesAsync();
        }

        var loaded = new List<LoadedScript>();
        foreach (var script in scripts.Except(expired))
        {
            try
            {
                loaded.Add(new LoadedScript(script, ScriptRunner.Compile(script.Code)));
            }
            catch (Exception ex)
            {
                // Code is checked before it's saved, so this only happens if Jint changes what it accepts.
                logger.LogError(ex, "Script {ScriptId} no longer compiles.", script.Id);
            }
        }

        return _scripts = loaded;
    }

    private sealed record LoadedScript(BotScript Script, Prepared<Acornima.Ast.Script> Prepared);
}
