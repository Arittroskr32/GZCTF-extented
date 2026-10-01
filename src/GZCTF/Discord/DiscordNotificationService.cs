using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Discord;

/// <summary>
/// Background worker that drains the notification queue and posts to Discord. All DB lookups needed
/// to enrich an embed happen here (off the flag-checking hot path). On startup it verifies the bot can
/// access each configured channel.
/// </summary>
internal sealed class DiscordNotificationService(
    ChannelReader<DiscordNotification> reader,
    DiscordApiClient apiClient,
    DiscordConfig config,
    IServiceScopeFactory scopeFactory,
    ILogger<DiscordNotificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await VerifyChannelsAsync(stoppingToken);

        try
        {
            await foreach (var notification in reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await HandleAsync(notification, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[Discord] Failed to process a {Type}", notification.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task VerifyChannelsAsync(CancellationToken token)
    {
        foreach (var channelId in config.ActiveChannelIds)
        {
            var (ok, error) = await apiClient.CheckChannelAsync(channelId, token);
            if (ok)
                logger.LogInformation("[Discord] Channel {ChannelId} is accessible", channelId);
            else
                logger.LogError("[Discord] Cannot use channel {ChannelId}: {Error}", channelId, error);
        }
    }

    private async Task HandleAsync(DiscordNotification notification, CancellationToken token)
    {
        switch (notification)
        {
            case FirstBloodNotification blood:
                await HandleFirstBloodAsync(blood, token);
                break;
            case CheatNotification cheat:
                await HandleCheatAsync(cheat, token);
                break;
        }
    }

    private async Task HandleFirstBloodAsync(FirstBloodNotification blood, CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var gameTitle = await context.Games.AsNoTracking()
            .Where(g => g.Id == blood.GameId).Select(g => g.Title).FirstOrDefaultAsync(token);

        var challenge = await context.GameChallenges.AsNoTracking()
            .Where(c => c.Id == blood.ChallengeId)
            .Select(c => new { c.Title, c.Category })
            .FirstOrDefaultAsync(token);

        var teamName = await context.Teams.AsNoTracking()
            .Where(t => t.Id == blood.TeamId).Select(t => t.Name).FirstOrDefaultAsync(token);

        if (gameTitle is null || challenge is null)
        {
            logger.LogDebug("[Discord] Skipping first blood: game/challenge no longer exists");
            return;
        }

        var embed = DiscordEmbedFactory.BuildFirstBlood(
            gameTitle,
            challenge.Title,
            challenge.Category.ToString(),
            teamName ?? string.Empty,
            blood.BloodType,
            blood.SolveTimeUtc);

        await apiClient.SendMessageAsync(config.FirstBloodChannelId, Wrap(embed), token);
    }

    private async Task HandleCheatAsync(CheatNotification cheat, CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var gameTitle = await context.Games.AsNoTracking()
            .Where(g => g.Id == cheat.GameId).Select(g => g.Title).FirstOrDefaultAsync(token);

        var challenge = await context.GameChallenges.AsNoTracking()
            .Where(c => c.Id == cheat.ChallengeId)
            .Select(c => new { c.Title, c.Category })
            .FirstOrDefaultAsync(token);

        var embed = DiscordEmbedFactory.BuildCheat(
            gameTitle ?? string.Empty,
            challenge?.Title ?? string.Empty,
            challenge?.Category.ToString() ?? string.Empty,
            cheat.SubmitTeamName,
            cheat.SourceTeamName,
            cheat.SubmitUserName,
            cheat.Flag,
            cheat.SubmissionId,
            cheat.SubmitTimeUtc,
            config.ShowSubmittedFlag);

        await apiClient.SendMessageAsync(config.CheatChannelId, Wrap(embed), token);
    }

    private static DiscordMessage Wrap(DiscordEmbed embed) => new() { Embeds = [embed] };
}
