using Microsoft.EntityFrameworkCore;

namespace GZCTF.Discord.ActivityLog;

/// <inheritdoc cref="IActivityLogRepository" />
internal sealed class ActivityLogRepository(AppDbContext context, ILogger<ActivityLogRepository> logger)
    : IActivityLogRepository
{
    public async Task<bool> AddAsync(ActivityLogEntry entry, CancellationToken token = default)
    {
        try
        {
            await context.ActivityLogEntries.AddAsync(entry, token);
            await context.SaveChangesAsync(token);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ActivityLog] Failed to persist a {Type} entry for game {GameId}",
                entry.Type, entry.GameId);
            return false;
        }
    }

    public Task<List<ActivityLogEntry>> GetTimelineAsync(int gameId, int? challengeId = null,
        CancellationToken token = default)
    {
        var query = context.ActivityLogEntries.AsNoTracking().Where(e => e.GameId == gameId);
        if (challengeId is not null)
            query = query.Where(e => e.ChallengeId == challengeId);

        return query
            .OrderBy(e => e.ChallengeId)
            .ThenBy(e => e.TeamId)
            .ThenBy(e => e.TimeUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(token);
    }

    public Task<List<int>> GetChallengeIdsWithActivityAsync(int gameId, CancellationToken token = default) =>
        context.ActivityLogEntries.AsNoTracking()
            .Where(e => e.GameId == gameId)
            .Select(e => e.ChallengeId)
            .Distinct()
            .ToListAsync(token);
}
