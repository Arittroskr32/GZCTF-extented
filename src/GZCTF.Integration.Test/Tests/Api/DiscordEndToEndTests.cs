using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GZCTF.Integration.Test.Base;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Account;
using GZCTF.Models.Request.Game;
using GZCTF.Utils;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GZCTF.Integration.Test.Tests.Api;

/// <summary>
/// End-to-end tests of the Discord integration with a real <c>discord.yml</c>: config loading, DI wiring,
/// background workers and the REST client all run for real, and only the HTTP transport is replaced by a
/// fake Discord server that records every request.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class DiscordEndToEndTests(GZCTFApplicationFactory factory)
{
    private const string Password = "D1scord@E2E123";
    private const ulong GuildId = 100000000000000001;
    private const ulong BloodChannel = 100000000000000010;
    private const ulong CheatChannel = 100000000000000020;
    private const ulong ActivityChannel = 100000000000000030;
    private const ulong OtherChannel = 100000000000000040;
    private const string ConfigPathEnv = "GZCTF_DISCORD_CONFIG";

    [Fact]
    public async Task FullPipeline_PostsBloodCheatLiveFeedAndReport_ToTheRightChannels()
    {
        var discord = new FakeDiscord();
        using var app = CreateApp(discord, showFlag: false);

        var game = await TestDataSeeder.CreateGameAsync(app.Services, $"E2E {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(app.Services, game.Id, "E2E Chal");

        // A thread with the same "#<id>" prefix in another (e.g. public) channel must never be reused.
        discord.ActiveThreads.Add((900000000000000001, $"#{challenge.Id} decoy", OtherChannel));

        var flagA = $"flag{{a_{TestDataSeeder.RandomName()}}}";
        var flagB = $"flag{{b_{TestDataSeeder.RandomName()}}}";
        var teamA = await SetupTeamAsync(app, game.Id, challenge.Id, flagA, "A");
        var teamB = await SetupTeamAsync(app, game.Id, challenge.Id, flagB, "B");

        // Team B cheats first with team A's flag: accepted for the player, cheat alert, no blood.
        Assert.Equal(AnswerResult.Accepted, await SubmitAndWaitAsync(teamB.client, game.Id, challenge.Id, flagA));
        // Team A then solves legitimately and still takes first blood.
        Assert.Equal(AnswerResult.WrongAnswer,
            await SubmitAndWaitAsync(teamA.client, game.Id, challenge.Id, "flag{nope}"));
        Assert.Equal(AnswerResult.Accepted, await SubmitAndWaitAsync(teamA.client, game.Id, challenge.Id, flagA));

        Assert.True(await WaitForAsync(() => discord.MessagesTo(BloodChannel).Count == 1 &&
                                             discord.MessagesTo(CheatChannel).Count == 1),
            "expected one first-blood and one cheat message");

        var blood = discord.MessagesTo(BloodChannel).Single();
        Assert.Contains(teamA.teamName, blood);
        Assert.DoesNotContain(teamB.teamName, blood);

        var cheat = discord.MessagesTo(CheatChannel).Single();
        Assert.Contains(teamB.teamName, cheat);
        Assert.DoesNotContain(flagA, cheat); // show_submitted_flag: false
        Assert.Contains("\"parse\":[]", cheat); // never pings

        // Activity entries are persisted for the tracked container challenge.
        using (var scope = app.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await WaitForAsync(() => ctx.ActivityLogEntries.AsNoTracking()
                .Count(e => e.ChallengeId == challenge.Id) == 3));
            var types = await ctx.ActivityLogEntries.AsNoTracking()
                .Where(e => e.ChallengeId == challenge.Id).Select(e => e.Type).ToListAsync();
            Assert.Contains(ActivityLogType.FlagCheatDetected, types);
            Assert.Contains(ActivityLogType.FlagWrong, types);
            Assert.Contains(ActivityLogType.FlagAccepted, types);
        }

        // Live feed: one thread created under the private activity channel (decoy ignored), no flags.
        Assert.True(await WaitForAsync(() => discord.CreatedThreads.Count == 1 &&
                                             discord.MessagesTo(discord.CreatedThreads[0].Id).Count >= 1,
            maxAttempts: 150), "expected the live feed in a new activity thread");
        Assert.Equal(ActivityChannel, discord.CreatedThreads[0].Parent);
        Assert.Empty(discord.MessagesTo(900000000000000001));
        var feed = string.Join("\n", discord.MessagesTo(discord.CreatedThreads[0].Id));
        Assert.DoesNotContain(flagA, feed);
        Assert.DoesNotContain("flag{nope}", feed);

        // On-demand report as a monitor: summary + timeline .txt posted to the challenge thread.
        var monitor = await LoginAsAsync(app, Role.Monitor);
        using (var res = await monitor.PostAsync(
                   $"/api/Game/{game.Id}/ActivityLog/Report?challengeId={challenge.Id}", null))
            res.EnsureSuccessStatusCode();
        Assert.True(await WaitForAsync(() => discord.Uploads.Any(u => u.Channel == discord.CreatedThreads[0].Id)),
            "expected the report upload in the challenge thread");

        // CSV export works and hides flags when show_submitted_flag is false.
        var csv = await monitor.GetStringAsync(
            $"/api/Game/{game.Id}/ActivityLog?challengeId={challenge.Id}&format=csv");
        Assert.Contains(teamB.teamName, csv);
        Assert.DoesNotContain(flagA, csv);

        // Nothing was ever posted to the decoy channel or the public channel beyond the blood.
        Assert.Empty(discord.MessagesTo(OtherChannel));
    }

    [Fact]
    public async Task GamesAllowList_SkipsOtherGames_ForAllDiscordPosts()
    {
        var discord = new FakeDiscord();
        using var app = CreateApp(discord, showFlag: true, games: [int.MaxValue]);

        var game = await TestDataSeeder.CreateGameAsync(app.Services, $"E2E Off {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(app.Services, game.Id, "Off Chal");
        var flagA = $"flag{{a_{TestDataSeeder.RandomName()}}}";
        var teamA = await SetupTeamAsync(app, game.Id, challenge.Id, flagA, "A");

        Assert.Equal(AnswerResult.Accepted, await SubmitAndWaitAsync(teamA.client, game.Id, challenge.Id, flagA));

        // The entry is still stored for the admin export...
        using (var scope = app.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await WaitForAsync(() =>
                ctx.ActivityLogEntries.AsNoTracking().Any(e => e.ChallengeId == challenge.Id)));
        }

        // ...but nothing for this game reaches Discord (wait past one live-feed batch).
        await Task.Delay(TimeSpan.FromSeconds(7));
        Assert.Empty(discord.MessagesTo(BloodChannel));
        Assert.Empty(discord.CreatedThreads);
    }

    [Fact]
    public async Task CheatBetweenLegitSolves_BloodsSkipCheater_AndCheatAlertShowsFlag()
    {
        var discord = new FakeDiscord();
        using var app = CreateApp(discord, showFlag: true, allBloods: true);

        var game = await TestDataSeeder.CreateGameAsync(app.Services, $"E2E Bloods {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(app.Services, game.Id, "Bloods Chal");

        var flags = Enumerable.Range(0, 4).Select(i => $"flag{{t{i}_{TestDataSeeder.RandomName()}}}").ToArray();
        var teams = new List<(HttpClient client, string teamName)>();
        for (var i = 0; i < 4; i++)
            teams.Add(await SetupTeamAsync(app, game.Id, challenge.Id, flags[i], $"T{i}"));

        // T0 solves (1st blood), T1 copies T0's flag (cheat), then T2 and T3 solve legitimately.
        Assert.Equal(AnswerResult.Accepted, await SubmitAndWaitAsync(teams[0].client, game.Id, challenge.Id, flags[0]));
        Assert.Equal(AnswerResult.Accepted, await SubmitAndWaitAsync(teams[1].client, game.Id, challenge.Id, flags[0]));
        Assert.Equal(AnswerResult.Accepted, await SubmitAndWaitAsync(teams[2].client, game.Id, challenge.Id, flags[2]));
        Assert.Equal(AnswerResult.Accepted, await SubmitAndWaitAsync(teams[3].client, game.Id, challenge.Id, flags[3]));

        Assert.True(await WaitForAsync(() => discord.MessagesTo(BloodChannel).Count == 3 &&
                                             discord.MessagesTo(CheatChannel).Count == 1));

        // The cheater never takes a blood slot: 2nd and 3rd blood go to T2 and T3.
        var bloods = discord.MessagesTo(BloodChannel);
        Assert.Contains(bloods, b => b.Contains(teams[0].teamName));
        Assert.Contains(bloods, b => b.Contains(teams[2].teamName));
        Assert.Contains(bloods, b => b.Contains(teams[3].teamName));
        Assert.DoesNotContain(bloods, b => b.Contains(teams[1].teamName));

        // The private alert names cheater and source, and shows the flag when configured.
        var cheat = discord.MessagesTo(CheatChannel).Single();
        Assert.Contains(teams[1].teamName, cheat);
        Assert.Contains(teams[0].teamName, cheat);
        Assert.Contains(flags[0], cheat);

        // Scoreboard: the cheater is scored, and the blood types match the Discord posts.
        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var solves = await ctx.FirstSolves.AsNoTracking()
            .Where(fs => fs.ChallengeId == challenge.Id).CountAsync();
        Assert.Equal(4, solves);
    }

    #region Helpers

    private WebApplicationFactory<Program> CreateApp(FakeDiscord discord, bool showFlag, int[]? games = null,
        bool allBloods = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"discord-e2e-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, $"""
            enabled: true
            bot_token: "test-token"
            first_blood:
              enabled: true
              channel_id: "{BloodChannel}"
              include_second_third_blood: {(allBloods ? "true" : "false")}
            cheat_detection:
              enabled: true
              channel_id: "{CheatChannel}"
              show_submitted_flag: {(showFlag ? "true" : "false")}
            activity_log:
              enabled: true
              channel_id: "{ActivityChannel}"
              challenge_types: ["DynamicContainer"]
              live_feed:
                enabled: true
                batch_seconds: 5
              summary:
                interval_minutes: 0
                post_at_game_end: false
            games: [{string.Join(", ", games ?? [])}]
            """);

        var app = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                // DiscordApiClient is internal; a typed client's name is its type name.
                services.AddHttpClient("DiscordApiClient").ConfigurePrimaryHttpMessageHandler(() => discord)));

        // The config path is read while services are registered, i.e. when the host is first built.
        var previous = Environment.GetEnvironmentVariable(ConfigPathEnv);
        Environment.SetEnvironmentVariable(ConfigPathEnv, path);
        try
        {
            _ = app.Services;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigPathEnv, previous);
            File.Delete(path);
        }

        return app;
    }

    private static async Task<HttpClient> LoginAsAsync(WebApplicationFactory<Program> app, Role role)
    {
        var userName = $"mon{TestDataSeeder.RandomName(10)}";
        await TestDataSeeder.CreateUserAsync(app.Services, userName, Password, role: role);
        var client = app.CreateClient();
        using var res = await client.PostAsJsonAsync("/api/Account/LogIn",
            new LoginModel { UserName = userName, Password = Password });
        res.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<(HttpClient client, string teamName)> SetupTeamAsync(
        WebApplicationFactory<Program> app, int gameId, int challengeId, string flag, string label)
    {
        var services = app.Services;
        var userName = $"{label}{TestDataSeeder.RandomName(10)}";
        var user = await TestDataSeeder.CreateUserAsync(services, userName, Password);
        var teamName = $"Team {label} {userName}";
        var team = await TestDataSeeder.CreateTeamAsync(services, user.Id, teamName);
        var participation = await TestDataSeeder.JoinGameAsync(services, gameId, team.Id, user.Id);
        await TestDataSeeder.SetInstanceFlagAsync(services, participation.Id, challengeId, flag);

        var client = app.CreateClient();
        using var res = await client.PostAsJsonAsync("/api/Account/LogIn",
            new LoginModel { UserName = user.UserName, Password = Password });
        res.EnsureSuccessStatusCode();
        return (client, teamName);
    }

    private static async Task<AnswerResult> SubmitAndWaitAsync(HttpClient client, int gameId, int challengeId,
        string flag)
    {
        using var response = await client.PostAsJsonAsync($"/api/Game/{gameId}/Challenges/{challengeId}",
            new FlagSubmitModel { Flag = flag });
        response.EnsureSuccessStatusCode();
        var submitId = await response.Content.ReadFromJsonAsync<int>();

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var status = await client.GetFromJsonAsync<AnswerResult>(
                $"/api/Game/{gameId}/Challenges/{challengeId}/Status/{submitId}");
            if (status != AnswerResult.FlagSubmitted)
                return status;
            await Task.Delay(100);
        }

        Assert.Fail($"Submission {submitId} was not processed in time.");
        return AnswerResult.FlagSubmitted;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int maxAttempts = 100, int delayMs = 100)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (condition())
                return true;
            await Task.Delay(delayMs);
        }

        return condition();
    }

    #endregion

    /// <summary>
    /// Minimal fake of the Discord REST endpoints the integration uses. Records every posted message.
    /// </summary>
    private sealed class FakeDiscord : HttpMessageHandler
    {
        private long _nextId = 800000000000000000;

        public ConcurrentQueue<(ulong Channel, string Body)> Messages { get; } = new();
        public ConcurrentQueue<(ulong Channel, string Body)> Uploads { get; } = new();
        public List<(ulong Id, ulong Parent)> CreatedThreads { get; } = [];
        public List<(ulong Id, string Name, ulong Parent)> ActiveThreads { get; } = [];

        public List<string> MessagesTo(ulong channel) =>
            Messages.Where(m => m.Channel == channel).Select(m => m.Body).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(s => s != "v10").Skip(1).ToArray();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            switch (segments)
            {
                case ["channels", var id, "messages"] when request.Method == HttpMethod.Post:
                    var channel = ulong.Parse(id);
                    if (request.Content is MultipartFormDataContent)
                        Uploads.Enqueue((channel, body));
                    else
                        Messages.Enqueue((channel, body));
                    return Json(new { id = Interlocked.Increment(ref _nextId).ToString() });
                case ["channels", var id, "threads"] when request.Method == HttpMethod.Post:
                    var threadId = (ulong)Interlocked.Increment(ref _nextId);
                    lock (CreatedThreads)
                        CreatedThreads.Add((threadId, ulong.Parse(id)));
                    return Json(new { id = threadId.ToString() });
                case ["channels", _, "threads", "archived", "public"]:
                    return Json(new { threads = Array.Empty<object>() });
                case ["guilds", _, "threads", "active"]:
                    return Json(new
                    {
                        threads = ActiveThreads.Select(t => new
                        {
                            id = t.Id.ToString(),
                            name = t.Name,
                            parent_id = t.Parent.ToString(),
                            thread_metadata = new { archived = false }
                        })
                    });
                case ["channels", var id] when request.Method == HttpMethod.Get:
                    return Json(new { id, guild_id = GuildId.ToString() });
                default:
                    return Json(new { }, HttpStatusCode.OK);
            }
        }

        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };
    }
}
