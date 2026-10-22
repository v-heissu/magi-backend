using System.Collections.Concurrent;
using Magi.Api.Configuration;
using Magi.Api.Data;
using Magi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Magi.Api.Providers;

public enum ValueSource
{
    None,
    Environment,
    Database,
}

/// <summary>Effective settings of one core plus where each value came from.</summary>
public sealed record EffectiveProvider(
    ProviderSettings Settings,
    ValueSource KeySource,
    string? KeyHint,
    ValueSource ModelSource,
    ValueSource EffortSource,
    DateTime? UpdatedAt);

/// <summary>
/// Source of truth for provider settings at runtime: .env values overridden by the DB rows.
/// Cores read from here on every call, so a change saved from the settings page applies to the
/// next deliberation without restarting the container.
/// </summary>
public sealed class ProviderConfigStore(
    MagiSettings settings,
    SecretProtector protector,
    IServiceScopeFactory scopes,
    ILogger<ProviderConfigStore> logger)
{
    private readonly ConcurrentDictionary<CoreId, EffectiveProvider> _current = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EffectiveProvider Get(CoreId core) =>
        _current.TryGetValue(core, out var value) ? value : Merge(core, null);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MagiDbContext>();
        var rows = await db.ProviderSettings.AsNoTracking().ToListAsync(ct);

        foreach (var info in ProviderCatalog.All)
        {
            _current[info.Core] = Merge(info.Core, rows.FirstOrDefault(r => r.Core == info.Core));
        }
    }

    /// <param name="apiKey">New key, or null to keep the current one.</param>
    /// <param name="clearKey">Drop the saved key and fall back to the .env one.</param>
    public async Task<EffectiveProvider> SaveAsync(
        CoreId core, string? apiKey, bool clearKey, string? model, string? effort, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MagiDbContext>();
            var row = await db.ProviderSettings.FirstOrDefaultAsync(r => r.Core == core, ct);
            if (row is null)
            {
                row = new ProviderSetting { Core = core };
                db.ProviderSettings.Add(row);
            }

            if (clearKey)
            {
                row.ApiKeyCipher = null;
                row.ApiKeyHint = null;
            }
            else if (apiKey is not null)
            {
                row.ApiKeyCipher = protector.Protect(apiKey);
                row.ApiKeyHint = apiKey.Length > 8 ? apiKey[^4..] : null;
            }

            // A value equal to the .env one is stored as null, so later .env edits still apply.
            var env = EnvDefaults(core);
            row.Model = model is null || model == env.Model ? null : model;
            row.Effort = effort is null || effort == env.Effort ? null : effort;
            row.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);

            var merged = Merge(core, row);
            _current[core] = merged;
            logger.LogInformation("Provider settings for {Core} updated (model {Model}, key from {KeySource})",
                core, merged.Settings.Model, merged.KeySource);
            return merged;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ProviderSettings EnvDefaults(CoreId core) => core switch
    {
        CoreId.Melchior => settings.Anthropic,
        CoreId.Balthasar => settings.OpenAi,
        CoreId.Casper => settings.Gemini,
        _ => throw new ArgumentOutOfRangeException(nameof(core)),
    };

    private EffectiveProvider Merge(CoreId core, ProviderSetting? row)
    {
        var env = EnvDefaults(core);

        string? dbKey = null;
        if (row?.ApiKeyCipher is { } cipher)
        {
            dbKey = protector.Unprotect(cipher);
            if (dbKey is null)
            {
                logger.LogWarning(
                    "Saved API key for {Core} cannot be decrypted (SETTINGS_ENCRYPTION_KEY changed?): using .env", core);
            }
        }

        var keySource = dbKey is not null ? ValueSource.Database
            : env.IsConfigured ? ValueSource.Environment
            : ValueSource.None;
        var key = dbKey ?? env.ApiKey;
        var hint = dbKey is not null ? row!.ApiKeyHint : Hint(env.ApiKey);

        return new EffectiveProvider(
            env with
            {
                ApiKey = key,
                Model = row?.Model ?? env.Model,
                Effort = row?.Effort ?? env.Effort,
            },
            keySource,
            hint,
            row?.Model is not null ? ValueSource.Database : ValueSource.Environment,
            row?.Effort is not null ? ValueSource.Database : ValueSource.Environment,
            row?.UpdatedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null);
    }

    private static string? Hint(string? key) => key is { Length: > 8 } ? key[^4..] : null;
}
