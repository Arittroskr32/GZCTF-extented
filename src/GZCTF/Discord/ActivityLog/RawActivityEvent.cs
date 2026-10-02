namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// An activity event as enqueued by a hook, before it is resolved, filtered and persisted by the
/// background writer. Most events arrive fully formed; container destructions arrive as a marker
/// (<see cref="NeedsResolution" />) because the destroy choke point only has the container, so the
/// writer resolves the game/challenge/team from the container's game instance off the hot path.
/// </summary>
internal sealed record RawActivityEvent
{
    public bool NeedsResolution { get; init; }

    // Destroy-resolution field (the game instance key is in ParticipationId + ChallengeId below).
    public ContainerDestroyReason DestroyReason { get; init; }

    // Fully-formed fields (also filled in after resolution).
    public int GameId { get; init; }
    public int ChallengeId { get; init; }
    public ChallengeType ChallengeType { get; init; }
    public int ParticipationId { get; init; }
    public int TeamId { get; init; }
    public string TeamName { get; init; } = string.Empty;
    public Guid? UserId { get; init; }
    public string? UserName { get; init; }
    public ActivityLogType Type { get; init; }
    public DateTimeOffset TimeUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? Detail { get; init; }
    public string? Flag { get; init; }
}
