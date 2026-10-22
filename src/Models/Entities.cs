namespace Magi.Api.Models;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Deliberation> Deliberations { get; set; } = [];
}

/// <summary>A proposition submitted to the MAGI and the final resolution of the three cores.</summary>
public sealed class Deliberation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public required string Proposition { get; set; }
    public string? Context { get; set; }
    public ResolutionMode Mode { get; set; } = ResolutionMode.Majority;

    /// <summary>1 = independent vote only; 2 = cores see each other's arguments and may revise.</summary>
    public int MaxRounds { get; set; } = 2;
    public int RoundsCompleted { get; set; }

    public DeliberationStatus Status { get; set; } = DeliberationStatus.Pending;
    public Resolution? Resolution { get; set; }
    public int ApproveCount { get; set; }
    public int RejectCount { get; set; }
    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public List<CoreVote> Votes { get; set; } = [];
}

/// <summary>One core's answer in one round. A failed call is stored too (Verdict = null, Error set).</summary>
public sealed class CoreVote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeliberationId { get; set; }
    public Deliberation? Deliberation { get; set; }

    public int Round { get; set; }
    public CoreId Core { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }

    public Verdict? Verdict { get; set; }
    public int Confidence { get; set; }
    public string? Summary { get; set; }
    public string? Reasoning { get; set; }

    /// <summary>JSON array of strings: conditions attached to the vote.</summary>
    public string ConditionsJson { get; set; } = "[]";

    /// <summary>True when the core switched verdict compared with its previous round.</summary>
    public bool ChangedVerdict { get; set; }

    public string? Error { get; set; }
    public int LatencyMs { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum CoreId
{
    Melchior = 1,
    Balthasar = 2,
    Casper = 3,
}

public enum Verdict
{
    Approve,
    Reject,
}

public enum ResolutionMode
{
    /// <summary>Two out of three votes decide.</summary>
    Majority,

    /// <summary>All three cores must approve, as for the self-destruct protocol.</summary>
    Unanimous,
}

public enum DeliberationStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

public enum Resolution
{
    Approved,
    Rejected,

    /// <summary>Not enough valid votes to reach the threshold either way.</summary>
    Inconclusive,
}
