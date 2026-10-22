using System.Diagnostics;
using System.Text.Json;
using Magi.Api.Configuration;
using Magi.Api.Contracts;
using Magi.Api.Cores;
using Magi.Api.Data;
using Magi.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Magi.Api.Deliberations;

/// <summary>
/// Runs one deliberation end to end:
/// round 1 = the three cores vote independently and in parallel;
/// round 2+ = only if the vote was not unanimous, each core reads the others' arguments and may revise.
/// The final resolution uses each core's latest valid vote.
/// </summary>
public sealed class DeliberationEngine(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IMagiCore> cores,
    DeliberationEventHub hub,
    MagiSettings settings,
    ILogger<DeliberationEngine> logger)
{
    private readonly IReadOnlyList<IMagiCore> _cores = cores.OrderBy(c => c.Definition.Id).ToList();

    public async Task RunAsync(Guid deliberationId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MagiDbContext>();

        var deliberation = await db.Deliberations.FirstOrDefaultAsync(d => d.Id == deliberationId, ct);
        if (deliberation is null)
        {
            hub.Complete(deliberationId);
            return;
        }

        try
        {
            deliberation.Status = DeliberationStatus.Running;
            await db.SaveChangesAsync(ct);

            // Latest valid vote of each core, carried across rounds.
            var latest = new Dictionary<CoreId, CoreVote>();

            for (var round = 1; round <= deliberation.MaxRounds; round++)
            {
                if (round > 1 && !NeedsAnotherRound(latest))
                {
                    break;
                }

                hub.Publish(deliberationId, new MagiEvent("round_started", new { round }));

                // Requests are built before starting the tasks, so `latest` is only read here and
                // only written after all cores of the round have answered.
                var calls = _cores
                    .Select(core => RunCoreAsync(core, BuildRequest(deliberation, round, core, latest), deliberation.Id, latest, ct))
                    .ToList();
                var votes = await Task.WhenAll(calls);

                db.CoreVotes.AddRange(votes);
                foreach (var vote in votes.Where(v => v.Verdict is not null))
                {
                    latest[vote.Core] = vote;
                }

                var tally = ResolutionPolicy.Resolve(deliberation.Mode, latest.Values.Select(v => v.Verdict));
                deliberation.RoundsCompleted = round;
                deliberation.ApproveCount = tally.Approve;
                deliberation.RejectCount = tally.Reject;
                await db.SaveChangesAsync(ct);

                hub.Publish(deliberationId, new MagiEvent("round_completed",
                    new { round, approve = tally.Approve, reject = tally.Reject }));
            }

            var final = ResolutionPolicy.Resolve(deliberation.Mode, latest.Values.Select(v => v.Verdict));
            deliberation.Resolution = final.Resolution;
            deliberation.ApproveCount = final.Approve;
            deliberation.RejectCount = final.Reject;
            deliberation.CompletedAt = DateTime.UtcNow;

            if (latest.Count == 0)
            {
                deliberation.Status = DeliberationStatus.Failed;
                deliberation.FailureReason = "No core produced a valid vote";
            }
            else
            {
                deliberation.Status = DeliberationStatus.Completed;
            }

            await db.SaveChangesAsync(ct);
            await PublishFinalAsync(db, deliberation, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown: leave the record as is; startup recovery marks it interrupted.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deliberation {Id} failed", deliberationId);
            deliberation.Status = DeliberationStatus.Failed;
            deliberation.FailureReason = "Internal error during deliberation";
            deliberation.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            hub.Publish(deliberationId, new MagiEvent("failed", new { reason = deliberation.FailureReason }));
        }
        finally
        {
            hub.Complete(deliberationId);
        }
    }

    private static bool NeedsAnotherRound(Dictionary<CoreId, CoreVote> latest)
    {
        // A second round only makes sense when there is disagreement to discuss among at least two voices.
        var verdicts = latest.Values.Select(v => v.Verdict).ToList();
        return verdicts.Count >= 2 && !ResolutionPolicy.IsUnanimous(verdicts);
    }

    private static CoreRequest BuildRequest(
        Deliberation deliberation, int round, IMagiCore core, Dictionary<CoreId, CoreVote> latest)
    {
        if (round == 1)
        {
            return new CoreRequest(deliberation.Proposition, deliberation.Context, round, null, []);
        }

        var own = latest.TryGetValue(core.Definition.Id, out var ownVote) ? ToPosition(ownVote) : null;
        var peers = latest.Values
            .Where(v => v.Core != core.Definition.Id)
            .OrderBy(v => v.Core)
            .Select(ToPosition)
            .ToList();

        return new CoreRequest(deliberation.Proposition, deliberation.Context, round, own, peers);
    }

    private static PeerPosition ToPosition(CoreVote v) => new(
        CoreDefinitions.Get(v.Core).Designation,
        v.Verdict!.Value,
        v.Confidence,
        v.Summary ?? string.Empty,
        v.Reasoning ?? string.Empty,
        JsonSerializer.Deserialize<List<string>>(v.ConditionsJson) ?? []);

    private async Task<CoreVote> RunCoreAsync(
        IMagiCore core,
        CoreRequest request,
        Guid deliberationId,
        IReadOnlyDictionary<CoreId, CoreVote> latest,
        CancellationToken ct)
    {
        var id = core.Definition.Id;
        hub.Publish(deliberationId, new MagiEvent("core_started", new { round = request.Round, core = id.Wire() }));

        var vote = new CoreVote
        {
            DeliberationId = deliberationId,
            Round = request.Round,
            Core = id,
            Provider = core.Provider,
            Model = core.Model,
        };

        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.CoreTimeoutSeconds));

        try
        {
            var answer = await core.DeliberateAsync(request, timeout.Token);
            vote.Verdict = answer.Verdict;
            vote.Confidence = answer.Confidence;
            vote.Summary = answer.Summary;
            vote.Reasoning = answer.Reasoning;
            vote.ConditionsJson = JsonSerializer.Serialize(answer.Conditions);
            vote.InputTokens = answer.InputTokens;
            vote.OutputTokens = answer.OutputTokens;
            vote.ChangedVerdict = latest.TryGetValue(id, out var previous) && previous.Verdict != answer.Verdict;
        }
        catch (CoreFailureException ex)
        {
            logger.LogWarning(ex, "{Core} failed in round {Round}: {Message}", id, request.Round, ex.Message);
            vote.Error = ex.Message;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("{Core} timed out in round {Round}", id, request.Round);
            vote.Error = $"No answer within {settings.CoreTimeoutSeconds}s";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unexpected bug in a provider adapter: record it as a failed vote instead of sinking the whole deliberation.
            logger.LogError(ex, "{Core} crashed in round {Round}", id, request.Round);
            vote.Error = "Internal error in the core adapter";
        }

        vote.LatencyMs = (int)stopwatch.ElapsedMilliseconds;
        hub.Publish(deliberationId, new MagiEvent("core_voted", vote.ToDto()));
        return vote;
    }

    private async Task PublishFinalAsync(MagiDbContext db, Deliberation deliberation, CancellationToken ct)
    {
        await db.Entry(deliberation).Collection(d => d.Votes).LoadAsync(ct);
        hub.Publish(deliberation.Id, new MagiEvent("resolved", deliberation.ToDto()));
    }
}
