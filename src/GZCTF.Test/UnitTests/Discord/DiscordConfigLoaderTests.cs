using System;
using System.IO;
using GZCTF.Discord;
using Xunit;

namespace GZCTF.Test.UnitTests.Discord;

public class DiscordConfigLoaderTests
{
    private static readonly Serilog.ILogger Logger = Serilog.Log.Logger;

    private static string WriteTempConfig(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"discord-test-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.yml");
        Assert.Null(DiscordConfigLoader.Load(path, Logger));
    }

    [Fact]
    public void Load_Disabled_ReturnsNull()
    {
        var path = WriteTempConfig("enabled: false\nbot_token: \"x\"\n");
        try
        {
            Assert.Null(DiscordConfigLoader.Load(path, Logger));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingToken_ReturnsNull()
    {
        var path = WriteTempConfig("""
                                   enabled: true
                                   first_blood:
                                     enabled: true
                                     channel_id: "123456789012345678"
                                   """);
        try
        {
            Assert.Null(DiscordConfigLoader.Load(path, Logger));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_InvalidFirstBloodChannel_DisablesOnlyThatPart()
    {
        var path = WriteTempConfig("""
                                   enabled: true
                                   bot_token: "token"
                                   first_blood:
                                     enabled: true
                                     channel_id: "not-a-snowflake"
                                   cheat_detection:
                                     enabled: true
                                     channel_id: "234567890123456789"
                                   """);
        try
        {
            var config = DiscordConfigLoader.Load(path, Logger);
            Assert.NotNull(config);
            Assert.False(config.FirstBloodEnabled);
            Assert.True(config.CheatEnabled);
            Assert.Equal(234567890123456789UL, config.CheatChannelId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_SameChannelForBoth_Works()
    {
        var path = WriteTempConfig("""
                                   enabled: true
                                   bot_token: "token"
                                   first_blood:
                                     enabled: true
                                     channel_id: "123456789012345678"
                                   cheat_detection:
                                     enabled: true
                                     channel_id: "123456789012345678"
                                   """);
        try
        {
            var config = DiscordConfigLoader.Load(path, Logger);
            Assert.NotNull(config);
            Assert.True(config.FirstBloodEnabled);
            Assert.True(config.CheatEnabled);
            Assert.Equal(config.FirstBloodChannelId, config.CheatChannelId);
            // The startup access check must de-duplicate identical channel ids.
            Assert.Single(config.ActiveChannelIds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_FullValidConfig_ParsesAllOptions()
    {
        var path = WriteTempConfig("""
                                   enabled: true
                                   bot_token: "token"
                                   first_blood:
                                     enabled: true
                                     channel_id: "123456789012345678"
                                     include_second_third_blood: true
                                   cheat_detection:
                                     enabled: true
                                     channel_id: "234567890123456789"
                                     show_submitted_flag: true
                                   games: [1, 2, 3]
                                   """);
        try
        {
            var config = DiscordConfigLoader.Load(path, Logger);
            Assert.NotNull(config);
            Assert.True(config.IncludeSecondThirdBlood);
            Assert.True(config.ShowSubmittedFlag);
            Assert.Equal(123456789012345678UL, config.FirstBloodChannelId);
            Assert.Equal(234567890123456789UL, config.CheatChannelId);
            Assert.True(config.ShouldNotifyGame(2));
            Assert.False(config.ShouldNotifyGame(99));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_EmptyGamesList_NotifiesAllGames()
    {
        var path = WriteTempConfig("""
                                   enabled: true
                                   bot_token: "token"
                                   cheat_detection:
                                     enabled: true
                                     channel_id: "234567890123456789"
                                   games: []
                                   """);
        try
        {
            var config = DiscordConfigLoader.Load(path, Logger);
            Assert.NotNull(config);
            Assert.True(config.ShouldNotifyGame(1));
            Assert.True(config.ShouldNotifyGame(12345));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("123456789012345678", true)]
    [InlineData("12345", false)] // too short
    [InlineData("abcdefghijklmnopqr", false)] // not numeric
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParseSnowflake_ValidatesFormat(string? value, bool expected)
    {
        Assert.Equal(expected, DiscordConfigLoader.TryParseSnowflake(value, out _));
    }
}
