using System.Text;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// A request to generate and post a challenge activity report, drained by
/// <see cref="ActivitySummaryService" />. <see cref="ChallengeId" /> null means all challenges of the game
/// that have activity.
/// </summary>
internal sealed record ReportRequest(int GameId, int? ChallengeId);

/// <summary>
/// Builds a challenge activity report from stored entries and posts it (summary embed + full timeline as a
/// <c>.txt</c>) into the challenge's thread in the private activity channel. Also exposes the raw report for
/// the admin JSON/CSV endpoints. All Discord sends go through the shared rate-limited client; if Discord is
/// down the data is already stored and can be regenerated later.
/// </summary>
internal sealed class ActivityReportService(
    AppDbContext context,
    IActivityLogRepository repository,
    DiscordApiClient apiClient,
    ActivityThreadManager threadManager,
    DiscordConfig config,
    ILogger<ActivityReportService> logger)
{
    private ActivityLogConfig Config => config.ActivityLog!;

    /// <summary>
    /// Build the report for one challenge from stored entries, or null if the game/challenge is gone or
    /// the challenge type is not tracked.
    /// </summary>
    internal async Task<ChallengeReport?> BuildReportAsync(int gameId, int challengeId,
        CancellationToken token = default)
    {
        var game = await context.Games.AsNoTracking()
            .Where(g => g.Id == gameId).Select(g => g.Title).FirstOrDefaultAsync(token);
        if (game is null)
            return null;

        var challenge = await context.GameChallenges.AsNoTracking()
            .Where(c => c.Id == challengeId)
            .Select(c => new { c.Title, c.Type })
            .FirstOrDefaultAsync(token);
        if (challenge is null || !Config.Tracks(challenge.Type))
            return null;

        var entries = await repository.GetTimelineAsync(gameId, challengeId, token);

        var ctx = new ReportContext(gameId, game, challengeId, challenge.Title, challenge.Type,
            DateTimeOffset.UtcNow, Config.Timezone, Config.FastSolveMinutes, Config.NoWrongAttempts,
            Config.CloseSolveWindowMinutes, config.ShowSubmittedFlag);

        return ActivityTimelineBuilder.Build(ctx, entries);
    }

    /// <summary>
    /// Build and post reports for a game. <paramref name="challengeId" /> null posts for every challenge
    /// with activity.
    /// </summary>
    internal async Task PostAsync(int gameId, int? challengeId, CancellationToken token = default)
    {
        List<int> challengeIds = challengeId is not null
            ? [challengeId.Value]
            : await repository.GetChallengeIdsWithActivityAsync(gameId, token);

        foreach (var cid in challengeIds)
        {
            try
            {
                var report = await BuildReportAsync(gameId, cid, token);
                if (report is null || report.IsEmpty)
                    continue;

                await PostReportAsync(report, token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[ActivityLog] Failed to post report for game {GameId} challenge {Cid}",
                    gameId, cid);
            }
        }
    }

    private async Task PostReportAsync(ChallengeReport report, CancellationToken token)
    {
        var threadId = await threadManager.GetOrCreateThreadAsync(report.ChallengeId, report.ChallengeTitle,
            token);
        if (threadId is null)
        {
            logger.LogWarning("[ActivityLog] No thread for challenge {Id}; report stored but not posted",
                report.ChallengeId);
            return;
        }

        var message = new DiscordMessage { Embeds = [BuildSummaryEmbed(report)] };
        var fileName = $"challenge-{report.ChallengeId}-timeline.txt";
        var bytes = Encoding.UTF8.GetBytes(report.TimelineText);

        await apiClient.SendMessageWithFileAsync(threadId.Value, message, fileName, bytes, token);
    }

    private DiscordEmbed BuildSummaryEmbed(ChallengeReport report)
    {
        var flaggedValue = report.FlaggedTeams.Count == 0
            ? "none"
            : string.Join("\n", report.FlaggedTeams.Select(t =>
                $"• Team #{t.TeamId} {DiscordEmbedFactory.Escape(t.TeamName)} — " +
                DiscordEmbedFactory.Escape(string.Join("; ", t.SuspicionReasons))));

        return new DiscordEmbed
        {
            Title = DiscordEmbedFactory.Truncate(
                $"\U0001F4CB Activity Report — #{report.ChallengeId} {report.ChallengeTitle}", 256),
            Color = report.FlaggedTeams.Count > 0 ? 0xE67E22 : 0x2ECC71,
            Timestamp = report.GeneratedUtc.ToUniversalTime().ToString("o"),
            Fields =
            [
                new DiscordEmbedField
                {
                    Name = "Teams started", Value = report.TeamsStarted.ToString(), Inline = true
                },
                new DiscordEmbedField { Name = "Solves", Value = report.Solves.ToString(), Inline = true },
                new DiscordEmbedField
                {
                    Name = "Flagged teams", Value = report.FlaggedTeams.Count.ToString(), Inline = true
                },
                new DiscordEmbedField
                {
                    Name = "Suspicious",
                    Value = DiscordEmbedFactory.Truncate(flaggedValue, 1024),
                    Inline = false
                }
            ],
            Footer = new DiscordEmbedFooter
            {
                Text = DiscordEmbedFactory.Truncate(
                    $"{DiscordEmbedFactory.Escape(report.GameTitle)} · {report.ChallengeType}", 1024)
            }
        };
    }

    /// <summary>
    /// The stored timeline for a game/challenge as CSV, for the admin export endpoint.
    /// </summary>
    internal async Task<string> BuildCsvAsync(int gameId, int? challengeId, CancellationToken token = default)
    {
        var entries = await repository.GetTimelineAsync(gameId, challengeId, token);
        var showFlags = config.ShowSubmittedFlag;

        var sb = new StringBuilder();
        sb.Append("Id,GameId,ChallengeId,ParticipationId,TeamId,TeamName,UserName,Type,TimeUtc,Detail,Flag\n");
        foreach (var e in entries)
        {
            sb.Append(e.Id).Append(',')
                .Append(e.GameId).Append(',')
                .Append(e.ChallengeId).Append(',')
                .Append(e.ParticipationId).Append(',')
                .Append(e.TeamId).Append(',')
                .Append(Csv(e.TeamName)).Append(',')
                .Append(Csv(e.UserName)).Append(',')
                .Append(e.Type).Append(',')
                .Append(e.TimeUtc.ToUniversalTime().ToString("o")).Append(',')
                .Append(Csv(e.Detail)).Append(',')
                .Append(Csv(showFlags ? e.Flag : null)).Append('\n');
        }

        return sb.ToString();
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
