using System.Collections.Concurrent;

namespace GZCTF.Discord.ActivityLog;

/// <summary>
/// Resolves (and caches) the Discord thread used for a challenge's activity. One thread per challenge,
/// named <c>#&lt;challengeId&gt; &lt;title&gt;</c>, created inside the private activity channel. On a miss
/// it looks for an existing thread by name among the channel's active threads and then its archived public
/// threads (unarchiving a match), so a restart reuses the existing thread instead of creating a duplicate.
/// </summary>
internal sealed class ActivityThreadManager(
    DiscordApiClient apiClient,
    DiscordConfig config,
    ILogger<ActivityThreadManager> logger)
{
    private readonly ConcurrentDictionary<int, ulong> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ulong? _guildId;
    private bool _guildResolved;

    private ActivityLogConfig Config => config.ActivityLog!;

    internal static string ThreadName(int challengeId, string title)
    {
        var name = $"#{challengeId} {title}".Trim();
        // Discord thread/channel names are limited to 100 characters.
        return name.Length > 100 ? name[..100] : name;
    }

    /// <summary>
    /// Get or create the thread id for a challenge. Returns null if a thread could not be resolved or
    /// created (the caller then skips posting but the data is still stored).
    /// </summary>
    internal async Task<ulong?> GetOrCreateThreadAsync(int challengeId, string title,
        CancellationToken token = default)
    {
        if (_cache.TryGetValue(challengeId, out var cached))
            return cached;

        await _lock.WaitAsync(token);
        try
        {
            if (_cache.TryGetValue(challengeId, out cached))
                return cached;

            var channelId = Config.ChannelId;
            var wanted = ThreadName(challengeId, title);

            // 1. Active threads in the guild.
            var guildId = await ResolveGuildAsync(channelId, token);
            if (guildId is not null)
            {
                var active = await apiClient.GetActiveThreadsAsync(guildId.Value, token);
                var match = active.FirstOrDefault(t => NameMatches(t.Name, challengeId));
                if (match is not null)
                    return Cache(challengeId, match.Id);
            }

            // 2. Archived public threads under the channel (unarchive on match).
            var archived = await apiClient.GetArchivedThreadsAsync(channelId, token);
            var archivedMatch = archived.FirstOrDefault(t => NameMatches(t.Name, challengeId));
            if (archivedMatch is not null)
            {
                await apiClient.UnarchiveThreadAsync(archivedMatch.Id, token);
                return Cache(challengeId, archivedMatch.Id);
            }

            // 3. Create a new thread.
            var created = await apiClient.CreateThreadAsync(channelId, wanted, token);
            if (created is not null)
                return Cache(challengeId, created.Value);

            logger.LogWarning("[ActivityLog] Could not resolve or create a thread for challenge {Id}",
                challengeId);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ulong?> ResolveGuildAsync(ulong channelId, CancellationToken token)
    {
        if (_guildResolved)
            return _guildId;

        _guildId = await apiClient.GetGuildIdAsync(channelId, token);
        _guildResolved = true;
        return _guildId;
    }

    private ulong Cache(int challengeId, ulong threadId)
    {
        _cache[challengeId] = threadId;
        return threadId;
    }

    /// <summary>
    /// Match a thread to a challenge by its <c>#&lt;id&gt;</c> prefix so a renamed challenge still maps to
    /// the same thread.
    /// </summary>
    internal static bool NameMatches(string threadName, int challengeId)
    {
        var prefix = $"#{challengeId}";
        if (!threadName.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        // Ensure "#12" does not match "#120".
        return threadName.Length == prefix.Length || !char.IsDigit(threadName[prefix.Length]);
    }
}
