using System;
using System.Linq;
using GZCTF.Discord;
using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Discord;

public class DiscordEmbedFactoryTests
{
    [Fact]
    public void Escape_EscapesMarkdownSpecials()
    {
        var result = DiscordEmbedFactory.Escape("a*b_c`d~e|f");
        Assert.Equal("a\\*b\\_c\\`d\\~e\\|f", result);
    }

    [Fact]
    public void Escape_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DiscordEmbedFactory.Escape(null));
        Assert.Equal(string.Empty, DiscordEmbedFactory.Escape(""));
    }

    [Fact]
    public void Truncate_LongText_IsTruncatedWithEllipsis()
    {
        var text = new string('x', 100);
        var result = DiscordEmbedFactory.Truncate(text, 10);
        Assert.Equal(10, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void Truncate_ShortText_IsUnchanged()
    {
        Assert.Equal("short", DiscordEmbedFactory.Truncate("short", 100));
    }

    [Fact]
    public void FirstBlood_HasDistinctTitlesAndColorsPerTier()
    {
        var now = DateTimeOffset.UtcNow;
        var first = DiscordEmbedFactory.BuildFirstBlood("Game", "Chal", "Pwn", "Team", SubmissionType.FirstBlood, now);
        var second = DiscordEmbedFactory.BuildFirstBlood("Game", "Chal", "Pwn", "Team", SubmissionType.SecondBlood, now);
        var third = DiscordEmbedFactory.BuildFirstBlood("Game", "Chal", "Pwn", "Team", SubmissionType.ThirdBlood, now);

        Assert.NotEqual(first.Color, second.Color);
        Assert.NotEqual(second.Color, third.Color);
        Assert.Contains("First Blood", first.Title);
        Assert.Contains("Second Blood", second.Title);
        Assert.Contains("Third Blood", third.Title);
    }

    [Fact]
    public void FirstBlood_EscapesTeamName()
    {
        var embed = DiscordEmbedFactory.BuildFirstBlood("Game", "Chal", "Pwn", "ev*il_team",
            SubmissionType.FirstBlood, DateTimeOffset.UtcNow);
        var teamField = embed.Fields.Single(f => f.Name == "Team");
        Assert.Equal("ev\\*il\\_team", teamField.Value);
    }

    [Fact]
    public void Cheat_OmitsFlag_WhenShowFlagFalse()
    {
        var embed = DiscordEmbedFactory.BuildCheat("Game", "Chal", "Web", "Cheater", "Owner", "user",
            "flag{secret}", 42, DateTimeOffset.UtcNow, showFlag: false);
        Assert.DoesNotContain(embed.Fields, f => f.Name == "Submitted Flag");
    }

    [Fact]
    public void Cheat_IncludesFlag_WhenShowFlagTrue()
    {
        var embed = DiscordEmbedFactory.BuildCheat("Game", "Chal", "Web", "Cheater", "Owner", "user",
            "flag{secret}", 42, DateTimeOffset.UtcNow, showFlag: true);
        var flagField = embed.Fields.Single(f => f.Name == "Submitted Flag");
        Assert.Contains("flag{secret}", flagField.Value);
    }

    [Fact]
    public void Cheat_IncludesBothTeamsAndSubmissionId()
    {
        var embed = DiscordEmbedFactory.BuildCheat("Game", "Chal", "Web", "Cheater", "Owner", "user",
            "flag{x}", 42, DateTimeOffset.UtcNow, showFlag: false);
        Assert.Contains(embed.Fields, f => f.Value == "Cheater");
        Assert.Contains(embed.Fields, f => f.Value == "Owner");
        Assert.NotNull(embed.Footer);
        Assert.Contains("42", embed.Footer!.Text);
    }

    [Fact]
    public void DiscordMessage_AllowedMentions_IsAlwaysEmpty()
    {
        var message = new DiscordMessage();
        Assert.NotNull(message.AllowedMentions);
        Assert.Empty(message.AllowedMentions.Parse);
    }

    [Fact]
    public void FormatTimestamp_UsesDiscordDynamicFormat()
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        Assert.Equal("<t:1700000000:F>", DiscordEmbedFactory.FormatTimestamp(time));
    }
}
