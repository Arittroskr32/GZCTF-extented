using System.Threading.Channels;
using GZCTF.Models.Internal;

namespace GZCTF.Discord;

/// <summary>
/// Enqueues notifications onto a bounded in-process channel drained by
/// <see cref="DiscordNotificationService" />. Enqueuing is non-blocking; if the queue is full the
/// message is dropped with a warning so flag checking is never slowed down or broken.
/// </summary>
internal sealed class DiscordNotifier(
    ChannelWriter<DiscordNotification> writer,
    DiscordConfig config,
    ILogger<DiscordNotifier> logger) : IDiscordNotifier
{
    public void NotifyFirstBlood(int gameId, int challengeId, int teamId, SubmissionType bloodType,
        DateTimeOffset solveTimeUtc)
    {
        if (!config.FirstBloodEnabled || !config.ShouldNotifyGame(gameId))
            return;

        switch (bloodType)
        {
            case SubmissionType.FirstBlood:
                break;
            case SubmissionType.SecondBlood or SubmissionType.ThirdBlood when config.IncludeSecondThirdBlood:
                break;
            default:
                // Not a blood tier we announce (includes Normal/Unaccepted, i.e. cheated solves).
                return;
        }

        Enqueue(new FirstBloodNotification(gameId, challengeId, teamId, bloodType, solveTimeUtc));
    }

    public void NotifyCheat(int gameId, int challengeId, int submissionId, CheatCheckInfo cheat,
        DateTimeOffset submitTimeUtc)
    {
        if (!config.CheatEnabled || !config.ShouldNotifyGame(gameId))
            return;

        Enqueue(new CheatNotification(
            gameId,
            challengeId,
            submissionId,
            cheat.SubmitTeamName ?? string.Empty,
            cheat.SourceTeamName ?? string.Empty,
            cheat.CheatUserName ?? string.Empty,
            cheat.Flag ?? string.Empty,
            submitTimeUtc));
    }

    private void Enqueue(DiscordNotification notification)
    {
        if (!writer.TryWrite(notification))
            logger.LogWarning("[Discord] Notification queue is full; dropping a {Type} for game {GameId}",
                notification.GetType().Name, notification.GameId);
    }
}
