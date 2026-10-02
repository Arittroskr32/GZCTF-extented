using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GZCTF.Discord;

/// <summary>
/// Loads and validates <c>discord.yml</c>. Returns <c>null</c> when the integration should be
/// silently disabled (file missing, parse error, <c>enabled: false</c>, or no usable part), after
/// logging a single clear line. Never throws and never logs the bot token.
/// </summary>
internal static class DiscordConfigLoader
{
    internal const string ConfigPathEnv = "GZCTF_DISCORD_CONFIG";
    internal const string DefaultConfigPath = "/app/discord.yml";

    internal static string ResolveConfigPath() =>
        Environment.GetEnvironmentVariable(ConfigPathEnv) is { Length: > 0 } path ? path : DefaultConfigPath;

    internal static DiscordConfig? Load(string path, Serilog.ILogger logger)
    {
        if (!File.Exists(path))
        {
            logger.Information("[Discord] No config file at {Path}; Discord integration disabled", path);
            return null;
        }

        DiscordOptions? options;
        try
        {
            var yaml = File.ReadAllText(path);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            options = deserializer.Deserialize<DiscordOptions>(yaml);
        }
        catch (Exception ex)
        {
            logger.Error("[Discord] Failed to parse {Path}: {Message}; Discord integration disabled", path,
                ex.Message);
            return null;
        }

        if (options is null || !options.Enabled)
        {
            logger.Information("[Discord] Integration is disabled via config ({Path})", path);
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            logger.Error("[Discord] bot_token is missing; Discord integration disabled");
            return null;
        }

        var firstBloodEnabled = options.FirstBlood.Enabled;
        ulong firstBloodChannel = 0;
        if (firstBloodEnabled)
        {
            if (TryParseSnowflake(options.FirstBlood.ChannelId, out firstBloodChannel))
            {
                logger.Information("[Discord] First blood notifications enabled (channel {ChannelId}{Extra})",
                    firstBloodChannel,
                    options.FirstBlood.IncludeSecondThirdBlood ? ", incl. 2nd/3rd" : "");
            }
            else
            {
                logger.Error(
                    "[Discord] first_blood.channel_id is not a valid Discord channel id; first blood notifications disabled");
                firstBloodEnabled = false;
            }
        }

        var cheatEnabled = options.CheatDetection.Enabled;
        ulong cheatChannel = 0;
        if (cheatEnabled)
        {
            if (TryParseSnowflake(options.CheatDetection.ChannelId, out cheatChannel))
            {
                logger.Information("[Discord] Cheat notifications enabled (channel {ChannelId})", cheatChannel);
            }
            else
            {
                logger.Error(
                    "[Discord] cheat_detection.channel_id is not a valid Discord channel id; cheat notifications disabled");
                cheatEnabled = false;
            }
        }

        var activityLog = LoadActivityLog(options, firstBloodEnabled ? firstBloodChannel : null, logger);

        if (!firstBloodEnabled && !cheatEnabled && activityLog is null)
        {
            logger.Information("[Discord] No notification type is usable; Discord integration disabled");
            return null;
        }

        return new DiscordConfig
        {
            BotToken = options.BotToken.Trim(),
            FirstBloodEnabled = firstBloodEnabled,
            FirstBloodChannelId = firstBloodChannel,
            IncludeSecondThirdBlood = options.FirstBlood.IncludeSecondThirdBlood,
            CheatEnabled = cheatEnabled,
            CheatChannelId = cheatChannel,
            ShowSubmittedFlag = options.CheatDetection.ShowSubmittedFlag,
            ActivityLog = activityLog,
            Games = new HashSet<int>(options.Games)
        };
    }

    /// <summary>
    /// Validate the <c>activity_log</c> section. Returns null (feature disabled) when it is off or
    /// mis-configured, after logging a clear line; never throws. A broken activity-log section never
    /// disables the rest of the integration.
    /// </summary>
    private static ActivityLogConfig? LoadActivityLog(DiscordOptions options, ulong? firstBloodChannel,
        Serilog.ILogger logger)
    {
        var opt = options.ActivityLog;
        if (!opt.Enabled)
            return null;

        if (!TryParseSnowflake(opt.ChannelId, out var channelId))
        {
            logger.Error(
                "[Discord] activity_log.channel_id is not a valid Discord channel id; activity log disabled");
            return null;
        }

        // Security: warn loudly if the private activity channel is the same as the public blood channel.
        if (firstBloodChannel is not null && firstBloodChannel == channelId)
            logger.Warning(
                "[Discord] activity_log.channel_id equals first_blood.channel_id ({ChannelId}); " +
                "private activity data would be posted to the public first-blood channel. Use a separate " +
                "private channel.", channelId);

        var types = new HashSet<ChallengeType>();
        foreach (var raw in opt.ChallengeTypes)
        {
            if (Enum.TryParse<ChallengeType>(raw, true, out var type) && type.IsContainer())
                types.Add(type);
            else
                logger.Warning(
                    "[Discord] activity_log.challenge_types: '{Value}' is not a container challenge type; ignored",
                    raw);
        }

        if (types.Count == 0)
        {
            types.Add(ChallengeType.DynamicContainer);
            logger.Information(
                "[Discord] activity_log.challenge_types resolved to none; defaulting to DynamicContainer");
        }

        var tz = ResolveTimezone(opt.Timezone, logger);

        logger.Information(
            "[Discord] Activity log enabled (channel {ChannelId}, types [{Types}], timezone {Tz})",
            channelId, string.Join(", ", types), tz.Id);

        return new ActivityLogConfig
        {
            ChannelId = channelId,
            ChallengeTypes = types,
            Timezone = tz,
            LiveFeedEnabled = opt.LiveFeed.Enabled,
            BatchSeconds = Math.Clamp(opt.LiveFeed.BatchSeconds, 5, 3600),
            SummaryIntervalMinutes = Math.Max(0, opt.Summary.IntervalMinutes),
            PostAtGameEnd = opt.Summary.PostAtGameEnd,
            FastSolveMinutes = Math.Max(0, opt.Suspicion.FastSolveMinutes),
            NoWrongAttempts = opt.Suspicion.NoWrongAttempts,
            CloseSolveWindowMinutes = Math.Max(0, opt.Suspicion.CloseSolveWindowMinutes)
        };
    }

    internal static TimeZoneInfo ResolveTimezone(string? id, Serilog.ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception)
        {
            logger.Warning("[Discord] activity_log.timezone '{Tz}' not found; falling back to UTC", id);
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// A Discord snowflake is a positive 64-bit integer, in practice 17-20 digits.
    /// </summary>
    internal static bool TryParseSnowflake(string? value, out ulong snowflake)
    {
        snowflake = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length is < 17 or > 20)
            return false;

        return ulong.TryParse(trimmed, out snowflake) && snowflake > 0;
    }
}
