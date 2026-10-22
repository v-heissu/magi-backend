using System.Text.Json;
using Magi.Api.Cores;
using Magi.Api.Models;

namespace Magi.Api.Contracts;

public sealed record LoginRequest(string Email, string Password);

public sealed record LoginResponse(string Token, DateTime ExpiresAt, string Email);

public sealed record MeResponse(Guid Id, string Email);

public sealed record CreateDeliberationRequest(string Proposition, string? Context, string? Mode, int? Rounds);

public sealed record CreatedResponse(Guid Id);

public sealed record CoreStatusDto(
    string Id,
    string Designation,
    string Aspect,
    string Provider,
    string Model,
    bool Online);

public sealed record VoteDto(
    int Round,
    string Core,
    string Designation,
    string Provider,
    string Model,
    string? Verdict,
    int Confidence,
    string? Summary,
    string? Reasoning,
    IReadOnlyList<string> Conditions,
    bool ChangedVerdict,
    string? Error,
    int LatencyMs,
    int InputTokens,
    int OutputTokens,
    DateTime CreatedAt);

public sealed record DeliberationDto(
    Guid Id,
    string Proposition,
    string? Context,
    string Mode,
    int MaxRounds,
    int RoundsCompleted,
    string Status,
    string? Resolution,
    int ApproveCount,
    int RejectCount,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    IReadOnlyList<VoteDto> Votes);

public sealed record DeliberationSummaryDto(
    Guid Id,
    string Proposition,
    string Mode,
    string Status,
    string? Resolution,
    int ApproveCount,
    int RejectCount,
    DateTime CreatedAt);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <summary>Mapping between entities and the wire format. Enums go out as stable uppercase strings.</summary>
public static class DtoMapper
{
    public static string Wire(this Verdict v) => v == Verdict.Approve ? "APPROVE" : "REJECT";

    public static string Wire(this Resolution r) => r.ToString().ToUpperInvariant();

    public static string Wire(this DeliberationStatus s) => s.ToString().ToUpperInvariant();

    public static string Wire(this ResolutionMode m) => m.ToString().ToUpperInvariant();

    public static string Wire(this CoreId c) => c.ToString().ToLowerInvariant();

    // MySQL hands DateTime back as Kind=Unspecified; all values are stored in UTC, so mark them as such
    // to serialize with a trailing "Z" (otherwise browsers read them as local time).
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is null ? null : Utc(value.Value);

    public static VoteDto ToDto(this CoreVote v) => new(
        v.Round,
        v.Core.Wire(),
        CoreDefinitions.Get(v.Core).Designation,
        v.Provider,
        v.Model,
        v.Verdict?.Wire(),
        v.Confidence,
        v.Summary,
        v.Reasoning,
        JsonSerializer.Deserialize<List<string>>(v.ConditionsJson) ?? [],
        v.ChangedVerdict,
        v.Error,
        v.LatencyMs,
        v.InputTokens,
        v.OutputTokens,
        Utc(v.CreatedAt));

    public static DeliberationDto ToDto(this Deliberation d) => new(
        d.Id,
        d.Proposition,
        d.Context,
        d.Mode.Wire(),
        d.MaxRounds,
        d.RoundsCompleted,
        d.Status.Wire(),
        d.Resolution?.Wire(),
        d.ApproveCount,
        d.RejectCount,
        d.FailureReason,
        Utc(d.CreatedAt),
        Utc(d.CompletedAt),
        d.Votes.OrderBy(v => v.Round).ThenBy(v => v.Core).Select(ToDto).ToList());

    public static DeliberationSummaryDto ToSummaryDto(this Deliberation d) => new(
        d.Id,
        d.Proposition,
        d.Mode.Wire(),
        d.Status.Wire(),
        d.Resolution?.Wire(),
        d.ApproveCount,
        d.RejectCount,
        Utc(d.CreatedAt));
}
