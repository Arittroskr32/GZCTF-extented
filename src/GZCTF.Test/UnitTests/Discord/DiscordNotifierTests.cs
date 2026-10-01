using System;
using System.Collections.Generic;
using System.Threading.Channels;
using GZCTF.Discord;
using GZCTF.Models.Internal;
using GZCTF.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GZCTF.Test.UnitTests.Discord;

public class DiscordNotifierTests
{
    private static (DiscordNotifier notifier, ChannelReader<DiscordNotification> reader) Create(
        DiscordConfig config, int capacity = 100)
    {
        var channel = Channel.CreateBounded<DiscordNotification>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        var notifier = new DiscordNotifier(channel.Writer, config, NullLogger<DiscordNotifier>.Instance);
        return (notifier, channel.Reader);
    }

    private static DiscordConfig Config(bool firstBlood = true, bool cheat = true, bool secondThird = false,
        IReadOnlySet<int>? games = null) => new()
    {
        BotToken = "token",
        FirstBloodEnabled = firstBlood,
        FirstBloodChannelId = 1,
        IncludeSecondThirdBlood = secondThird,
        CheatEnabled = cheat,
        CheatChannelId = 2,
        ShowSubmittedFlag = false,
        Games = games ?? new HashSet<int>()
    };

    [Fact]
    public void NotifyFirstBlood_FirstBlood_IsEnqueued()
    {
        var (notifier, reader) = Create(Config());
        notifier.NotifyFirstBlood(1, 2, 3, SubmissionType.FirstBlood, DateTimeOffset.UtcNow);

        Assert.True(reader.TryRead(out var n));
        Assert.IsType<FirstBloodNotification>(n);
    }

    [Fact]
    public void NotifyFirstBlood_NormalType_IsIgnored()
    {
        var (notifier, reader) = Create(Config());
        notifier.NotifyFirstBlood(1, 2, 3, SubmissionType.Normal, DateTimeOffset.UtcNow);
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void NotifyFirstBlood_SecondBlood_RespectsIncludeFlag()
    {
        var (off, offReader) = Create(Config(secondThird: false));
        off.NotifyFirstBlood(1, 2, 3, SubmissionType.SecondBlood, DateTimeOffset.UtcNow);
        Assert.False(offReader.TryRead(out _));

        var (on, onReader) = Create(Config(secondThird: true));
        on.NotifyFirstBlood(1, 2, 3, SubmissionType.SecondBlood, DateTimeOffset.UtcNow);
        Assert.True(onReader.TryRead(out _));
    }

    [Fact]
    public void NotifyFirstBlood_Disabled_IsIgnored()
    {
        var (notifier, reader) = Create(Config(firstBlood: false));
        notifier.NotifyFirstBlood(1, 2, 3, SubmissionType.FirstBlood, DateTimeOffset.UtcNow);
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void NotifyFirstBlood_RespectsGamesFilter()
    {
        var (notifier, reader) = Create(Config(games: new HashSet<int> { 5 }));

        notifier.NotifyFirstBlood(6, 2, 3, SubmissionType.FirstBlood, DateTimeOffset.UtcNow);
        Assert.False(reader.TryRead(out _));

        notifier.NotifyFirstBlood(5, 2, 3, SubmissionType.FirstBlood, DateTimeOffset.UtcNow);
        Assert.True(reader.TryRead(out _));
    }

    [Fact]
    public void NotifyCheat_IsEnqueued_WithMappedFields()
    {
        var (notifier, reader) = Create(Config());
        var cheat = new CheatCheckInfo
        {
            SubmitTeamName = "Cheater",
            SourceTeamName = "Owner",
            CheatUserName = "user",
            Flag = "flag{x}"
        };

        notifier.NotifyCheat(1, 2, 42, cheat, DateTimeOffset.UtcNow);

        Assert.True(reader.TryRead(out var n));
        var cheatNotification = Assert.IsType<CheatNotification>(n);
        Assert.Equal("Cheater", cheatNotification.SubmitTeamName);
        Assert.Equal("Owner", cheatNotification.SourceTeamName);
        Assert.Equal(42, cheatNotification.SubmissionId);
    }

    [Fact]
    public void NotifyCheat_Disabled_IsIgnored()
    {
        var (notifier, reader) = Create(Config(cheat: false));
        notifier.NotifyCheat(1, 2, 42, new CheatCheckInfo(), DateTimeOffset.UtcNow);
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void Enqueue_WhenQueueFull_DropsWithoutThrowing()
    {
        var (notifier, reader) = Create(Config(), capacity: 1);

        // First one fills the queue, subsequent ones are dropped (no exception).
        notifier.NotifyFirstBlood(1, 2, 3, SubmissionType.FirstBlood, DateTimeOffset.UtcNow);
        notifier.NotifyFirstBlood(1, 2, 4, SubmissionType.FirstBlood, DateTimeOffset.UtcNow);
        notifier.NotifyFirstBlood(1, 2, 5, SubmissionType.FirstBlood, DateTimeOffset.UtcNow);

        Assert.True(reader.TryRead(out _));
        Assert.False(reader.TryRead(out _));
    }
}
