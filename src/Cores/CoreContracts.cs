using Magi.Api.Models;

namespace Magi.Api.Cores;

/// <summary>A single MAGI unit backed by one LLM provider.</summary>
public interface IMagiCore
{
    CoreDefinition Definition { get; }
    string Provider { get; }
    string Model { get; }
    bool IsConfigured { get; }

    /// <summary>Asks the core for its vote. Throws <see cref="CoreFailureException"/> on any failure.</summary>
    Task<CoreAnswer> DeliberateAsync(CoreRequest request, CancellationToken ct);
}

/// <param name="Peers">Empty in round 1; in later rounds the other cores' previous positions.</param>
/// <param name="OwnPrevious">This core's own position from the previous round, if any.</param>
public sealed record CoreRequest(
    string Proposition,
    string? Context,
    int Round,
    PeerPosition? OwnPrevious,
    IReadOnlyList<PeerPosition> Peers);

public sealed record PeerPosition(
    string Designation,
    Verdict Verdict,
    int Confidence,
    string Summary,
    string Reasoning,
    IReadOnlyList<string> Conditions);

public sealed record CoreAnswer(
    Verdict Verdict,
    int Confidence,
    string Summary,
    string Reasoning,
    IReadOnlyList<string> Conditions,
    int InputTokens,
    int OutputTokens);

/// <summary>Any provider error, refusal, timeout or malformed answer. The message is safe to show to the user.</summary>
public sealed class CoreFailureException(string message, Exception? inner = null) : Exception(message, inner);
