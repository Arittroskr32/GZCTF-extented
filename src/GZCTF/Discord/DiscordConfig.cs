namespace GZCTF.Discord;

/// <summary>
/// Validated, runtime Discord configuration. Only constructed by <see cref="DiscordConfigLoader" />
/// after the on-disk <see cref="DiscordOptions" /> has passed validation. A broken part (e.g. an
/// invalid channel id) is disabled individually rather than failing the whole integration.
/// </summary>
internal sealed class DiscordConfig
{
    public required string BotToken { get; init; }

    public bool FirstBloodEnabled { get; init; }

    public ulong FirstBloodChannelId { get; init; }

    public bool IncludeSecondThirdBlood { get; init; }

    public bool CheatEnabled { get; init; }

    public ulong CheatChannelId { get; init; }

    public bool ShowSubmittedFlag { get; init; }

    /// <summary>
    /// Validated activity-log configuration, or null when the activity log is disabled.
    /// </summary>
    public ActivityLogConfig? ActivityLog { get; init; }

    /// <summary>
    /// Game IDs to notify for; empty means all games.
    /// </summary>
    public IReadOnlySet<int> Games { get; init; } = new HashSet<int>();

    /// <summary>
    /// True when at least one notification type is enabled.
    /// </summary>
    public bool AnyEnabled => FirstBloodEnabled || CheatEnabled || ActivityLog is not null;

    /// <summary>
    /// Whether the given game should produce notifications.
    /// </summary>
    public bool ShouldNotifyGame(int gameId) => Games.Count == 0 || Games.Contains(gameId);

    /// <summary>
    /// Distinct channel ids that are actually in use, for the startup access check.
    /// Works correctly whether the two channels are the same id or different ids.
    /// </summary>
    public IEnumerable<ulong> ActiveChannelIds
    {
        get
        {
            var ids = new HashSet<ulong>();
            if (FirstBloodEnabled)
                ids.Add(FirstBloodChannelId);
            if (CheatEnabled)
                ids.Add(CheatChannelId);
            if (ActivityLog is not null)
                ids.Add(ActivityLog.ChannelId);
            return ids;
        }
    }
}

/// <summary>
/// Validated, runtime configuration for the activity-log module. Only constructed by
/// <see cref="DiscordConfigLoader" /> when the section is enabled and valid.
/// </summary>
internal sealed class ActivityLogConfig
{
    public required ulong ChannelId { get; init; }

    /// <summary>
    /// Container challenge types to track (validated subset of DynamicContainer / StaticContainer).
    /// </summary>
    public required IReadOnlySet<ChallengeType> ChallengeTypes { get; init; }

    /// <summary>
    /// Timezone used to render report times; defaults to UTC when unset/invalid.
    /// </summary>
    public required TimeZoneInfo Timezone { get; init; }

    public bool LiveFeedEnabled { get; init; }

    public int BatchSeconds { get; init; }

    public int SummaryIntervalMinutes { get; init; }

    public bool PostAtGameEnd { get; init; }

    public int FastSolveMinutes { get; init; }

    public bool NoWrongAttempts { get; init; }

    public int CloseSolveWindowMinutes { get; init; }

    public bool Tracks(ChallengeType type) => ChallengeTypes.Contains(type);
}
