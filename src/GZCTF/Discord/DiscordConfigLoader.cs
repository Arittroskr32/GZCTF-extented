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

        if (!firstBloodEnabled && !cheatEnabled)
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
            Games = new HashSet<int>(options.Games)
        };
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
