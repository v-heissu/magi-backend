using System.Text.Json;
using Magi.Api.Auth;
using Magi.Api.Contracts;
using Magi.Api.Data;
using Magi.Api.Deliberations;
using Magi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Magi.Api.Endpoints;

public static class DeliberationEndpoints
{
    private const int MaxPropositionLength = 2000;
    private const int MaxContextLength = 8000;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    public static void MapDeliberationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/deliberations").RequireAuthorization();

        group.MapPost("/", CreateAsync).RequireRateLimiting(RateLimitPolicies.Deliberations);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
        group.MapGet("/{id:guid}/events", StreamAsync);
    }

    private static async Task<IResult> CreateAsync(
        CreateDeliberationRequest body,
        HttpContext http,
        MagiDbContext db,
        DeliberationQueue queue,
        DeliberationEventHub hub)
    {
        var errors = new Dictionary<string, string[]>();
        var proposition = body.Proposition?.Trim() ?? string.Empty;
        var context = string.IsNullOrWhiteSpace(body.Context) ? null : body.Context.Trim();

        if (proposition.Length is < 3 or > MaxPropositionLength)
        {
            errors["proposition"] = [$"The proposition must be between 3 and {MaxPropositionLength} characters"];
        }

        if (context is { Length: > MaxContextLength })
        {
            errors["context"] = [$"The context must be at most {MaxContextLength} characters"];
        }

        ResolutionMode? mode = (body.Mode ?? "majority").ToLowerInvariant() switch
        {
            "majority" => ResolutionMode.Majority,
            "unanimous" => ResolutionMode.Unanimous,
            _ => null,
        };
        if (mode is null)
        {
            errors["mode"] = ["Mode must be 'majority' or 'unanimous'"];
        }

        var rounds = body.Rounds ?? 2;
        if (rounds is < 1 or > 2)
        {
            errors["rounds"] = ["Rounds must be 1 or 2"];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var deliberation = new Deliberation
        {
            UserId = http.User.UserId(),
            Proposition = proposition,
            Context = context,
            Mode = mode!.Value,
            MaxRounds = rounds,
        };
        db.Deliberations.Add(deliberation);
        await db.SaveChangesAsync();

        // Open the live stream before queueing, so no event can be published before subscribers can see it.
        hub.Open(deliberation.Id, new MagiEvent("snapshot", deliberation.ToDto()));
        queue.Enqueue(deliberation.Id);

        return Results.Accepted($"/api/deliberations/{deliberation.Id}", new CreatedResponse(deliberation.Id));
    }

    private static async Task<IResult> ListAsync(HttpContext http, MagiDbContext db, int? page, int? pageSize)
    {
        var p = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 100);
        var query = db.Deliberations.AsNoTracking().Where(d => d.UserId == http.User.UserId());

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip((p - 1) * size)
            .Take(size)
            .ToListAsync();

        return Results.Ok(new PagedResponse<DeliberationSummaryDto>(
            items.Select(d => d.ToSummaryDto()).ToList(), p, size, total));
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, MagiDbContext db)
    {
        var deliberation = await LoadOwnedAsync(db, id, http.User.UserId(), includeVotes: true);
        return deliberation is null ? Results.NotFound() : Results.Ok(deliberation.ToDto());
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, MagiDbContext db)
    {
        var deliberation = await LoadOwnedAsync(db, id, http.User.UserId(), includeVotes: false);
        if (deliberation is null)
        {
            return Results.NotFound();
        }

        if (deliberation.Status is DeliberationStatus.Pending or DeliberationStatus.Running)
        {
            return Results.Conflict(new { error = "A deliberation in progress cannot be deleted" });
        }

        db.Deliberations.Remove(deliberation);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    /// <summary>
    /// Server-Sent Events. A live deliberation replays its full history then follows it;
    /// a finished one sends a single snapshot. Either way the stream ends with an "end" event.
    /// </summary>
    private static async Task StreamAsync(
        Guid id,
        HttpContext http,
        MagiDbContext db,
        DeliberationEventHub hub,
        CancellationToken ct)
    {
        var deliberation = await LoadOwnedAsync(db, id, http.User.UserId(), includeVotes: true);
        if (deliberation is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        using var subscription = hub.Subscribe(id);
        if (subscription is null)
        {
            await WriteEventAsync(http.Response, new MagiEvent("snapshot", deliberation.ToDto()), ct);
            await WriteEventAsync(http.Response, new MagiEvent("end", new { }), ct);
            return;
        }

        var reader = subscription.Reader;
        try
        {
            while (true)
            {
                // Wait for the next event, sending a comment line as heartbeat so proxies keep the connection open.
                var waitTask = reader.WaitToReadAsync(ct).AsTask();
                while (await Task.WhenAny(waitTask, Task.Delay(HeartbeatInterval, ct)) != waitTask)
                {
                    await http.Response.WriteAsync(": ping\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }

                if (!await waitTask)
                {
                    break;
                }

                while (reader.TryRead(out var evt))
                {
                    await WriteEventAsync(http.Response, evt, ct);
                }
            }

            await WriteEventAsync(http.Response, new MagiEvent("end", new { }), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away: nothing to do, the subscription is disposed by `using`.
        }
    }

    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    private static async Task WriteEventAsync(HttpResponse response, MagiEvent evt, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(evt.Data, evt.Data.GetType(), SseJson);
        await response.WriteAsync($"event: {evt.Type}\ndata: {payload}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static Task<Deliberation?> LoadOwnedAsync(MagiDbContext db, Guid id, Guid userId, bool includeVotes)
    {
        IQueryable<Deliberation> query = db.Deliberations;
        if (includeVotes)
        {
            query = query.Include(d => d.Votes).AsNoTracking();
        }

        return query.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId);
    }
}
