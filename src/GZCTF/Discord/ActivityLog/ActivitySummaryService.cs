using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// Drives activity-log reports: it drains on-demand report requests (from the admin endpoint and its own
/// triggers) and, on a one-minute heartbeat, enqueues periodic reports for running games
/// (<c>summary.interval_minutes</c>) and a final report when a game ends (<c>summary.post_at_game_end</c>).
/// Each request is handled in its own scope via <see cref="ActivityReportService" />.
/// </summary>
internal sealed class ActivitySummaryService(
    ChannelReader<ReportRequest> reader,
    DiscordConfig config,
    IServiceScopeFactory scopeFactory,
    ILogger<ActivitySummaryService> logger) : BackgroundService
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastInterval = new();
    private readonly HashSet<int> _endReported = [];

    private ActivityLogConfig Config => config.ActivityLog!;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(DrainLoop(stoppingToken), HeartbeatLoop(stoppingToken));
    }

    private async Task DrainLoop(CancellationToken token)
    {
        try
        {
            await foreach (var request in reader.ReadAllAsync(token))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var reports = scope.ServiceProvider.GetRequiredService<ActivityReportService>();
                    await reports.PostAsync(request.GameId, request.ChallengeId, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[ActivityLog] Failed to handle report request for game {GameId}",
                        request.GameId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task HeartbeatLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            // Run once promptly, then every minute.
            do
            {
                await TickAsync(token);
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var scope = scopeFactory.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Periodic reports for running games.
            if (Config.SummaryIntervalMinutes > 0)
            {
                var running = await ctx.Games.AsNoTracking()
                    .Where(g => g.StartTimeUtc <= now && now <= g.EndTimeUtc)
                    .Select(g => g.Id).ToListAsync(token);

                var interval = TimeSpan.FromMinutes(Config.SummaryIntervalMinutes);
                foreach (var gameId in running)
                {
                    var last = _lastInterval.GetValueOrDefault(gameId, DateTimeOffset.MinValue);
                    if (now - last < interval)
                        continue;
                    _lastInterval[gameId] = now;
                    await EnqueueAsync(scope, gameId, token);
                }
            }

            // Final report for games that ended since this service started.
            if (Config.PostAtGameEnd)
            {
                var justEnded = await ctx.Games.AsNoTracking()
                    .Where(g => g.EndTimeUtc > _startedAt && g.EndTimeUtc <= now)
                    .Select(g => g.Id).ToListAsync(token);

                foreach (var gameId in justEnded)
                {
                    if (!_endReported.Add(gameId))
                        continue;
                    await EnqueueAsync(scope, gameId, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ActivityLog] Summary heartbeat failed");
        }
    }

    private static async Task EnqueueAsync(AsyncServiceScope scope, int gameId, CancellationToken token)
    {
        var writer = scope.ServiceProvider.GetRequiredService<ChannelWriter<ReportRequest>>();
        await writer.WriteAsync(new ReportRequest(gameId, null), token);
    }
}
