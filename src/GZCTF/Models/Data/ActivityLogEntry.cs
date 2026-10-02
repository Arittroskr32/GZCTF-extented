using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// The kind of activity recorded in <see cref="ActivityLogEntry" />. This is a self-contained enum for
/// the activity-log module only; it is intentionally separate from <c>EventType</c> (which the frontend
/// monitor renders) so adding values here never affects existing game events or the UI.
/// </summary>
public enum ActivityLogType : byte
{
    ContainerStarted = 0,
    ContainerStartFailed = 1,
    ContainerExtended = 2,
    ContainerDestroyedByUser = 3,
    ContainerAutoDestroyedAfterSolve = 4,
    ContainerDestroyedOnLimit = 5,
    ContainerExpired = 6,
    ContainerDestroyedByAdmin = 7,
    ContainerDestroyedOnChallengeRemoval = 8,
    FlagWrong = 20,
    FlagAccepted = 21,
    FlagCheatDetected = 22
}

/// <summary>
/// Append-only record of a single container-lifecycle or flag-submission event for a team on a
/// challenge. Written off the hot path by the activity-log module; it is the durable source of truth for
/// the per-challenge timeline reports and never affects scoring or existing game events.
/// </summary>
/// <remarks>
/// Team/user names are denormalized so a report reflects the state at event time and needs no joins.
/// </remarks>
[Index(nameof(GameId), nameof(ChallengeId), nameof(ParticipationId), nameof(TimeUtc))]
[Index(nameof(GameId), nameof(ChallengeId))]
public class ActivityLogEntry
{
    [Key]
    public long Id { get; set; }

    public int GameId { get; set; }

    public int ChallengeId { get; set; }

    public int ParticipationId { get; set; }

    public int TeamId { get; set; }

    /// <summary>
    /// Team name captured at event time (denormalized; survives later renames).
    /// </summary>
    [MaxLength(Limits.MaxTeamNameLength)]
    public string TeamName { get; set; } = string.Empty;

    /// <summary>
    /// Acting user id, when the event was triggered by a specific user (container ops, submissions).
    /// Null for system events (expiry, limit eviction, challenge removal).
    /// </summary>
    public Guid? UserId { get; set; }

    [MaxLength(64)]
    public string? UserName { get; set; }

    public ActivityLogType Type { get; set; }

    public DateTimeOffset TimeUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional structured detail: blood tier for <see cref="ActivityLogType.FlagAccepted" />, source
    /// team name for <see cref="ActivityLogType.FlagCheatDetected" />, or a short failure reason for
    /// <see cref="ActivityLogType.ContainerStartFailed" />.
    /// </summary>
    [MaxLength(256)]
    public string? Detail { get; set; }

    /// <summary>
    /// Submitted flag value for a flag submission (wrong/accepted/cheat). Stored so the attached report
    /// can show wrong flags when <c>cheat_detection.show_submitted_flag</c> is enabled. Never sent to the
    /// live feed and never to the public channel.
    /// </summary>
    [MaxLength(Limits.MaxFlagLength)]
    public string? Flag { get; set; }
}
