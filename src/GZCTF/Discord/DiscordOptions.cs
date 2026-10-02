namespace GZCTF.Discord;

/// <summary>
/// Raw shape of <c>discord.yml</c>, deserialized with an underscored naming convention.
/// This is the on-disk form; it is validated into a <see cref="DiscordConfig" /> before use.
/// </summary>
internal sealed class DiscordOptions
{
    public bool Enabled { get; set; }

    public string? BotToken { get; set; }

    public FirstBloodOptions FirstBlood { get; set; } = new();

    public CheatDetectionOptions CheatDetection { get; set; } = new();

    public ActivityLogOptions ActivityLog { get; set; } = new();

    /// <summary>
    /// Optional list of game IDs to notify for. Empty means all games.
    /// </summary>
    public List<int> Games { get; set; } = [];
}

internal sealed class FirstBloodOptions
{
    public bool Enabled { get; set; }

    public string? ChannelId { get; set; }

    /// <summary>
    /// Also post second and third blood (with distinct titles/colours).
    /// </summary>
    public bool IncludeSecondThirdBlood { get; set; }
}

internal sealed class CheatDetectionOptions
{
    public bool Enabled { get; set; }

    public string? ChannelId { get; set; }

    /// <summary>
    /// Include the submitted (shared) flag value in the private alert.
    /// </summary>
    public bool ShowSubmittedFlag { get; set; }
}

/// <summary>
/// Raw shape of the <c>activity_log</c> section of <c>discord.yml</c>.
/// </summary>
internal sealed class ActivityLogOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// PRIVATE channel id (threads are created inside it). Admins + bot only.
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Challenge types to track. Values are <c>ChallengeType</c> names (e.g. "DynamicContainer",
    /// "StaticContainer"). Only container types are meaningful; others are ignored. Default:
    /// DynamicContainer only.
    /// </summary>
    public List<string> ChallengeTypes { get; set; } = ["DynamicContainer"];

    /// <summary>
    /// IANA/Windows timezone id used to render times in reports (e.g. "Asia/Dhaka"). Default UTC.
    /// </summary>
    public string? Timezone { get; set; }

    public LiveFeedOptions LiveFeed { get; set; } = new();

    public SummaryOptions Summary { get; set; } = new();

    public SuspicionOptions Suspicion { get; set; } = new();
}

internal sealed class LiveFeedOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often (seconds) to flush batched entries into one message per challenge thread.
    /// </summary>
    public int BatchSeconds { get; set; } = 30;
}

internal sealed class SummaryOptions
{
    /// <summary>
    /// Minutes between periodic full reports; 0 disables the periodic report.
    /// </summary>
    public int IntervalMinutes { get; set; } = 60;

    public bool PostAtGameEnd { get; set; } = true;
}

internal sealed class SuspicionOptions
{
    /// <summary>
    /// Flag a solve within N minutes of that team's most recent container start.
    /// </summary>
    public int FastSolveMinutes { get; set; } = 10;

    /// <summary>
    /// Also flag a solve that had zero wrong submissions.
    /// </summary>
    public bool NoWrongAttempts { get; set; } = true;

    /// <summary>
    /// Flag solves within N minutes after another team's solve of the same challenge.
    /// </summary>
    public int CloseSolveWindowMinutes { get; set; } = 15;
}
