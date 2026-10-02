using GZCTF.Utils;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GZCTF.Discord;
using GZCTF.Discord.ActivityLog;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GZCTF.Test.UnitTests.Discord.ActivityLog;

public class ActivityThreadManagerTests
{
    private const ulong Channel = 345678901234567890UL;

    private static DiscordConfig Config() => new()
    {
        BotToken = "token",
        ActivityLog = new ActivityLogConfig
        {
            ChannelId = Channel,
            ChallengeTypes = new HashSet<ChallengeType> { ChallengeType.DynamicContainer },
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

    private static ActivityThreadManager Manager(RouteHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://discord.com/api/v10/") };
        var api = new DiscordApiClient(http, NullLogger<DiscordApiClient>.Instance);
        return new ActivityThreadManager(api, Config(), NullLogger<ActivityThreadManager>.Instance);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("#12 Login Bypass", 12, true)]
    [InlineData("#12", 12, true)]
    [InlineData("#120 Other", 12, false)]
    [InlineData("#1 Something", 12, false)]
    public void NameMatches_UsesIdPrefixBoundary(string name, int id, bool expected) =>
        Assert.Equal(expected, ActivityThreadManager.NameMatches(name, id));

    [Fact]
    public async Task GetOrCreate_ReusesActiveThread_WithoutCreating()
    {
        var created = false;
        var handler = new RouteHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith($"/channels/{Channel}"))
                return Json("""{"guild_id":"999"}""");
            if (req.Method == HttpMethod.Get && path.Contains("/guilds/999/threads/active"))
                return Json("""{"threads":[{"id":"111","name":"#12 Login","thread_metadata":{"archived":false}}]}""");
            if (req.Method == HttpMethod.Post && path.Contains("/threads"))
            {
                created = true;
                return Json("""{"id":"999"}""");
            }
            return Json("""{"threads":[]}""");
        });

        var manager = Manager(handler);
        var id = await manager.GetOrCreateThreadAsync(12, "Login");

        Assert.Equal(111UL, id);
        Assert.False(created);
    }

    [Fact]
    public async Task GetOrCreate_ReusesArchivedThread_Unarchives_WithoutCreating()
    {
        var unarchived = false;
        var created = false;
        var handler = new RouteHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith($"/channels/{Channel}"))
                return Json("""{"guild_id":"999"}""");
            if (req.Method == HttpMethod.Get && path.Contains("/threads/active"))
                return Json("""{"threads":[]}""");
            if (req.Method == HttpMethod.Get && path.Contains("/threads/archived/public"))
                return Json("""{"threads":[{"id":"222","name":"#12 Login","thread_metadata":{"archived":true}}]}""");
            if (req.Method == HttpMethod.Patch)
            {
                unarchived = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.Method == HttpMethod.Post)
            {
                created = true;
                return Json("""{"id":"999"}""");
            }
            return Json("{}");
        });

        var manager = Manager(handler);
        var id = await manager.GetOrCreateThreadAsync(12, "Login");

        Assert.Equal(222UL, id);
        Assert.True(unarchived);
        Assert.False(created);
    }

    [Fact]
    public async Task GetOrCreate_CreatesNewThread_WhenNoneExist_AndCachesIt()
    {
        var createCalls = 0;
        var handler = new RouteHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith($"/channels/{Channel}"))
                return Json("""{"guild_id":"999"}""");
            if (req.Method == HttpMethod.Get && path.Contains("/threads"))
                return Json("""{"threads":[]}""");
            if (req.Method == HttpMethod.Post && path.Contains("/threads"))
            {
                createCalls++;
                return Json("""{"id":"777"}""");
            }
            return Json("{}");
        });

        var manager = Manager(handler);
        var id1 = await manager.GetOrCreateThreadAsync(12, "Login");
        var id2 = await manager.GetOrCreateThreadAsync(12, "Login"); // cached, no extra create

        Assert.Equal(777UL, id1);
        Assert.Equal(777UL, id2);
        Assert.Equal(1, createCalls);
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(route(request));
    }
}
