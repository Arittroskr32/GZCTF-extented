using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// Background worker for the activity log. Drains the raw-event queue, resolves and filters each event off
/// the hot path, persists it to <see cref="ActivityLogEntry" />, and (when the live feed is enabled)
/// batches persisted entries per challenge and posts a compact message to that challenge's thread every
/// <c>batch_seconds</c>. Draining and flushing run concurrently; neither can throw into the producers.
/// </summary>
internal sealed class ActivityLogService(
    ChannelReader<RawActivityEvent> reader,
    DiscordConfig config,
    IServiceScopeFactory scopeFactory,
    DiscordApiClient apiClient,
    ActivityThreadManager threadManager,
    ILogger<ActivityLogService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, ConcurrentQueue<ActivityLogEntry>> _pending = new();
    private ActivityLogConfig Config => config.ActivityLog!;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var drain = DrainLoop(stoppingToken);
        var flush = Config.LiveFeedEnabled ? FlushLoop(stoppingToken) : Task.CompletedTask;
        await Task.WhenAll(drain, flush);
    }

    private async Task DrainLoop(CancellationToken token)
    {
        try
        {
            await foreach (var raw in reader.ReadAllAsync(token))
            {
                try
                {
                    var entry = await ResolveAndPersistAsync(raw, token);
                    if (entry is not null && Config.LiveFeedEnabled)
                        _pending.GetOrAdd(entry.ChallengeId, _ => new ConcurrentQueue<ActivityLogEntry>())
                            .Enqueue(entry);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[ActivityLog] Failed to process an activity event");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task<ActivityLogEntry?> ResolveAndPersistAsync(RawActivityEvent raw, CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IActivityLogRepository>();

        ActivityLogEntry entry;

        if (raw.NeedsResolution)
        {
            var resolved = await ResolveDestroyAsync(ctx, raw, token);
            if (resolved is null)
                return null;
            entry = resolved;
        }
        else
        {
            entry = new ActivityLogEntry
            {
                GameId = raw.GameId,
                ChallengeId = raw.ChallengeId,
                ParticipationId = raw.ParticipationId,
                TeamId = raw.TeamId,
                TeamName = raw.TeamName,
                UserId = raw.UserId,
                UserName = raw.UserName,
                Type = raw.Type,
                TimeUtc = raw.TimeUtc,
                Detail = raw.Detail,
                Flag = raw.Flag
            };
        }

        return await repo.AddAsync(entry, token) ? entry : null;
    }

    private async Task<ActivityLogEntry?> ResolveDestroyAsync(AppDbContext ctx, RawActivityEvent raw,
        CancellationToken token)
    {
        var info = await ctx.GameInstances.AsNoTracking()
            .Where(i => i.ParticipationId == raw.ParticipationId && i.ChallengeId == raw.ChallengeId)
            .Select(i => new
            {
                i.ChallengeId,
                i.Challenge.Type,
                i.Challenge.GameId,
                i.ParticipationId,
                i.Participation.TeamId,
                TeamName = i.Participation.Team.Name
            })
            .FirstOrDefaultAsync(token);

        if (info is null || !Config.Tracks(info.Type))
            return null;

        // A user destroy only needs the solve check (to distinguish auto-destroy-after-solve).
        var solved = raw.DestroyReason == ContainerDestroyReason.User &&
                     await IsSolvedAsync(ctx, info.ParticipationId, info.ChallengeId, token);
        var type = ActivityTimelineBuilder.MapDestroyType(raw.DestroyReason, solved);

        return new ActivityLogEntry
        {
            GameId = info.GameId,
            ChallengeId = info.ChallengeId,
            ParticipationId = info.ParticipationId,
            TeamId = info.TeamId,
            TeamName = info.TeamName,
            Type = type,
            TimeUtc = raw.TimeUtc
        };
    }

    private static Task<bool> IsSolvedAsync(AppDbContext ctx, int participationId, int challengeId,
        CancellationToken token) =>
        ctx.FirstSolves.AsNoTracking()
            .AnyAsync(fs => fs.ParticipationId == participationId && fs.ChallengeId == challengeId, token);

    private async Task FlushLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Config.BatchSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                await FlushOnceAsync(token);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task FlushOnceAsync(CancellationToken token)
    {
        foreach (var challengeId in _pending.Keys.ToList())
        {
            if (!_pending.TryGetValue(challengeId, out var queue) || queue.IsEmpty)
                continue;

            var drained = new List<ActivityLogEntry>();
            while (queue.TryDequeue(out var e))
                drained.Add(e);

            if (drained.Count == 0)
                continue;

            try
            {
                await PostLiveBatchAsync(challengeId, drained, token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[ActivityLog] Failed to post live feed for challenge {Id}", challengeId);
            }
        }
    }

    private async Task PostLiveBatchAsync(int challengeId, List<ActivityLogEntry> entries,
        CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var title = await ctx.GameChallenges.AsNoTracking()
            .Where(c => c.Id == challengeId).Select(c => c.Title).FirstOrDefaultAsync(token);
        if (title is null)
            return;

        var threadId = await threadManager.GetOrCreateThreadAsync(challengeId, title, token);
        if (threadId is null)
            return;

        var lines = entries
            .OrderBy(e => e.TimeUtc).ThenBy(e => e.Id)
            .Select(ActivityTimelineBuilder.FormatLiveLine);

        foreach (var chunk in ActivityTimelineBuilder.SplitIntoMessages(lines))
            await apiClient.SendMessageAsync(threadId.Value, new DiscordMessage { Content = chunk }, token);
    }
}
