using GZCTF.Models.Internal;

namespace GZCTF.Discord;

/// <summary>
/// No-op notifier registered when the Discord integration is disabled, so hook points can depend on
/// <see cref="IDiscordNotifier" /> unconditionally.
/// </summary>
public sealed class NullDiscordNotifier : IDiscordNotifier
{
    public void NotifyFirstBlood(int gameId, int challengeId, int teamId, SubmissionType bloodType,
        DateTimeOffset solveTimeUtc)
    {
    }

    public void NotifyCheat(int gameId, int challengeId, int submissionId, CheatCheckInfo cheat,
        DateTimeOffset submitTimeUtc)
    {
    }
}
