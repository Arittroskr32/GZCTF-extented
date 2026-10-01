using System.Collections.Concurrent;
using System.Net.Http.Json;
using GZCTF.Discord;
using GZCTF.Integration.Test.Base;
using GZCTF.Models;
using GZCTF.Models.Internal;
using GZCTF.Models.Request.Account;
using GZCTF.Models.Request.Game;
using GZCTF.Utils;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GZCTF.Integration.Test.Tests.Api;

/// <summary>
/// Verifies the FlagChecker Discord hook points fire correctly: a legitimate first solve produces
/// exactly one first-blood notification; a shared-flag solve produces exactly one cheat notification
/// and no first-blood; and disabling Discord does not affect scoring or submission results.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class DiscordNotificationHookTests(GZCTFApplicationFactory factory)
{
    private const string Password = "D1scord@Hook123";

    [Fact]
    public async Task LegitimateFirstSolve_EnqueuesExactlyOneFirstBlood_AndNoCheat()
    {
        var recorder = new RecordingDiscordNotifier();
        using var app = CreateAppWithRecorder(recorder);

        var game = await TestDataSeeder.CreateGameAsync(app.Services, $"Discord Blood {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(app.Services, game.Id, "Blood Chal");

        var flag = $"flag{{own_{TestDataSeeder.RandomName()}}}";
        var team = await SetupTeamAsync(app, game.Id, challenge.Id, flag, "A");

        var status = await SubmitAndWaitAsync(team.client, game.Id, challenge.Id, flag);
        Assert.Equal(AnswerResult.Accepted, status);

        Assert.True(await WaitForAsync(() => recorder.CountFirstBlood(challenge.Id) == 1),
            "expected exactly one first blood notification");
        Assert.Equal(SubmissionType.FirstBlood, recorder.FirstBloods.Single().BloodType);
        Assert.Empty(recorder.Cheats);
    }

    [Fact]
    public async Task SharedFlagSolve_EnqueuesExactlyOneCheat_AndNoFirstBlood()
    {
        var recorder = new RecordingDiscordNotifier();
        using var app = CreateAppWithRecorder(recorder);

        var game = await TestDataSeeder.CreateGameAsync(app.Services, $"Discord Cheat {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(app.Services, game.Id, "Cheat Chal");

        var flagA = $"flag{{a_{TestDataSeeder.RandomName()}}}";
        var flagB = $"flag{{b_{TestDataSeeder.RandomName()}}}";
        await SetupTeamAsync(app, game.Id, challenge.Id, flagA, "A");
        var teamB = await SetupTeamAsync(app, game.Id, challenge.Id, flagB, "B");

        // Team B submits Team A's flag.
        var status = await SubmitAndWaitAsync(teamB.client, game.Id, challenge.Id, flagA);
        Assert.Equal(AnswerResult.Accepted, status);

        Assert.True(await WaitForAsync(() => recorder.Cheats.Count == 1),
            "expected exactly one cheat notification");
        var cheat = recorder.Cheats.Single();
        Assert.Equal(challenge.Id, cheat.ChallengeId);
        Assert.True(cheat.SubmissionId > 0);
        // A cheated solve is never blood, so no first-blood notification must be produced.
        Assert.Empty(recorder.FirstBloods);
    }

    [Fact]
    public async Task DiscordDisabled_DoesNotAffectScoringOrSubmission()
    {
        // The default factory has no discord.yml, so the integration is disabled (NullDiscordNotifier).
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"Discord Off {TestDataSeeder.RandomName()}");
        var flag = $"flag{{static_{TestDataSeeder.RandomName()}}}";
        var challenge = await TestDataSeeder.CreateStaticChallengeAsync(factory.Services, game.Id, "Off Chal", flag);

        var team = await SetupTeamAsync(factory, game.Id, challenge.Id, null, "Off");

        var status = await SubmitAndWaitAsync(team.client, game.Id, challenge.Id, flag);
        Assert.Equal(AnswerResult.Accepted, status);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var solved = await ctx.FirstSolves.AsNoTracking()
            .AnyAsync(fs => fs.ParticipationId == team.participationId && fs.ChallengeId == challenge.Id);
        Assert.True(solved, "solve should be recorded even when Discord is disabled");
    }

    #region Helpers

    private WebApplicationFactory<Program> CreateAppWithRecorder(RecordingDiscordNotifier recorder) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDiscordNotifier>();
                services.AddSingleton<IDiscordNotifier>(recorder);
            }));

    private static async Task<(TestDataSeeder.SeededUser user, TestDataSeeder.SeededTeam team, HttpClient client, int
        participationId)> SetupTeamAsync(WebApplicationFactory<Program> app, int gameId, int challengeId, string? flag,
        string label)
    {
        var services = app.Services;
        var userName = $"{label}{TestDataSeeder.RandomName(10)}";
        var user = await TestDataSeeder.CreateUserAsync(services, userName, Password);
        var team = await TestDataSeeder.CreateTeamAsync(services, user.Id, $"Team {label} {userName}");
        var participation = await TestDataSeeder.JoinGameAsync(services, gameId, team.Id, user.Id);

        if (flag is not null)
            await TestDataSeeder.SetInstanceFlagAsync(services, participation.Id, challengeId, flag);

        var client = app.CreateClient();
        using (var loginResponse = await client.PostAsJsonAsync("/api/Account/LogIn",
                   new LoginModel { UserName = user.UserName, Password = Password }))
        {
            loginResponse.EnsureSuccessStatusCode();
        }

        return (user, team, client, participation.Id);
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
            using var statusResponse = await client.GetAsync(
                $"/api/Game/{gameId}/Challenges/{challengeId}/Status/{submitId}");
            statusResponse.EnsureSuccessStatusCode();
            var status = await statusResponse.Content.ReadFromJsonAsync<AnswerResult>();
            if (status != AnswerResult.FlagSubmitted)
                return status;
            await Task.Delay(100);
        }

        Assert.Fail($"Submission {submitId} was not processed in time.");
        return AnswerResult.FlagSubmitted;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int maxAttempts = 50, int delayMs = 100)
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
    /// Records every hook invocation for assertions. Thread-safe (the flag checker uses several workers).
    /// </summary>
    private sealed class RecordingDiscordNotifier : IDiscordNotifier
    {
        public ConcurrentQueue<(int GameId, int ChallengeId, int TeamId, SubmissionType BloodType)> FirstBloods { get; }
            = new();

        public ConcurrentQueue<(int GameId, int ChallengeId, int SubmissionId)> Cheats { get; } = new();

        public void NotifyFirstBlood(int gameId, int challengeId, int teamId, SubmissionType bloodType,
            DateTimeOffset solveTimeUtc) => FirstBloods.Enqueue((gameId, challengeId, teamId, bloodType));

        public void NotifyCheat(int gameId, int challengeId, int submissionId, CheatCheckInfo cheat,
            DateTimeOffset submitTimeUtc) => Cheats.Enqueue((gameId, challengeId, submissionId));

        public int CountFirstBlood(int challengeId)
        {
            var count = 0;
            foreach (var fb in FirstBloods)
                if (fb.ChallengeId == challengeId)
                    count++;
            return count;
        }
    }
}
