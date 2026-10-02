using System.Text.Json.Serialization;

namespace GZCTF.Discord;

/// <summary>
/// JSON payload for <c>POST /channels/{id}/messages</c>. <see cref="AllowedMentions" /> is always an
/// empty parse list so no content can ever ping @everyone, roles or users.
/// </summary>
internal sealed class DiscordMessage
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("embeds")]
    public List<DiscordEmbed> Embeds { get; set; } = [];

    [JsonPropertyName("allowed_mentions")]
    public AllowedMentions AllowedMentions { get; set; } = new();
}

internal sealed class AllowedMentions
{
    [JsonPropertyName("parse")]
    public string[] Parse { get; set; } = [];
}

internal sealed class DiscordEmbed
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("color")]
    public int Color { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("fields")]
    public List<DiscordEmbedField> Fields { get; set; } = [];

    [JsonPropertyName("footer")]
    public DiscordEmbedFooter? Footer { get; set; }
}

internal sealed class DiscordEmbedField
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("inline")]
    public bool Inline { get; set; }
}

internal sealed class DiscordEmbedFooter
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}
