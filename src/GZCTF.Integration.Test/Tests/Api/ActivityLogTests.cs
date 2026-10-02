using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using GZCTF.Discord.ActivityLog;
using GZCTF.Integration.Test.Base;
using GZCTF.Models;
using GZCTF.Models.Data;
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
/// Activity-log integration tests: the flag-result hook fires through the real pipeline for tracked
/// container challenges, a faulty logger cannot break flag checking, the entry table persists (migration
/// applied), and the admin endpoints enforce the Monitor role.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ActivityLogTests(GZCTFApplicationFactory factory)
{
    private const string Password = "Activity@Log123";

    [Fact]
    public async Task FlagResultHook_FiresForAcceptedAndWrong_OnTrackedChallenge()
    {
        var recorder = new RecordingActivityLogger();
        using var app = CreateAppWithRecorder(recorder);

        var game = await TestDataSeeder.CreateGameAsync(app.Services, $"Act Flag {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(app.Services, game.Id, "Act Chal");

        var flag = $"flag{{own_{TestDataSeeder.RandomName()}}}";
        var team = await SetupTeamAsync(app, game.Id, challenge.Id, flag, "A");

        var wrong = await SubmitAndWaitAsync(team.client, game.Id, challenge.Id, "flag{definitely_wrong}");
        Assert.Equal(AnswerResult.WrongAnswer, wrong);

        var accepted = await SubmitAndWaitAsync(team.client, game.Id, challenge.Id, flag);
        Assert.Equal(AnswerResult.Accepted, accepted);

        Assert.True(await WaitForAsync(() =>
            recorder.Has(challenge.Id, ActivityLogType.FlagWrong) &&
            recorder.Has(challenge.Id, ActivityLogType.FlagAccepted)));
    }

    [Fact]
    public async Task FlagResultHook_RecordsCheat_ForSharedFlag()
    {
        var recorder = new RecordingActivityLogger();
        using var app = CreateAppWithRecorder(recorder);

        var game = await TestDataSeeder.CreateGameAsync(app.Services, $"Act Cheat {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(app.Services, game.Id, "Cheat Chal");

        var flagA = $"flag{{a_{TestDataSeeder.RandomName()}}}";
        var flagB = $"flag{{b_{TestDataSeeder.RandomName()}}}";
        await SetupTeamAsync(app, game.Id, challenge.Id, flagA, "A");
        var teamB = await SetupTeamAsync(app, game.Id, challenge.Id, flagB, "B");

        var status = await SubmitAndWaitAsync(teamB.client, game.Id, challenge.Id, flagA);
        Assert.Equal(AnswerResult.Accepted, status);

        Assert.True(await WaitForAsync(() => recorder.Has(challenge.Id, ActivityLogType.FlagCheatDetected)));
    }

    [Fact]
    public async Task FaultyActivityLogger_DoesNotBreakFlagChecking()
    {
        using var app = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IActivityLogger>();
                services.AddSingleton<IActivityLogger>(new ThrowingActivityLogger());
            }));

        var game = await TestDataSeeder.CreateGameAsync(app.Services, $"Act Fault {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(app.Services, game.Id, "Fault Chal");
        var flag = $"flag{{own_{TestDataSeeder.RandomName()}}}";
        var team = await SetupTeamAsync(app, game.Id, challenge.Id, flag, "F");

        var status = await SubmitAndWaitAsync(team.client, game.Id, challenge.Id, flag);
        Assert.Equal(AnswerResult.Accepted, status);

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var solved = await ctx.FirstSolves.AsNoTracking()
            .AnyAsync(fs => fs.ParticipationId == team.participationId && fs.ChallengeId == challenge.Id);
        Assert.True(solved, "scoring must be unaffected by an activity-logger fault");
    }

    [Fact]
    public async Task ActivityLogEntries_PersistAndQueryInTeamTimeOrder()
    {
        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var gameId = 900000 + Random.Shared.Next(1, 90000);
        var baseTime = DateTimeOffset.UtcNow;

        ctx.ActivityLogEntries.AddRange(
            Entry(gameId, 5, 7, ActivityLogType.FlagAccepted, baseTime.AddMinutes(5)),
            Entry(gameId, 5, 3, ActivityLogType.ContainerStarted, baseTime),
            Entry(gameId, 5, 3, ActivityLogType.FlagWrong, baseTime.AddMinutes(2)));
        await ctx.SaveChangesAsync();

        var timeline = await ctx.ActivityLogEntries.AsNoTracking()
            .Where(e => e.GameId == gameId)
            .OrderBy(e => e.TeamId).ThenBy(e => e.TimeUtc)
            .ToListAsync();

        Assert.Equal(3, timeline.Count);
        Assert.Equal(3, timeline[0].TeamId); // team 3 entries first
        Assert.Equal(ActivityLogType.ContainerStarted, timeline[0].Type);
        Assert.Equal(ActivityLogType.FlagWrong, timeline[1].Type);
        Assert.Equal(7, timeline[2].TeamId);
    }

    [Fact]
    public async Task AdminEndpoints_RequireMonitorRole()
    {
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"Act Auth {TestDataSeeder.RandomName()}");

        // Anonymous → 401.
        var anon = factory.CreateClient();
        using (var r = await anon.GetAsync($"/api/Game/{game.Id}/ActivityLog"))
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using (var r = await anon.PostAsync($"/api/Game/{game.Id}/ActivityLog/Report", null))
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);

        // Regular user → 403.
        var userClient = await LoginAsAsync(Role.User, "u");
        using (var r = await userClient.GetAsync($"/api/Game/{game.Id}/ActivityLog"))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        // Monitor → authorized; feature disabled in test env → 400 (not 401/403).
        var monitorClient = await LoginAsAsync(Role.Monitor, "m");
        using (var r = await monitorClient.GetAsync($"/api/Game/{game.Id}/ActivityLog"))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using (var r = await monitorClient.PostAsync($"/api/Game/{game.Id}/ActivityLog/Report", null))
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    #region Helpers

    private static ActivityLogEntry Entry(int gameId, int participationId, int teamId, ActivityLogType type,
        DateTimeOffset time) => new()
    {
        GameId = gameId,
        ChallengeId = 1,
        ParticipationId = participationId,
        TeamId = teamId,
        TeamName = $"Team {teamId}",
        Type = type,
        TimeUtc = time
    };

    private WebApplicationFactory<Program> CreateAppWithRecorder(RecordingActivityLogger recorder) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IActivityLogger>();
                services.AddSingleton<IActivityLogger>(recorder);
            }));

    private async Task<HttpClient> LoginAsAsync(Role role, string label)
    {
        var userName = $"{label}{TestDataSeeder.RandomName(10)}";
        var user = await TestDataSeeder.CreateUserAsync(factory.Services, userName, Password, role: role);
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/Account/LogIn",
            new LoginModel { UserName = user.UserName, Password = Password });
        login.EnsureSuccessStatusCode();
        return client;
    }

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

    private sealed class RecordingActivityLogger : IActivityLogger
    {
        public ConcurrentQueue<(int ChallengeId, ActivityLogType Type)> Events { get; } = new();

        public bool Has(int challengeId, ActivityLogType type)
        {
            foreach (var e in Events)
                if (e.ChallengeId == challengeId && e.Type == type)
                    return true;
            return false;
        }

        public void ContainerStarted(int gameId, int challengeId, ChallengeType type, int participationId,
            int teamId, string teamName, Guid? userId, string? userName) =>
            Events.Enqueue((challengeId, ActivityLogType.ContainerStarted));

        public void ContainerStartFailed(int gameId, int challengeId, ChallengeType type, int participationId,
            int teamId, string teamName, Guid? userId, string? userName, string reason) =>
            Events.Enqueue((challengeId, ActivityLogType.ContainerStartFailed));

        public void ContainerExtended(int gameId, int challengeId, ChallengeType type, int participationId,
            int teamId, string teamName, Guid? userId, string? userName) =>
            Events.Enqueue((challengeId, ActivityLogType.ContainerExtended));

        public void ContainerDestroyed(int participationId, int challengeId, ContainerDestroyReason reason) =>
            Events.Enqueue((challengeId, ActivityLogType.ContainerDestroyedByUser));

        public void FlagResult(int gameId, int challengeId, ChallengeType type, int participationId, int teamId,
            string teamName, Guid? userId, string? userName, AnswerResult result, SubmissionType bloodTier,
            string? sourceTeamName, string? flag, DateTimeOffset timeUtc)
        {
            var logType = result switch
            {
                AnswerResult.Accepted => ActivityLogType.FlagAccepted,
                AnswerResult.CheatDetected => ActivityLogType.FlagCheatDetected,
                _ => ActivityLogType.FlagWrong
            };
            Events.Enqueue((challengeId, logType));
        }
    }

    private sealed class ThrowingActivityLogger : IActivityLogger
    {
        public void ContainerStarted(int gameId, int challengeId, ChallengeType type, int participationId,
            int teamId, string teamName, Guid? userId, string? userName) => throw new InvalidOperationException();

        public void ContainerStartFailed(int gameId, int challengeId, ChallengeType type, int participationId,
            int teamId, string teamName, Guid? userId, string? userName, string reason) =>
            throw new InvalidOperationException();

        public void ContainerExtended(int gameId, int challengeId, ChallengeType type, int participationId,
            int teamId, string teamName, Guid? userId, string? userName) => throw new InvalidOperationException();

        public void ContainerDestroyed(int participationId, int challengeId, ContainerDestroyReason reason) =>
            throw new InvalidOperationException();

        public void FlagResult(int gameId, int challengeId, ChallengeType type, int participationId, int teamId,
            string teamName, Guid? userId, string? userName, AnswerResult result, SubmissionType bloodTier,
            string? sourceTeamName, string? flag, DateTimeOffset timeUtc) => throw new InvalidOperationException();
    }
}
