using GZCTF.Models.Internal;

namespace GZCTF.Discord;

/// <summary>
/// Fire-and-forget entry point used by the flag checker to enqueue Discord notifications.
/// Implementations must be non-blocking and must never throw into the caller.
/// </summary>
public interface IDiscordNotifier
{
    /// <summary>
    /// Enqueue a blood announcement. <paramref name="bloodType" /> is the final tier from the flag
    /// checker; non-blood tiers are ignored. Cheated solves are never blood, so they never reach here.
    /// </summary>
    void NotifyFirstBlood(int gameId, int challengeId, int teamId, SubmissionType bloodType,
        DateTimeOffset solveTimeUtc);

    /// <summary>
    /// Enqueue a cheat (shared flag) alert for a single submission.
    /// </summary>
    void NotifyCheat(int gameId, int challengeId, int submissionId, CheatCheckInfo cheat,
        DateTimeOffset submitTimeUtc);
}
