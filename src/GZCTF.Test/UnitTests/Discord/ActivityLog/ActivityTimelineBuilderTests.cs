using GZCTF.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using GZCTF.Discord.ActivityLog;
using GZCTF.Models.Data;
using Xunit;

namespace GZCTF.Test.UnitTests.Discord.ActivityLog;

public class ActivityTimelineBuilderTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private static ReportContext Context(int fast = 10, bool noWrong = true, int close = 15,
        DateTimeOffset? now = null) =>
        new(1, "Example CTF", 12, "Login Bypass", ChallengeType.DynamicContainer,
            now ?? Base.AddHours(2), TimeZoneInfo.Utc, fast, noWrong, close, ShowFlags: true);

    private static ActivityLogEntry Entry(int teamId, string teamName, ActivityLogType type, int minutes,
        string? user = "alice", string? detail = null) => new()
    {
        Id = _id++,
        GameId = 1,
        ChallengeId = 12,
        ParticipationId = teamId,
        TeamId = teamId,
        TeamName = teamName,
        UserName = user,
        Type = type,
        TimeUtc = Base.AddMinutes(minutes),
        Detail = detail
    };

    private static long _id = 1;

    [Fact]
    public void Build_OrdersTeamsAndComputesCounts()
    {
        var entries = new List<ActivityLogEntry>
        {
            Entry(7, "BetaPwn", ActivityLogType.ContainerStarted, 5),
            Entry(3, "AlphaSec", ActivityLogType.ContainerStarted, 1),
            Entry(3, "AlphaSec", ActivityLogType.FlagWrong, 2),
            Entry(3, "AlphaSec", ActivityLogType.FlagAccepted, 40, detail: "Normal")
        };

        var report = ActivityTimelineBuilder.Build(Context(), entries);

        Assert.Equal(2, report.Teams.Count);
        Assert.Equal(3, report.Teams[0].TeamId); // sorted by team id
        Assert.Equal(7, report.Teams[1].TeamId);
        Assert.Equal(2, report.TeamsStarted);
        Assert.Equal(1, report.Solves);

        var alpha = report.Teams[0];
        Assert.Equal(1, alpha.WrongCount);
        Assert.True(alpha.Solved);
    }

    [Fact]
    public void Uptime_PairsStartsAndEnds_AndCountsRunningContainerToReportTime()
    {
        // One closed session (30m) + one still-running at report time (report = Base + 2h).
        var entries = new List<ActivityLogEntry>
        {
            Entry(3, "AlphaSec", ActivityLogType.ContainerStarted, 0),
            Entry(3, "AlphaSec", ActivityLogType.ContainerExpired, 30),
            Entry(3, "AlphaSec", ActivityLogType.ContainerStarted, 60)
        };

        var report = ActivityTimelineBuilder.Build(Context(now: Base.AddHours(2)), entries);
        var team = report.Teams.Single();

        // 30m (closed) + 60m (from minute 60 to minute 120) = 90m.
        Assert.Equal(TimeSpan.FromMinutes(90), team.TotalUptime);
        Assert.Equal(2, team.StartCount);
    }

    [Fact]
    public void Suspicion_FastSolve_FiresWithinWindow_NotOutside()
    {
        var fast = new List<ActivityLogEntry>
        {
            Entry(3, "A", ActivityLogType.ContainerStarted, 0),
            Entry(3, "A", ActivityLogType.FlagAccepted, 3, detail: "Normal") // 3 min after start
        };
        var slow = new List<ActivityLogEntry>
        {
            Entry(4, "B", ActivityLogType.ContainerStarted, 0),
            Entry(4, "B", ActivityLogType.FlagWrong, 5),
            Entry(4, "B", ActivityLogType.FlagAccepted, 40, detail: "Normal") // 40 min after start
        };

        var fastTeam = ActivityTimelineBuilder.Build(Context(fast: 10, noWrong: false, close: 0), fast)
            .Teams.Single();
        var slowTeam = ActivityTimelineBuilder.Build(Context(fast: 10, noWrong: false, close: 0), slow)
            .Teams.Single();

        Assert.True(fastTeam.IsSuspicious);
        Assert.Contains(fastTeam.SuspicionReasons, r => r.Contains("after container start"));
        Assert.False(slowTeam.IsSuspicious);
    }

    [Fact]
    public void Suspicion_NoWrongAttempts_FiresOnlyWhenEnabledAndZeroWrong()
    {
        var clean = new List<ActivityLogEntry>
        {
            Entry(3, "A", ActivityLogType.ContainerStarted, 0),
            Entry(3, "A", ActivityLogType.FlagAccepted, 50, detail: "Normal")
        };

        var withRule = ActivityTimelineBuilder.Build(Context(fast: 0, noWrong: true, close: 0), clean)
            .Teams.Single();
        var withoutRule = ActivityTimelineBuilder.Build(Context(fast: 0, noWrong: false, close: 0), clean)
            .Teams.Single();

        Assert.Contains(withRule.SuspicionReasons, r => r.Contains("no wrong submissions"));
        Assert.False(withoutRule.IsSuspicious);
    }

    [Fact]
    public void Suspicion_CloseSolve_FlagsTeamSolvingSoonAfterAnother()
    {
        var entries = new List<ActivityLogEntry>
        {
            Entry(3, "A", ActivityLogType.ContainerStarted, 0),
            Entry(3, "A", ActivityLogType.FlagWrong, 1),
            Entry(3, "A", ActivityLogType.FlagAccepted, 30, detail: "FirstBlood"),
            Entry(7, "B", ActivityLogType.ContainerStarted, 0),
            Entry(7, "B", ActivityLogType.FlagWrong, 2),
            Entry(7, "B", ActivityLogType.FlagAccepted, 35, detail: "Normal") // 5 min after A
        };

        var report = ActivityTimelineBuilder.Build(Context(fast: 0, noWrong: false, close: 15), entries);
        var a = report.Teams.Single(t => t.TeamId == 3);
        var b = report.Teams.Single(t => t.TeamId == 7);

        Assert.False(a.IsSuspicious); // A solved first, nobody before it
        Assert.True(b.IsSuspicious);
        Assert.Contains(b.SuspicionReasons, r => r.Contains("after Team #3"));
    }

    [Fact]
    public void Suspicion_CloseSolve_DoesNotFireOutsideWindow()
    {
        var entries = new List<ActivityLogEntry>
        {
            Entry(3, "A", ActivityLogType.ContainerStarted, 0),
            Entry(3, "A", ActivityLogType.FlagWrong, 1),
            Entry(3, "A", ActivityLogType.FlagAccepted, 10, detail: "Normal"),
            Entry(7, "B", ActivityLogType.ContainerStarted, 0),
            Entry(7, "B", ActivityLogType.FlagWrong, 2),
            Entry(7, "B", ActivityLogType.FlagAccepted, 60, detail: "Normal") // 50 min after A
        };

        var report = ActivityTimelineBuilder.Build(Context(fast: 0, noWrong: false, close: 15), entries);
        Assert.False(report.Teams.Single(t => t.TeamId == 7).IsSuspicious);
    }

    [Fact]
    public void TimelineText_ContainsHeaderTeamsAndSuspicionAndSummary()
    {
        var entries = new List<ActivityLogEntry>
        {
            Entry(3, "AlphaSec", ActivityLogType.ContainerStarted, 0),
            Entry(3, "AlphaSec", ActivityLogType.FlagAccepted, 2, detail: "Normal")
        };

        var report = ActivityTimelineBuilder.Build(Context(fast: 10, noWrong: true, close: 0), entries);
        var text = report.TimelineText;

        Assert.Contains("Challenge #12 — Login Bypass (DynamicContainer) — Game: Example CTF", text);
        Assert.Contains("Team #3 \"AlphaSec\"", text);
        Assert.Contains("container started", text);
        Assert.Contains("SUSPICIOUS", text);
        Assert.Contains("Summary: total container uptime", text);
    }

    [Theory]
    [InlineData(ContainerDestroyReason.Expired, false, ActivityLogType.ContainerExpired)]
    [InlineData(ContainerDestroyReason.Admin, false, ActivityLogType.ContainerDestroyedByAdmin)]
    [InlineData(ContainerDestroyReason.LimitReached, false, ActivityLogType.ContainerDestroyedOnLimit)]
    [InlineData(ContainerDestroyReason.ChallengeRemoval, false,
        ActivityLogType.ContainerDestroyedOnChallengeRemoval)]
    [InlineData(ContainerDestroyReason.User, false, ActivityLogType.ContainerDestroyedByUser)]
    [InlineData(ContainerDestroyReason.User, true, ActivityLogType.ContainerAutoDestroyedAfterSolve)]
    public void MapDestroyType_MapsReasonAndSolveState(ContainerDestroyReason reason, bool solved,
        ActivityLogType expected) =>
        Assert.Equal(expected, ActivityTimelineBuilder.MapDestroyType(reason, solved));

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(70, "1m10s")]
    [InlineData(120, "2m")]
    [InlineData(4320, "1h12m")]
    public void FormatDuration_IsCompact(int seconds, string expected) =>
        Assert.Equal(expected, ActivityTimelineBuilder.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void SplitIntoMessages_PacksLinesUnderLimit()
    {
        var lines = Enumerable.Range(0, 50).Select(i => new string('x', 100)).ToList();
        var messages = ActivityTimelineBuilder.SplitIntoMessages(lines, 2000);

        Assert.True(messages.Count > 1);
        Assert.All(messages, m => Assert.True(m.Length <= 2000));
        // No line is lost.
        Assert.Equal(50, messages.Sum(m => m.Split('\n').Length));
    }

    [Fact]
    public void SplitIntoMessages_HardSplitsAnOverLongLine()
    {
        var messages = ActivityTimelineBuilder.SplitIntoMessages([new string('y', 4500)], 2000);
        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.True(m.Length <= 2000));
    }

    [Fact]
    public void FormatLiveLine_NeverIncludesFlagValue()
    {
        var e = Entry(3, "A", ActivityLogType.FlagWrong, 1);
        e.Flag = "flag{secret}";
        var line = ActivityTimelineBuilder.FormatLiveLine(e);

        Assert.DoesNotContain("secret", line);
        Assert.Contains("wrong flag", line);
    }
}
