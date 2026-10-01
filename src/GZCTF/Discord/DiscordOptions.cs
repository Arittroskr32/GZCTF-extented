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
