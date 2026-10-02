namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// Fire-and-forget hook used by container and flag-checking code to record activity. Every method is
/// non-blocking (it only enqueues) and must never throw into the caller, so recording activity can never
/// slow down or break container start/stop or flag checking. Resolution, filtering by tracked challenge
/// type, persistence and Discord delivery all happen later off the hot path.
/// </summary>
public interface IActivityLogger
{
    void ContainerStarted(int gameId, int challengeId, ChallengeType type, int participationId, int teamId,
        string teamName, Guid? userId, string? userName);

    void ContainerStartFailed(int gameId, int challengeId, ChallengeType type, int participationId, int teamId,
        string teamName, Guid? userId, string? userName, string reason);

    void ContainerExtended(int gameId, int challengeId, ChallengeType type, int participationId, int teamId,
        string teamName, Guid? userId, string? userName);

    /// <summary>
    /// Record a container destruction, identified by its game instance key
    /// (<paramref name="participationId" />, <paramref name="challengeId" />) captured before the
    /// container row is removed. Team/challenge details are resolved off the hot path;
    /// <paramref name="reason" /> decides the recorded entry type.
    /// </summary>
    void ContainerDestroyed(int participationId, int challengeId, ContainerDestroyReason reason);

    /// <summary>
    /// Record the final result of a flag submission (wrong / accepted / cheat), with the blood tier and,
    /// for a cheat, the source team name.
    /// </summary>
    void FlagResult(int gameId, int challengeId, ChallengeType type, int participationId, int teamId,
        string teamName, Guid? userId, string? userName, AnswerResult result, SubmissionType bloodTier,
        string? sourceTeamName, string? flag, DateTimeOffset timeUtc);
}

/// <summary>
/// Why a container was destroyed, so the activity log can record the correct entry type.
/// </summary>
public enum ContainerDestroyReason
{
    User,
    AutoAfterSolve,
    LimitReached,
    Expired,
    Admin,
    ChallengeRemoval
}
