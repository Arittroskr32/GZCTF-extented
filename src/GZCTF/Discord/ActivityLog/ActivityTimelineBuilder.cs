using System.Globalization;
using System.Text;

namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// Pure builder that turns a flat list of <see cref="ActivityLogEntry" /> rows into a per-challenge
/// report: per-team container uptime, submission counts, time-to-solve, the three suspicion rules, and a
/// plain-text timeline sorted by team id then time. Has no I/O so it is fully unit-testable.
/// </summary>
public static class ActivityTimelineBuilder
{
    private static readonly ActivityLogType[] DestroyTypes =
    [
        ActivityLogType.ContainerDestroyedByUser,
        ActivityLogType.ContainerAutoDestroyedAfterSolve,
        ActivityLogType.ContainerDestroyedOnLimit,
        ActivityLogType.ContainerExpired,
        ActivityLogType.ContainerDestroyedByAdmin,
        ActivityLogType.ContainerDestroyedOnChallengeRemoval
    ];

    /// <summary>
    /// Map a container destroy reason (plus whether the team had solved the challenge) to the recorded
    /// activity entry type. A user-initiated destroy of an already-solved challenge is treated as the
    /// frontend's auto-destroy after a correct flag.
    /// </summary>
    public static ActivityLogType MapDestroyType(ContainerDestroyReason reason, bool solved) => reason switch
    {
        ContainerDestroyReason.Expired => ActivityLogType.ContainerExpired,
        ContainerDestroyReason.Admin => ActivityLogType.ContainerDestroyedByAdmin,
        ContainerDestroyReason.LimitReached => ActivityLogType.ContainerDestroyedOnLimit,
        ContainerDestroyReason.ChallengeRemoval => ActivityLogType.ContainerDestroyedOnChallengeRemoval,
        ContainerDestroyReason.AutoAfterSolve => ActivityLogType.ContainerAutoDestroyedAfterSolve,
        _ => solved
            ? ActivityLogType.ContainerAutoDestroyedAfterSolve
            : ActivityLogType.ContainerDestroyedByUser
    };

    public static bool IsStart(ActivityLogType t) => t == ActivityLogType.ContainerStarted;

    public static bool IsContainerEnd(ActivityLogType t) => Array.IndexOf(DestroyTypes, t) >= 0;

    public static bool IsSolve(ActivityLogType t) =>
        t is ActivityLogType.FlagAccepted or ActivityLogType.FlagCheatDetected;

    public static ChallengeReport Build(ReportContext ctx, IReadOnlyCollection<ActivityLogEntry> entries)
    {
        // Group by team, each ordered by time.
        var byTeam = entries
            .GroupBy(e => e.TeamId)
            .Select(g => new
            {
                TeamId = g.Key,
                Entries = g.OrderBy(e => e.TimeUtc).ThenBy(e => e.Id).ToList()
            })
            .OrderBy(g => g.TeamId)
            .ToList();

        // First, per-team solve times (needed for the cross-team "close solve" rule).
        var solveTimes = new Dictionary<int, DateTimeOffset>();
        foreach (var team in byTeam)
        {
            var solve = team.Entries.FirstOrDefault(e => IsSolve(e.Type));
            if (solve is not null)
                solveTimes[team.TeamId] = solve.TimeUtc;
        }

        var teams = new List<TeamTimeline>();
        foreach (var team in byTeam)
            teams.Add(BuildTeam(ctx, team.TeamId, team.Entries, solveTimes));

        var flagged = teams.Where(t => t.IsSuspicious).ToList();
        var teamsStarted = teams.Count(t => t.StartCount > 0);
        var solves = teams.Count(t => t.Solved);

        var text = BuildTimelineText(ctx, byTeam.ToDictionary(t => t.TeamId, t => t.Entries), teams);

        return new ChallengeReport(ctx.ChallengeId, ctx.ChallengeTitle, ctx.ChallengeType, ctx.GameTitle,
            ctx.GeneratedUtc, teamsStarted, solves, teams, flagged, text);
    }

    private static TeamTimeline BuildTeam(ReportContext ctx, int teamId, List<ActivityLogEntry> entries,
        IReadOnlyDictionary<int, DateTimeOffset> solveTimes)
    {
        var teamName = entries.Select(e => e.TeamName).LastOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";

        // Uptime: pair each start with the next container-end; a start still open at report time counts
        // up to GeneratedUtc.
        var uptime = TimeSpan.Zero;
        DateTimeOffset? openStart = null;
        var startCount = 0;
        var wrongCount = 0;
        var starts = new List<DateTimeOffset>();

        foreach (var e in entries)
        {
            if (IsStart(e.Type))
            {
                startCount++;
                starts.Add(e.TimeUtc);
                // A new start while one is open: close the previous at this point (defensive).
                if (openStart is not null)
                    uptime += e.TimeUtc - openStart.Value;
                openStart = e.TimeUtc;
            }
            else if (IsContainerEnd(e.Type))
            {
                if (openStart is not null)
                {
                    uptime += e.TimeUtc - openStart.Value;
                    openStart = null;
                }
            }
            else if (e.Type == ActivityLogType.FlagWrong)
            {
                wrongCount++;
            }
        }

        if (openStart is not null && ctx.GeneratedUtc > openStart.Value)
            uptime += ctx.GeneratedUtc - openStart.Value;

        var solveEntry = entries.FirstOrDefault(e => IsSolve(e.Type));
        DateTimeOffset? solveTime = solveEntry?.TimeUtc;
        var cheated = solveEntry?.Type == ActivityLogType.FlagCheatDetected;
        var tier = ParseTier(solveEntry);

        DateTimeOffset? firstStart = starts.Count > 0 ? starts[0] : null;
        TimeSpan? fromFirst = null;
        TimeSpan? fromLatest = null;
        DateTimeOffset? latestStartBeforeSolve = null;

        if (solveTime is not null)
        {
            if (firstStart is not null)
                fromFirst = solveTime.Value - firstStart.Value;

            latestStartBeforeSolve = starts.Where(s => s <= solveTime.Value).DefaultIfEmpty()
                .Max(s => s == default ? (DateTimeOffset?)null : s);
            if (latestStartBeforeSolve is not null)
                fromLatest = solveTime.Value - latestStartBeforeSolve.Value;
        }

        var reasons = new List<string>();
        if (solveTime is not null)
        {
            // Rule 1: fast solve after that team's most recent container start.
            if (ctx.FastSolveMinutes > 0 && fromLatest is not null &&
                fromLatest.Value <= TimeSpan.FromMinutes(ctx.FastSolveMinutes))
                reasons.Add($"solved {FormatDuration(fromLatest.Value)} after container start");

            // Rule 2: solved with zero wrong submissions.
            if (ctx.NoWrongAttempts && wrongCount == 0)
                reasons.Add("solved with no wrong submissions");

            // Rule 3: solved within the window after another team's solve.
            if (ctx.CloseSolveWindowMinutes > 0)
            {
                var window = TimeSpan.FromMinutes(ctx.CloseSolveWindowMinutes);
                foreach (var (otherTeam, otherSolve) in solveTimes)
                {
                    if (otherTeam == teamId)
                        continue;
                    var gap = solveTime.Value - otherSolve;
                    if (gap > TimeSpan.Zero && gap <= window)
                    {
                        reasons.Add($"solved {FormatDuration(gap)} after Team #{otherTeam}");
                        break;
                    }
                }
            }
        }

        return new TeamTimeline(teamId, teamName, uptime, startCount, wrongCount, firstStart, solveTime,
            tier, cheated, fromFirst, fromLatest, reasons);
    }

    private static string BuildTimelineText(ReportContext ctx,
        IReadOnlyDictionary<int, List<ActivityLogEntry>> byTeam, IReadOnlyList<TeamTimeline> teams)
    {
        var sb = new StringBuilder();
        sb.Append("Challenge #").Append(ctx.ChallengeId).Append(" — ").Append(ctx.ChallengeTitle)
            .Append(" (").Append(ctx.ChallengeType).Append(") — Game: ").Append(ctx.GameTitle).Append('\n');
        sb.Append("Generated: ").Append(FormatDateTime(ctx.GeneratedUtc, ctx.Timezone)).Append(" (")
            .Append(ctx.Timezone.Id).Append(")\n");

        foreach (var team in teams)
        {
            sb.Append('\n');
            sb.Append("Team #").Append(team.TeamId).Append(" \"").Append(team.TeamName).Append("\"\n");

            foreach (var e in byTeam[team.TeamId])
                sb.Append("  ").Append(FormatTime(e.TimeUtc, ctx.Timezone)).Append("  ")
                    .Append(FormatEventLine(e, ctx.ShowFlags)).Append('\n');

            if (team.IsSuspicious)
                sb.Append("  ⚠ SUSPICIOUS: ").Append(string.Join("; ", team.SuspicionReasons))
                    .Append('\n');

            sb.Append("  Summary: total container uptime ").Append(FormatDuration(team.TotalUptime))
                .Append(", ").Append(team.StartCount).Append(" starts, ")
                .Append(team.WrongCount).Append(" wrong flags");
            if (team.Solved)
                sb.Append(", solved after ")
                    .Append(team.TimeFromFirstStartToSolve is { } d ? FormatDuration(d) : "n/a");
            else
                sb.Append(", not solved");
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string FormatEventLine(ActivityLogEntry e, bool showFlags)
    {
        var user = string.IsNullOrEmpty(e.UserName) ? "" : $" (user: {e.UserName})";
        return e.Type switch
        {
            ActivityLogType.ContainerStarted => $"▶ container started{user}",
            ActivityLogType.ContainerStartFailed =>
                $"✖ container start FAILED{user}{Detail(e)}",
            ActivityLogType.ContainerExtended => $"⏱ container extended{user}",
            ActivityLogType.ContainerDestroyedByUser => $"⏹ container destroyed by user{user}",
            ActivityLogType.ContainerAutoDestroyedAfterSolve =>
                $"⏹ container auto-destroyed after solve{user}",
            ActivityLogType.ContainerDestroyedOnLimit => "⏹ container destroyed (container limit)",
            ActivityLogType.ContainerExpired => "⏹ container expired",
            ActivityLogType.ContainerDestroyedByAdmin => "⏹ container destroyed by admin",
            ActivityLogType.ContainerDestroyedOnChallengeRemoval =>
                "⏹ container destroyed (challenge removed)",
            ActivityLogType.FlagWrong =>
                $"✗ wrong flag{user}{(showFlags && !string.IsNullOrEmpty(e.Flag) ? $" [{e.Flag}]" : "")}",
            ActivityLogType.FlagAccepted => $"✓ solved — {e.Detail ?? "Normal"}{user}",
            ActivityLogType.FlagCheatDetected =>
                $"✓ solved (CHEAT — shared from {e.Detail ?? "?"}){user}",
            _ => e.Type.ToString()
        };
    }

    private static string Detail(ActivityLogEntry e) =>
        string.IsNullOrEmpty(e.Detail) ? "" : $": {e.Detail}";

    /// <summary>
    /// A single compact live-feed line (markdown-escaped, with a Discord timestamp). Never includes a
    /// flag value.
    /// </summary>
    public static string FormatLiveLine(ActivityLogEntry e)
    {
        var who = string.IsNullOrEmpty(e.UserName) ? "" : $" — {DiscordEmbedFactory.Escape(e.UserName)}";
        var team = $"Team #{e.TeamId} {DiscordEmbedFactory.Escape(Quote(e.TeamName))}";
        var evt = e.Type switch
        {
            ActivityLogType.ContainerStarted => "▶ container started",
            ActivityLogType.ContainerStartFailed => "✖ container start failed",
            ActivityLogType.ContainerExtended => "⏱ container extended",
            ActivityLogType.ContainerDestroyedByUser => "⏹ destroyed by user",
            ActivityLogType.ContainerAutoDestroyedAfterSolve => "⏹ auto-destroyed after solve",
            ActivityLogType.ContainerDestroyedOnLimit => "⏹ destroyed (limit)",
            ActivityLogType.ContainerExpired => "⏹ expired",
            ActivityLogType.ContainerDestroyedByAdmin => "⏹ destroyed by admin",
            ActivityLogType.ContainerDestroyedOnChallengeRemoval => "⏹ destroyed (challenge removed)",
            ActivityLogType.FlagWrong => "✗ wrong flag",
            ActivityLogType.FlagAccepted => $"✓ solved — {DiscordEmbedFactory.Escape(e.Detail ?? "Normal")}",
            ActivityLogType.FlagCheatDetected =>
                $"✓ solved (CHEAT — from {DiscordEmbedFactory.Escape(e.Detail ?? "?")})",
            _ => e.Type.ToString()
        };
        return $"`{FormatTime(e.TimeUtc, TimeZoneInfo.Utc)}` {team}{who} — {evt} <t:{e.TimeUtc.ToUnixTimeSeconds()}:T>";
    }

    private static string Quote(string name) => $"\"{name}\"";

    private static SubmissionType ParseTier(ActivityLogEntry? solve)
    {
        if (solve is null)
            return SubmissionType.Unaccepted;
        if (solve.Type == ActivityLogType.FlagCheatDetected)
            return SubmissionType.Normal;
        return Enum.TryParse<SubmissionType>(solve.Detail, out var t) ? t : SubmissionType.Normal;
    }

    /// <summary>
    /// Pack lines into as few messages as possible, each at most <paramref name="maxLen" /> characters
    /// (Discord's message limit is 2000). A single line longer than the limit is hard-split.
    /// </summary>
    public static List<string> SplitIntoMessages(IEnumerable<string> lines, int maxLen = 2000)
    {
        var messages = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                messages.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (var raw in lines)
        {
            var line = raw ?? string.Empty;

            // Hard-split a single over-long line.
            while (line.Length > maxLen)
            {
                Flush();
                messages.Add(line[..maxLen]);
                line = line[maxLen..];
            }

            var extra = current.Length == 0 ? line.Length : line.Length + 1;
            if (current.Length + extra > maxLen)
                Flush();

            if (current.Length > 0)
                current.Append('\n');
            current.Append(line);
        }

        Flush();
        return messages;
    }

    public static string FormatDuration(TimeSpan d)
    {
        if (d < TimeSpan.Zero)
            d = TimeSpan.Zero;

        if (d.TotalHours >= 1)
            return $"{(int)d.TotalHours}h{d.Minutes:D2}m";
        if (d.TotalMinutes >= 1)
            return d.Seconds > 0 ? $"{d.Minutes}m{d.Seconds:D2}s" : $"{d.Minutes}m";
        return $"{d.Seconds}s";
    }

    private static string FormatTime(DateTimeOffset utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(utc, tz).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatDateTime(DateTimeOffset utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(utc, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
