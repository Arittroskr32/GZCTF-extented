using System.Text;

namespace GZCTF.Discord;

/// <summary>
/// Builds Discord embeds for the two notification kinds. All user-controlled text (team names, user
/// names, challenge titles, flags) is markdown-escaped and truncated to Discord's embed limits.
/// </summary>
internal static class DiscordEmbedFactory
{
    // Discord embed length limits.
    private const int TitleLimit = 256;
    private const int FieldNameLimit = 256;
    private const int FieldValueLimit = 1024;

    // Colours (decimal values of hex RGB).
    private const int FirstBloodColor = 0xE74C3C; // red
    private const int SecondBloodColor = 0xE67E22; // orange
    private const int ThirdBloodColor = 0xF1C40F; // yellow
    private const int CheatColor = 0x992D22; // dark red (warning)

    internal static DiscordEmbed BuildFirstBlood(string gameTitle, string challengeTitle, string category,
        string teamName, SubmissionType bloodType, DateTimeOffset solveTimeUtc)
    {
        var (title, color) = bloodType switch
        {
            SubmissionType.FirstBlood => ("\U0001FA78 First Blood!", FirstBloodColor),
            SubmissionType.SecondBlood => ("\U0001F948 Second Blood", SecondBloodColor),
            SubmissionType.ThirdBlood => ("\U0001F949 Third Blood", ThirdBloodColor),
            _ => ("Blood", FirstBloodColor)
        };

        return new DiscordEmbed
        {
            Title = Truncate(title, TitleLimit),
            Color = color,
            Timestamp = solveTimeUtc.ToUniversalTime().ToString("o"),
            Fields =
            [
                Field("Team", Escape(teamName), true),
                Field("Challenge", Escape(challengeTitle), true),
                Field("Category", Escape(category), true),
                Field("Solved", FormatTimestamp(solveTimeUtc), false)
            ],
            Footer = new DiscordEmbedFooter { Text = Truncate(Escape(gameTitle), FieldValueLimit) }
        };
    }

    internal static DiscordEmbed BuildCheat(string gameTitle, string challengeTitle, string category,
        string submitTeamName, string sourceTeamName, string submitUserName, string flag, int submissionId,
        DateTimeOffset submitTimeUtc, bool showFlag)
    {
        var embed = new DiscordEmbed
        {
            Title = Truncate("⚠️ Flag Sharing Detected", TitleLimit),
            Color = CheatColor,
            Timestamp = submitTimeUtc.ToUniversalTime().ToString("o"),
            Fields =
            [
                Field("Submitting Team (cheater)", Escape(submitTeamName), true),
                Field("Flag Owner (source)", Escape(sourceTeamName), true),
                Field("Challenge", Escape(challengeTitle), true),
                Field("Category", Escape(category), true),
                Field("Submitting User", Escape(submitUserName), true),
                Field("Submitted", FormatTimestamp(submitTimeUtc), true)
            ],
            Footer = new DiscordEmbedFooter
            {
                Text = Truncate($"{Escape(gameTitle)} · submission #{submissionId}", FieldValueLimit)
            }
        };

        if (showFlag)
            // Wrap the flag in an inline code span so it renders literally; escape backticks first.
            embed.Fields.Add(Field("Submitted Flag",
                Truncate($"`{flag.Replace("`", "'")}`", FieldValueLimit), false));

        return embed;
    }

    private static DiscordEmbedField Field(string name, string value, bool inline) => new()
    {
        Name = Truncate(string.IsNullOrEmpty(name) ? "​" : name, FieldNameLimit),
        Value = Truncate(string.IsNullOrEmpty(value) ? "​" : value, FieldValueLimit),
        Inline = inline
    };

    /// <summary>
    /// Render a Discord dynamic timestamp (<c>&lt;t:unix:F&gt;</c>) that each viewer sees in their
    /// own locale/timezone.
    /// </summary>
    internal static string FormatTimestamp(DateTimeOffset time) => $"<t:{time.ToUnixTimeSeconds()}:F>";

    /// <summary>
    /// Escape Discord markdown so user-controlled text cannot inject formatting or mentions.
    /// </summary>
    internal static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '#' or '-' or '[' or ']' or '(' or ')'
                or '@' or ':')
                sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    internal static string Truncate(string text, int limit)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= limit)
            return text;

        if (limit <= 1)
            return text[..limit];

        return string.Concat(text.AsSpan(0, limit - 1), "…");
    }
}
