using System.Security.Claims;
using Magi.Api.Configuration;
using Magi.Api.Contracts;
using Magi.Api.Cores;
using Magi.Api.Models;
using Magi.Api.Providers;

namespace Magi.Api.Endpoints;

/// <summary>
/// Settings page: model, effort and API key of each core. Saved values live in the DB, override
/// the .env and take effect on the next deliberation, with no restart.
/// </summary>
public static class SettingsEndpoints
{
    public const string AdminPolicy = "magi-admin";
    private const int MaxKeyLength = 500;

    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/providers").RequireAuthorization(AdminPolicy);

        group.MapGet("/", (ProviderConfigStore store) =>
            Results.Ok(ProviderCatalog.All.Select(p => ToDto(p, store)).ToList()));

        group.MapGet("/{core}/models", async (string core, ProviderConfigStore store, ModelLister lister, CancellationToken ct) =>
        {
            if (!TryParseCore(core, out var id))
            {
                return Results.NotFound();
            }

            var result = await lister.ListAsync(id, store.Get(id).Settings, ct);
            return Results.Ok(new ModelListDto(
                result.Models.Select(m => new ModelOptionDto(m.Id, m.Label)).ToList(), result.Live, result.Error));
        });

        group.MapPut("/{core}", async (
            string core,
            UpdateProviderRequest body,
            ProviderConfigStore store,
            ModelLister lister,
            CancellationToken ct) =>
        {
            if (!TryParseCore(core, out var id))
            {
                return Results.NotFound();
            }

            var info = ProviderCatalog.Get(id);
            var errors = new Dictionary<string, string[]>();

            var model = body.Model?.Trim();
            if (string.IsNullOrEmpty(model) || model.Length > 100 || model.Any(char.IsWhiteSpace))
            {
                errors["model"] = ["Modello non valido"];
            }

            var effort = body.Effort?.Trim().ToLowerInvariant();
            if (effort is null || !info.EffortOptions.Contains(effort))
            {
                errors["effort"] = [$"Valori ammessi: {string.Join(", ", info.EffortOptions)}"];
            }

            var apiKey = string.IsNullOrWhiteSpace(body.ApiKey) ? null : body.ApiKey.Trim();
            if (apiKey is not null && (apiKey.Length > MaxKeyLength || apiKey.Any(char.IsWhiteSpace)))
            {
                errors["apiKey"] = ["Chiave API non valida"];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            // Check a new key against the provider before saving it, so a typo does not take the core offline.
            if (apiKey is not null)
            {
                var probe = store.Get(id).Settings with { ApiKey = apiKey };
                var check = await lister.ListAsync(id, probe, ct);
                if (check.KeyRejected)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["apiKey"] = [$"{info.Provider} ha rifiutato la chiave"],
                    });
                }
            }

            await store.SaveAsync(id, apiKey, body.ClearApiKey == true, model, effort, ct);
            return Results.Ok(ToDto(info, store));
        });
    }

    public static void AddMagiAdminPolicy(this IServiceCollection services, MagiSettings settings)
    {
        var adminEmail = settings.AdminEmail?.Trim().ToLowerInvariant();
        services.AddAuthorizationBuilder().AddPolicy(AdminPolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => adminEmail is not null
                                     && string.Equals(ctx.User.FindFirstValue("email"), adminEmail,
                                         StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryParseCore(string value, out CoreId core) =>
        Enum.TryParse(value, ignoreCase: true, out core) && Enum.IsDefined(core);

    private static ProviderConfigDto ToDto(ProviderInfo info, ProviderConfigStore store)
    {
        var current = store.Get(info.Core);
        var env = store.EnvDefaults(info.Core);
        var definition = CoreDefinitions.Get(info.Core);
        return new ProviderConfigDto(
            info.Core.Wire(),
            definition.Designation,
            definition.Aspect,
            info.Provider,
            current.Settings.Model,
            current.Settings.Effort,
            info.EffortOptions,
            current.Settings.IsConfigured,
            current.KeyHint,
            Wire(current.KeySource),
            info.KeyEnvVar,
            env.IsConfigured,
            env.Model,
            env.Effort,
            current.UpdatedAt);
    }

    private static string Wire(ValueSource source) => source switch
    {
        ValueSource.Database => "database",
        ValueSource.Environment => "environment",
        _ => "none",
    };
}
