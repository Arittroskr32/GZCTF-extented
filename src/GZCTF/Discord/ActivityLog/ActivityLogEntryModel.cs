namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// Admin-facing view of an <see cref="ActivityLogEntry" /> for the JSON export endpoint.
/// </summary>
public sealed class ActivityLogEntryModel
{
    public long Id { get; set; }
    public int GameId { get; set; }
    public int ChallengeId { get; set; }
    public int ParticipationId { get; set; }
    public int TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public ActivityLogType Type { get; set; }
    public DateTimeOffset TimeUtc { get; set; }
    public string? Detail { get; set; }

    /// <summary>
    /// Submitted flag, included only when <c>cheat_detection.show_submitted_flag</c> is enabled.
    /// </summary>
    public string? Flag { get; set; }

    internal static ActivityLogEntryModel FromEntry(ActivityLogEntry e, bool showFlag) => new()
    {
        Id = e.Id,
        GameId = e.GameId,
        ChallengeId = e.ChallengeId,
        ParticipationId = e.ParticipationId,
        TeamId = e.TeamId,
        TeamName = e.TeamName,
        UserId = e.UserId,
        UserName = e.UserName,
        Type = e.Type,
        TimeUtc = e.TimeUtc,
        Detail = e.Detail,
        Flag = showFlag ? e.Flag : null
    };
}
