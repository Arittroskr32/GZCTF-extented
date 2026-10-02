namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// Append-only store for <see cref="ActivityLogEntry" /> rows and the queries the reports need.
/// </summary>
public interface IActivityLogRepository
{
    /// <summary>
    /// Persist one entry. Returns false (instead of throwing) if the write fails, so callers on a
    /// best-effort path are never broken.
    /// </summary>
    Task<bool> AddAsync(ActivityLogEntry entry, CancellationToken token = default);

    /// <summary>
    /// All entries for a game (optionally one challenge), ordered by team then time — the order the
    /// timeline report uses.
    /// </summary>
    Task<List<ActivityLogEntry>> GetTimelineAsync(int gameId, int? challengeId = null,
        CancellationToken token = default);

    /// <summary>
    /// Distinct challenge ids that have any recorded activity for the game.
    /// </summary>
    Task<List<int>> GetChallengeIdsWithActivityAsync(int gameId, CancellationToken token = default);
}
