using GZCTF.Utils;
using System;
using System.IO;
using GZCTF.Discord;
using Xunit;

namespace GZCTF.Test.UnitTests.Discord.ActivityLog;

public class ActivityConfigLoaderTests
{
    private static readonly Serilog.ILogger Logger = Serilog.Log.Logger;

    private static string WriteTempConfig(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"discord-act-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, content);
        return path;
    }

    private static T WithConfig<T>(string yaml, Func<DiscordConfig, T> assert)
    {
        var path = WriteTempConfig(yaml);
        try
        {
            var config = DiscordConfigLoader.Load(path, Logger);
            Assert.NotNull(config);
            return assert(config!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_FullActivityLog_Parses()
    {
        WithConfig("""
                   enabled: true
                   bot_token: "token"
                   activity_log:
                     enabled: true
                     channel_id: "345678901234567890"
                     challenge_types: ["DynamicContainer", "StaticContainer"]
                     timezone: "Asia/Dhaka"
                     live_feed:
                       enabled: true
                       batch_seconds: 45
                     summary:
                       interval_minutes: 30
                       post_at_game_end: true
                     suspicion:
                       fast_solve_minutes: 7
                       no_wrong_attempts: true
                       close_solve_window_minutes: 20
                   """, config =>
        {
            Assert.NotNull(config.ActivityLog);
            var a = config.ActivityLog!;
            Assert.Equal(345678901234567890UL, a.ChannelId);
            Assert.True(a.Tracks(ChallengeType.DynamicContainer));
            Assert.True(a.Tracks(ChallengeType.StaticContainer));
            Assert.Equal(45, a.BatchSeconds);
            Assert.Equal(30, a.SummaryIntervalMinutes);
            Assert.Equal(7, a.FastSolveMinutes);
            Assert.Equal(20, a.CloseSolveWindowMinutes);
            return true;
        });
    }

    [Fact]
    public void Load_InvalidActivityChannel_DisablesOnlyActivity()
    {
        WithConfig("""
                   enabled: true
                   bot_token: "token"
                   first_blood:
                     enabled: true
                     channel_id: "123456789012345678"
                   activity_log:
                     enabled: true
                     channel_id: "nope"
                   """, config =>
        {
            Assert.True(config.FirstBloodEnabled);
            Assert.Null(config.ActivityLog); // activity disabled, rest works
            return true;
        });
    }

    [Fact]
    public void Load_NonContainerChallengeTypes_DefaultToDynamicContainer()
    {
        WithConfig("""
                   enabled: true
                   bot_token: "token"
                   activity_log:
                     enabled: true
                     channel_id: "345678901234567890"
                     challenge_types: ["StaticAttachment", "DynamicAttachment"]
                   """, config =>
        {
            Assert.NotNull(config.ActivityLog);
            Assert.True(config.ActivityLog!.Tracks(ChallengeType.DynamicContainer));
            Assert.False(config.ActivityLog.Tracks(ChallengeType.StaticContainer));
            return true;
        });
    }

    [Fact]
    public void Load_InvalidTimezone_FallsBackToUtc()
    {
        WithConfig("""
                   enabled: true
                   bot_token: "token"
                   activity_log:
                     enabled: true
                     channel_id: "345678901234567890"
                     timezone: "Not/AZone"
                   """, config =>
        {
            Assert.Equal(TimeZoneInfo.Utc, config.ActivityLog!.Timezone);
            return true;
        });
    }

    [Fact]
    public void Load_OnlyActivityEnabled_IntegrationStillLoads()
    {
        WithConfig("""
                   enabled: true
                   bot_token: "token"
                   activity_log:
                     enabled: true
                     channel_id: "345678901234567890"
                   """, config =>
        {
            Assert.False(config.FirstBloodEnabled);
            Assert.False(config.CheatEnabled);
            Assert.NotNull(config.ActivityLog);
            Assert.True(config.AnyEnabled);
            return true;
        });
    }

    [Fact]
    public void Load_ActivityDisabled_LeavesOtherFeaturesWorking()
    {
        WithConfig("""
                   enabled: true
                   bot_token: "token"
                   cheat_detection:
                     enabled: true
                     channel_id: "234567890123456789"
                   activity_log:
                     enabled: false
                     channel_id: "345678901234567890"
                   """, config =>
        {
            Assert.True(config.CheatEnabled);
            Assert.Null(config.ActivityLog);
            return true;
        });
    }
}
