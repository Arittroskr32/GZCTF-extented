namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// No-op activity logger used when the activity-log feature is disabled, so hook points can call it
/// unconditionally without any null checks.
/// </summary>
internal sealed class NullActivityLogger : IActivityLogger
{
    public void ContainerStarted(int gameId, int challengeId, ChallengeType type, int participationId,
        int teamId, string teamName, Guid? userId, string? userName) { }

    public void ContainerStartFailed(int gameId, int challengeId, ChallengeType type, int participationId,
        int teamId, string teamName, Guid? userId, string? userName, string reason) { }

    public void ContainerExtended(int gameId, int challengeId, ChallengeType type, int participationId,
        int teamId, string teamName, Guid? userId, string? userName) { }

    public void ContainerDestroyed(int participationId, int challengeId, ContainerDestroyReason reason) { }

    public void FlagResult(int gameId, int challengeId, ChallengeType type, int participationId, int teamId,
        string teamName, Guid? userId, string? userName, AnswerResult result, SubmissionType bloodTier,
        string? sourceTeamName, string? flag, DateTimeOffset timeUtc) { }
}
