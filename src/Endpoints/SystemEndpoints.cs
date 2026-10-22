using Magi.Api.Contracts;
using Magi.Api.Cores;

namespace Magi.Api.Endpoints;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // Which cores are wired to a provider. Never exposes keys, only whether one is set.
        app.MapGet("/api/system/cores", (IEnumerable<IMagiCore> cores) =>
            Results.Ok(cores
                .OrderBy(c => c.Definition.Id)
                .Select(c => new CoreStatusDto(
                    c.Definition.Id.Wire(),
                    c.Definition.Designation,
                    c.Definition.Aspect,
                    c.Provider,
                    c.Model,
                    c.IsConfigured))
                .ToList()))
            .RequireAuthorization();
    }
}
