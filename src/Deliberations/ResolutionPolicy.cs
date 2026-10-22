using Magi.Api.Models;

namespace Magi.Api.Deliberations;

public sealed record Tally(Resolution Resolution, int Approve, int Reject);

/// <summary>
/// Turns the cores' final votes into a resolution. The thresholds are always out of the three
/// seats, not out of the votes received: a core that failed counts as neither approval nor rejection,
/// so a missing core can make the outcome INCONCLUSIVE but can never tip it.
/// </summary>
public static class ResolutionPolicy
{
    public const int Seats = 3;

    public static Tally Resolve(ResolutionMode mode, IEnumerable<Verdict?> finalVotes)
    {
        var votes = finalVotes.ToList();
        var approve = votes.Count(v => v == Verdict.Approve);
        var reject = votes.Count(v => v == Verdict.Reject);

        var resolution = mode switch
        {
            ResolutionMode.Unanimous when approve == Seats => Resolution.Approved,
            // Under unanimity a single rejection is a veto.
            ResolutionMode.Unanimous when reject > 0 => Resolution.Rejected,
            ResolutionMode.Unanimous => Resolution.Inconclusive,

            _ when approve >= 2 => Resolution.Approved,
            _ when reject >= 2 => Resolution.Rejected,
            _ => Resolution.Inconclusive,
        };

        return new Tally(resolution, approve, reject);
    }

    /// <summary>True when every seat voted and all votes agree: no further round is useful.</summary>
    public static bool IsUnanimous(IReadOnlyCollection<Verdict?> votes) =>
        votes.Count == Seats && votes.All(v => v is not null) && votes.Distinct().Count() == 1;
}
