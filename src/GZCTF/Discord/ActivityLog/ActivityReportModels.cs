namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// Inputs needed to build a challenge report, independent of any I/O so the builder stays pure/testable.
/// </summary>
public sealed record ReportContext(
    int GameId,
    string GameTitle,
    int ChallengeId,
    string ChallengeTitle,
    ChallengeType ChallengeType,
    DateTimeOffset GeneratedUtc,
    TimeZoneInfo Timezone,
    int FastSolveMinutes,
    bool NoWrongAttempts,
    int CloseSolveWindowMinutes,
    bool ShowFlags);

/// <summary>
/// Per-team computed timeline and statistics for one challenge.
/// </summary>
public sealed record TeamTimeline(
    int TeamId,
    string TeamName,
    TimeSpan TotalUptime,
    int StartCount,
    int WrongCount,
    DateTimeOffset? FirstStartUtc,
    DateTimeOffset? SolveTimeUtc,
    SubmissionType SolveTier,
    bool Cheated,
    TimeSpan? TimeFromFirstStartToSolve,
    TimeSpan? TimeFromLatestStartToSolve,
    IReadOnlyList<string> SuspicionReasons)
{
    public bool Solved => SolveTimeUtc is not null;
    public bool IsSuspicious => SuspicionReasons.Count > 0;
}

/// <summary>
/// The full result of building a challenge report: the stats, the flagged teams, and the plain-text
/// timeline to attach.
/// </summary>
public sealed record ChallengeReport(
    int ChallengeId,
    string ChallengeTitle,
    ChallengeType ChallengeType,
    string GameTitle,
    DateTimeOffset GeneratedUtc,
    int TeamsStarted,
    int Solves,
    IReadOnlyList<TeamTimeline> Teams,
    IReadOnlyList<TeamTimeline> FlaggedTeams,
    string TimelineText)
{
    public bool IsEmpty => Teams.Count == 0;
}
