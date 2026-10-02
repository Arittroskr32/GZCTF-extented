using GZCTF.Models.Data;
using GZCTF.Utils;
using System;
using System.Collections.Generic;
using System.Threading.Channels;
using GZCTF.Discord;
using GZCTF.Discord.ActivityLog;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GZCTF.Test.UnitTests.Discord.ActivityLog;

public class ActivityLoggerTests
{
    private static DiscordConfig Config(params ChallengeType[] tracked) => new()
    {
        BotToken = "token",
        ActivityLog = new ActivityLogConfig
        {
            ChannelId = 345678901234567890UL,
            ChallengeTypes = new HashSet<ChallengeType>(tracked),
            Timezone = TimeZoneInfo.Utc,
            LiveFeedEnabled = true,
            BatchSeconds = 30,
            SummaryIntervalMinutes = 60,
            PostAtGameEnd = true,
            FastSolveMinutes = 10,
            NoWrongAttempts = true,
            CloseSolveWindowMinutes = 15
        }
    };

    [Fact]
    public void TrackedChallengeType_IsEnqueued_UntrackedIsDropped()
    {
        var channel = Channel.CreateBounded<RawActivityEvent>(10);
        var logger = new ActivityLogger(channel.Writer, Config(ChallengeType.DynamicContainer),
            NullLogger<ActivityLogger>.Instance);

        logger.ContainerStarted(1, 12, ChallengeType.DynamicContainer, 3, 3, "A", Guid.NewGuid(), "alice");
        logger.ContainerStarted(1, 13, ChallengeType.StaticAttachment, 3, 3, "A", Guid.NewGuid(), "alice");

        Assert.True(channel.Reader.TryRead(out var evt));
        Assert.Equal(12, evt!.ChallengeId);
        Assert.Equal(ActivityLogType.ContainerStarted, evt.Type);
        Assert.False(channel.Reader.TryRead(out _)); // untracked type dropped
    }

    [Fact]
    public void FlagResult_MapsResultToType()
    {
        var channel = Channel.CreateBounded<RawActivityEvent>(10);
        var logger = new ActivityLogger(channel.Writer, Config(ChallengeType.DynamicContainer),
            NullLogger<ActivityLogger>.Instance);

        logger.FlagResult(1, 12, ChallengeType.DynamicContainer, 3, 3, "A", Guid.NewGuid(), "alice",
            AnswerResult.CheatDetected, SubmissionType.Normal, "SourceTeam", "flag{x}", DateTimeOffset.UtcNow);

        Assert.True(channel.Reader.TryRead(out var evt));
        Assert.Equal(ActivityLogType.FlagCheatDetected, evt!.Type);
        Assert.Equal("SourceTeam", evt.Detail);
        Assert.Equal("flag{x}", evt.Flag);
    }

    [Fact]
    public void ContainerDestroyed_AlwaysEnqueuesForResolution()
    {
        var channel = Channel.CreateBounded<RawActivityEvent>(10);
        var logger = new ActivityLogger(channel.Writer, Config(ChallengeType.DynamicContainer),
            NullLogger<ActivityLogger>.Instance);

        logger.ContainerDestroyed(5, 12, ContainerDestroyReason.Expired);

        Assert.True(channel.Reader.TryRead(out var evt));
        Assert.True(evt!.NeedsResolution);
        Assert.Equal(ContainerDestroyReason.Expired, evt.DestroyReason);
    }

    [Fact]
    public void QueueFull_DoesNotThrow()
    {
        var channel = Channel.CreateBounded<RawActivityEvent>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
        var logger = new ActivityLogger(channel.Writer, Config(ChallengeType.DynamicContainer),
            NullLogger<ActivityLogger>.Instance);

        // Fill then overflow — must never throw into the caller.
        var ex = Record.Exception(() =>
        {
            for (var i = 0; i < 50; i++)
                logger.ContainerStarted(1, 12, ChallengeType.DynamicContainer, 3, 3, "A", null, null);
        });

        Assert.Null(ex);
    }
}
