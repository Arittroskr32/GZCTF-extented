using System.Threading.Channels;

namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// Real <see cref="IActivityLogger" />. Enqueues raw events onto a bounded in-process channel drained by
/// <see cref="ActivityLogService" />; enqueuing is lock-free and never throws, so recording activity
/// cannot slow down or break the originating container/flag operation. Events for untracked challenge
/// types are dropped up front (where the type is known) to avoid queue pressure.
/// </summary>
internal sealed class ActivityLogger(
    ChannelWriter<RawActivityEvent> writer,
    DiscordConfig config,
    ILogger<ActivityLogger> logger) : IActivityLogger
{
    private ActivityLogConfig Config => config.ActivityLog!;

    public void ContainerStarted(int gameId, int challengeId, ChallengeType type, int participationId,
        int teamId, string teamName, Guid? userId, string? userName) =>
        EnqueueTyped(gameId, challengeId, type, participationId, teamId, teamName, userId, userName,
            ActivityLogType.ContainerStarted);

    public void ContainerStartFailed(int gameId, int challengeId, ChallengeType type, int participationId,
        int teamId, string teamName, Guid? userId, string? userName, string reason) =>
        EnqueueTyped(gameId, challengeId, type, participationId, teamId, teamName, userId, userName,
            ActivityLogType.ContainerStartFailed, reason);

    public void ContainerExtended(int gameId, int challengeId, ChallengeType type, int participationId,
        int teamId, string teamName, Guid? userId, string? userName) =>
        EnqueueTyped(gameId, challengeId, type, participationId, teamId, teamName, userId, userName,
            ActivityLogType.ContainerExtended);

    public void ContainerDestroyed(int participationId, int challengeId, ContainerDestroyReason reason)
    {
        // The challenge type is unknown here; the writer resolves and filters it off the hot path from the
        // game instance key (which still exists after the container row is removed).
        Enqueue(new RawActivityEvent
        {
            NeedsResolution = true,
            ParticipationId = participationId,
            ChallengeId = challengeId,
            DestroyReason = reason,
            TimeUtc = DateTimeOffset.UtcNow
        });
    }

    public void FlagResult(int gameId, int challengeId, ChallengeType type, int participationId, int teamId,
        string teamName, Guid? userId, string? userName, AnswerResult result, SubmissionType bloodTier,
        string? sourceTeamName, string? flag, DateTimeOffset timeUtc)
    {
        if (!Config.Tracks(type))
            return;

        var (logType, detail) = result switch
        {
            AnswerResult.Accepted => (ActivityLogType.FlagAccepted, bloodTier.ToString()),
            AnswerResult.CheatDetected => (ActivityLogType.FlagCheatDetected, sourceTeamName),
            _ => (ActivityLogType.FlagWrong, (string?)null)
        };

        Enqueue(new RawActivityEvent
        {
            GameId = gameId,
            ChallengeId = challengeId,
            ChallengeType = type,
            ParticipationId = participationId,
            TeamId = teamId,
            TeamName = teamName,
            UserId = userId,
            UserName = userName,
            Type = logType,
            Detail = detail,
            Flag = flag,
            TimeUtc = timeUtc
        });
    }

    private void EnqueueTyped(int gameId, int challengeId, ChallengeType type, int participationId, int teamId,
        string teamName, Guid? userId, string? userName, ActivityLogType logType, string? detail = null)
    {
        if (!Config.Tracks(type))
            return;

        Enqueue(new RawActivityEvent
        {
            GameId = gameId,
            ChallengeId = challengeId,
            ChallengeType = type,
            ParticipationId = participationId,
            TeamId = teamId,
            TeamName = teamName,
            UserId = userId,
            UserName = userName,
            Type = logType,
            Detail = detail,
            TimeUtc = DateTimeOffset.UtcNow
        });
    }

    private void Enqueue(RawActivityEvent evt)
    {
        try
        {
            if (!writer.TryWrite(evt))
                logger.LogWarning("[ActivityLog] Queue is full; dropping a {Type} for game {GameId}",
                    evt.NeedsResolution ? "ContainerDestroyed" : evt.Type.ToString(), evt.GameId);
        }
        catch (Exception ex)
        {
            // Never let logging break the caller.
            logger.LogWarning(ex, "[ActivityLog] Failed to enqueue an activity event");
        }
    }
}
