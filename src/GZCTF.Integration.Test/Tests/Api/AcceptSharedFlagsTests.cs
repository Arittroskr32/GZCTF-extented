using System.Net.Http.Json;
using GZCTF.Integration.Test.Base;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Account;
using GZCTF.Models.Request.Game;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Cache;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GZCTF.Integration.Test.Tests.Api;

/// <summary>
/// Integration tests for the "accept shared dynamic flags" behaviour: a team submitting another team's
/// dynamic flag is accepted and scored, while the cheat is still detected and recorded for admins.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class AcceptSharedFlagsTests(GZCTFApplicationFactory factory)
{
    private const string Password = "Shar3d@Flag123";

    /// <summary>
    /// Team B submits Team A's dynamic flag: status endpoint returns Accepted, a FirstSolve exists for
    /// Team B, a CheatInfo exists (SubmitTeam = B, SourceTeam = A), and the stored submission status is
    /// CheatDetected.
    /// </summary>
    [Fact]
    public async Task SharedDynamicFlag_IsAccepted_AndRecordedAsCheat()
    {
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"Shared Flag Game {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(factory.Services, game.Id,
            "Dynamic Share Challenge");

        var flagA = $"flag{{team_a_{TestDataSeeder.RandomName()}}}";
        var flagB = $"flag{{team_b_{TestDataSeeder.RandomName()}}}";

        var teamA = await SetupTeamAsync(game.Id, challenge.Id, flagA, "A");
        var teamB = await SetupTeamAsync(game.Id, challenge.Id, flagB, "B");

        // Team B submits Team A's flag.
        var (status, submitId) = await SubmitAndWaitAsync(teamB.client, game.Id, challenge.Id, flagA);

        // The player sees Accepted (identical to a legitimate correct flag).
        Assert.Equal(AnswerResult.Accepted, status);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // A FirstSolve is recorded for Team B.
        var firstSolve = await ctx.FirstSolves.AsNoTracking()
            .FirstOrDefaultAsync(fs => fs.ParticipationId == teamB.participationId && fs.ChallengeId == challenge.Id);
        Assert.NotNull(firstSolve);

        // The stored submission status is CheatDetected.
        var submission = await ctx.Submissions.AsNoTracking().FirstAsync(s => s.Id == submitId);
        Assert.Equal(AnswerResult.CheatDetected, submission.Status);

        // A CheatInfo record exists: SubmitTeam = B, SourceTeam = A.
        var cheat = await ctx.CheatInfo.AsNoTracking()
            .Include(c => c.SubmitTeam)
            .Include(c => c.SourceTeam)
            .FirstOrDefaultAsync(c => c.SubmissionId == submitId);
        Assert.NotNull(cheat);
        Assert.Equal(teamB.team.Id, cheat.SubmitTeam.TeamId);
        Assert.Equal(teamA.team.Id, cheat.SourceTeam.TeamId);
    }

    /// <summary>
    /// Team B (cheater) solves first and Team C solves legitimately second: Team C gets FirstBlood,
    /// and Team B gets no blood bonus on the scoreboard.
    /// </summary>
    [Fact]
    public async Task CheatedSolve_DoesNotTakeBlood_LegitSolverGetsFirstBlood()
    {
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"Blood Game {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(factory.Services, game.Id,
            "Dynamic Blood Challenge");

        var flagA = $"flag{{a_{TestDataSeeder.RandomName()}}}";
        var flagB = $"flag{{b_{TestDataSeeder.RandomName()}}}";
        var flagC = $"flag{{c_{TestDataSeeder.RandomName()}}}";

        var teamA = await SetupTeamAsync(game.Id, challenge.Id, flagA, "A");
        var teamB = await SetupTeamAsync(game.Id, challenge.Id, flagB, "B");
        var teamC = await SetupTeamAsync(game.Id, challenge.Id, flagC, "C");

        // Team B cheats first (submits Team A's flag), fully processed.
        var (statusB, _) = await SubmitAndWaitAsync(teamB.client, game.Id, challenge.Id, flagA);
        Assert.Equal(AnswerResult.Accepted, statusB);

        // Team C solves legitimately second.
        var (statusC, _) = await SubmitAndWaitAsync(teamC.client, game.Id, challenge.Id, flagC);
        Assert.Equal(AnswerResult.Accepted, statusC);

        var scoreboard = await GetScoreboardAsync(game.Id);

        var challengeInfo = scoreboard.ChallengeMap[challenge.Id];

        // Team C takes the first blood; Team B (cheater) is not in the blood list at all.
        Assert.Contains(challengeInfo.Bloods, b => b.Id == teamC.team.Id);
        Assert.DoesNotContain(challengeInfo.Bloods, b => b.Id == teamB.team.Id);
        Assert.Equal(teamC.team.Id, challengeInfo.Bloods[0].Id);

        var itemB = scoreboard.Items[teamB.team.Id];
        var itemC = scoreboard.Items[teamC.team.Id];

        var solveB = itemB.SolvedChallenges.Single(c => c.Id == challenge.Id);
        var solveC = itemC.SolvedChallenges.Single(c => c.Id == challenge.Id);

        Assert.Equal(SubmissionType.Normal, solveB.Type);
        Assert.Equal(SubmissionType.FirstBlood, solveC.Type);
    }

    /// <summary>
    /// Team B's cheated solve appears on the scoreboard with normal (non-blood) dynamic points.
    /// </summary>
    [Fact]
    public async Task CheatedSolve_AppearsOnScoreboard_WithNormalPoints()
    {
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"Score Game {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(factory.Services, game.Id,
            "Dynamic Score Challenge");

        var flagA = $"flag{{a_{TestDataSeeder.RandomName()}}}";
        var flagB = $"flag{{b_{TestDataSeeder.RandomName()}}}";

        var teamA = await SetupTeamAsync(game.Id, challenge.Id, flagA, "A");
        var teamB = await SetupTeamAsync(game.Id, challenge.Id, flagB, "B");

        var (status, _) = await SubmitAndWaitAsync(teamB.client, game.Id, challenge.Id, flagA);
        Assert.Equal(AnswerResult.Accepted, status);

        var scoreboard = await GetScoreboardAsync(game.Id);

        var itemB = scoreboard.Items[teamB.team.Id];
        var solveB = itemB.SolvedChallenges.Single(c => c.Id == challenge.Id);

        // Scored normally (no blood multiplier): the solve equals the challenge's current base score.
        Assert.Equal(SubmissionType.Normal, solveB.Type);
        Assert.True(solveB.Score > 0, "Cheated solve should receive dynamic challenge points");
        Assert.Equal(scoreboard.ChallengeMap[challenge.Id].Score, solveB.Score);
        Assert.True(itemB.Score > 0, "Team score should include the cheated solve");
    }

    /// <summary>
    /// Team B submits a shared flag after it has already solved the challenge: no duplicate FirstSolve,
    /// but a CheatInfo is still created and the player still sees Accepted.
    /// </summary>
    [Fact]
    public async Task SharedFlag_AfterAlreadySolved_NoDuplicateFirstSolve_CheatStillRecorded()
    {
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"AlreadySolved Game {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateDynamicChallengeAsync(factory.Services, game.Id,
            "Dynamic AlreadySolved Challenge");

        var flagA = $"flag{{a_{TestDataSeeder.RandomName()}}}";
        var flagB = $"flag{{b_{TestDataSeeder.RandomName()}}}";

        var teamA = await SetupTeamAsync(game.Id, challenge.Id, flagA, "A");
        var teamB = await SetupTeamAsync(game.Id, challenge.Id, flagB, "B");

        // Team B legitimately solves its own flag first.
        var (ownStatus, _) = await SubmitAndWaitAsync(teamB.client, game.Id, challenge.Id, flagB);
        Assert.Equal(AnswerResult.Accepted, ownStatus);

        // Then Team B submits Team A's flag (cheat) after already having solved.
        var (sharedStatus, sharedSubmitId) = await SubmitAndWaitAsync(teamB.client, game.Id, challenge.Id, flagA);
        Assert.Equal(AnswerResult.Accepted, sharedStatus);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Exactly one FirstSolve for Team B on this challenge (no duplicate).
        var firstSolveCount = await ctx.FirstSolves.AsNoTracking()
            .CountAsync(fs => fs.ParticipationId == teamB.participationId && fs.ChallengeId == challenge.Id);
        Assert.Equal(1, firstSolveCount);

        // The cheat for the second submission is still recorded.
        var cheat = await ctx.CheatInfo.AsNoTracking()
            .Include(c => c.SourceTeam)
            .FirstOrDefaultAsync(c => c.SubmissionId == sharedSubmitId);
        Assert.NotNull(cheat);
        Assert.Equal(teamA.team.Id, cheat.SourceTeam.TeamId);

        var submission = await ctx.Submissions.AsNoTracking().FirstAsync(s => s.Id == sharedSubmitId);
        Assert.Equal(AnswerResult.CheatDetected, submission.Status);
    }

    /// <summary>
    /// Static challenge behaviour is unchanged: a correct (shared) static flag is a normal accept with no
    /// CheatInfo, and a wrong flag is a wrong answer.
    /// </summary>
    [Fact]
    public async Task StaticChallenge_Behaviour_IsUnchanged()
    {
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"Static Game {TestDataSeeder.RandomName()}");
        var flag = $"flag{{static_{TestDataSeeder.RandomName()}}}";
        var challenge = await TestDataSeeder.CreateStaticChallengeAsync(factory.Services, game.Id,
            "Static Challenge", flag);

        var teamA = await SetupTeamAsync(game.Id, challenge.Id, flag: null, "A");
        var teamB = await SetupTeamAsync(game.Id, challenge.Id, flag: null, "B");

        // Both teams submit the same (correct, shared) static flag - this is legitimate, not a cheat.
        var (statusA, submitA) = await SubmitAndWaitAsync(teamA.client, game.Id, challenge.Id, flag);
        var (statusB, submitB) = await SubmitAndWaitAsync(teamB.client, game.Id, challenge.Id, flag);

        Assert.Equal(AnswerResult.Accepted, statusA);
        Assert.Equal(AnswerResult.Accepted, statusB);

        // A wrong flag is still a wrong answer.
        var (wrongStatus, _) = await SubmitAndWaitAsync(teamB.client, game.Id, challenge.Id,
            $"flag{{wrong_{TestDataSeeder.RandomName()}}}");
        Assert.Equal(AnswerResult.WrongAnswer, wrongStatus);

        using var scope = factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // No cheat was recorded for either accepted static submission.
        var cheatCount = await ctx.CheatInfo.AsNoTracking()
            .CountAsync(c => c.SubmissionId == submitA || c.SubmissionId == submitB);
        Assert.Equal(0, cheatCount);

        var subA = await ctx.Submissions.AsNoTracking().FirstAsync(s => s.Id == submitA);
        var subB = await ctx.Submissions.AsNoTracking().FirstAsync(s => s.Id == submitB);
        Assert.Equal(AnswerResult.Accepted, subA.Status);
        Assert.Equal(AnswerResult.Accepted, subB.Status);
    }

    #region Helpers

    private async Task<(TestDataSeeder.SeededUser user, TestDataSeeder.SeededTeam team, HttpClient client, int
        participationId)> SetupTeamAsync(int gameId, int challengeId, string? flag, string label)
    {
        var userName = $"{label}{TestDataSeeder.RandomName(10)}";
        var user = await TestDataSeeder.CreateUserAsync(factory.Services, userName, Password);
        var team = await TestDataSeeder.CreateTeamAsync(factory.Services, user.Id, $"Team {label} {userName}");

        // Join through the seeder so the game instance is created deterministically for the challenge.
        var participation = await TestDataSeeder.JoinGameAsync(factory.Services, gameId, team.Id, user.Id);

        // For dynamic challenges assign this team's own flag to its instance.
        if (flag is not null)
            await TestDataSeeder.SetInstanceFlagAsync(factory.Services, participation.Id, challengeId, flag);

        var client = factory.CreateClient();
        using (var loginResponse = await client.PostAsJsonAsync("/api/Account/LogIn",
                   new LoginModel { UserName = user.UserName, Password = Password }))
        {
            loginResponse.EnsureSuccessStatusCode();
        }

        return (user, team, client, participation.Id);
    }

    private async Task<(AnswerResult status, int submitId)> SubmitAndWaitAsync(HttpClient client, int gameId,
        int challengeId, string flag)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/Game/{gameId}/Challenges/{challengeId}",
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
                return (status, submitId);

            await Task.Delay(100);
        }

        Assert.Fail($"Submission {submitId} was not processed in time.");
        return (AnswerResult.FlagSubmitted, submitId);
    }

    private async Task<ScoreboardModel> GetScoreboardAsync(int gameId)
    {
        using var scope = factory.Services.CreateScope();
        var cacheHelper = scope.ServiceProvider.GetRequiredService<CacheHelper>();
        var gameRepository = scope.ServiceProvider.GetRequiredService<IGameRepository>();

        await cacheHelper.FlushScoreboardCache(gameId, CancellationToken.None);

        var game = await gameRepository.GetGameById(gameId, CancellationToken.None)
                   ?? throw new InvalidOperationException($"Game {gameId} not found");

        return await gameRepository.GetScoreboard(game, CancellationToken.None);
    }

    #endregion
}
