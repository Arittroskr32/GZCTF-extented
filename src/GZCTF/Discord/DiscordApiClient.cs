using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GZCTF.Discord;

/// <summary>
/// A Discord thread (sub-channel) as returned by the thread-listing endpoints. <see cref="ParentId" /> is
/// the channel the thread lives in (null when Discord did not report it).
/// </summary>
internal sealed record DiscordThread(ulong Id, string Name, bool Archived, ulong? ParentId = null);

/// <summary>
/// Thin Discord REST client over a typed <see cref="HttpClient" /> (auth header configured at
/// registration). Handles rate limiting (429 <c>retry_after</c>) and retries transient failures with
/// backoff. Never throws into the caller: send failures are logged and reported as a bool.
/// </summary>
internal sealed class DiscordApiClient(HttpClient http, ILogger<DiscordApiClient> logger)
{
    private const int MaxAttempts = 4;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Post a message to a channel. Returns true on success. Honours 429 <c>retry_after</c> and
    /// retries 5xx / network errors with backoff, dropping the message after the final attempt.
    /// </summary>
    internal async Task<bool> SendMessageAsync(ulong channelId, DiscordMessage message,
        CancellationToken token = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var content = JsonContent.Create(message, options: JsonOptions);
                using var response =
                    await http.PostAsync($"channels/{channelId}/messages", content, token);

                if (response.IsSuccessStatusCode)
                    return true;

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var delay = await GetRetryAfterAsync(response, token);
                    logger.LogWarning("[Discord] Rate limited on channel {ChannelId}; retrying in {Delay}s",
                        channelId, delay.TotalSeconds);
                    await Task.Delay(delay, token);
                    continue;
                }

                // 5xx is transient; 4xx (other than 429) will not succeed on retry.
                if ((int)response.StatusCode >= 500)
                {
                    logger.LogWarning(
                        "[Discord] Server error {Status} sending to channel {ChannelId} (attempt {Attempt}/{Max})",
                        (int)response.StatusCode, channelId, attempt, MaxAttempts);
                    await DelayBackoffAsync(attempt, token);
                    continue;
                }

                logger.LogError("[Discord] Failed to send to channel {ChannelId}: HTTP {Status}", channelId,
                    (int)response.StatusCode);
                return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[Discord] Network error sending to channel {ChannelId} (attempt {Attempt}/{Max})",
                    channelId, attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                    await DelayBackoffAsync(attempt, token);
            }
        }

        logger.LogError("[Discord] Giving up sending to channel {ChannelId} after {Max} attempts", channelId,
            MaxAttempts);
        return false;
    }

    /// <summary>
    /// Post a message with a single attached text file (multipart/form-data). Used for the activity-log
    /// report, whose full timeline is delivered as a <c>.txt</c>. Honours the same retry/rate-limit rules
    /// as <see cref="SendMessageAsync" /> and never throws into the caller.
    /// </summary>
    internal async Task<bool> SendMessageWithFileAsync(ulong channelId, DiscordMessage message,
        string fileName, byte[] fileContent, CancellationToken token = default)
    {
        var payloadJson = JsonSerializer.Serialize(message, JsonOptions);

        return await SendWithRetryAsync(channelId, () =>
        {
            // A fresh MultipartFormDataContent per attempt (content is disposed with the request).
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload_json");
            var file = new ByteArrayContent(fileContent);
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(file, "files[0]", fileName);
            return new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/messages")
            {
                Content = form
            };
        }, token);
    }

    /// <summary>
    /// Resolve the guild id that owns a channel (<c>GET /channels/{id}</c> → <c>guild_id</c>).
    /// </summary>
    internal async Task<ulong?> GetGuildIdAsync(ulong channelId, CancellationToken token = default)
    {
        try
        {
            using var response = await http.GetAsync($"channels/{channelId}", token);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (doc.RootElement.TryGetProperty("guild_id", out var guild) &&
                ulong.TryParse(guild.GetString(), out var guildId))
                return guildId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Discord] Failed to resolve guild for channel {ChannelId}", channelId);
        }

        return null;
    }

    /// <summary>
    /// List active (non-archived) threads in a guild (<c>GET /guilds/{id}/threads/active</c>).
    /// </summary>
    internal Task<IReadOnlyList<DiscordThread>> GetActiveThreadsAsync(ulong guildId,
        CancellationToken token = default) =>
        GetThreadsAsync($"guilds/{guildId}/threads/active", token);

    /// <summary>
    /// List archived public threads under a parent channel
    /// (<c>GET /channels/{id}/threads/archived/public</c>).
    /// </summary>
    internal Task<IReadOnlyList<DiscordThread>> GetArchivedThreadsAsync(ulong channelId,
        CancellationToken token = default) =>
        GetThreadsAsync($"channels/{channelId}/threads/archived/public", token);

    /// <summary>
    /// Create a public thread under a channel (<c>POST /channels/{id}/threads</c>, type 11).
    /// Returns the new thread id or null on failure.
    /// </summary>
    internal async Task<ulong?> CreateThreadAsync(ulong channelId, string name,
        CancellationToken token = default)
    {
        try
        {
            var body = new { name, type = 11, auto_archive_duration = 10080 };
            using var content = JsonContent.Create(body, options: JsonOptions);
            using var response = await http.PostAsync($"channels/{channelId}/threads", content, token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("[Discord] Failed to create thread '{Name}' in {ChannelId}: HTTP {Status}",
                    name, channelId, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (doc.RootElement.TryGetProperty("id", out var id) && ulong.TryParse(id.GetString(), out var tid))
                return tid;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Discord] Error creating thread '{Name}' in {ChannelId}", name, channelId);
        }

        return null;
    }

    /// <summary>
    /// Unarchive a thread (<c>PATCH /channels/{threadId}</c> with <c>archived: false</c>), so a reused
    /// archived thread can receive new messages. Needs the Manage Threads permission.
    /// </summary>
    internal async Task<bool> UnarchiveThreadAsync(ulong threadId, CancellationToken token = default)
    {
        try
        {
            using var content = JsonContent.Create(new { archived = false }, options: JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"channels/{threadId}")
            {
                Content = content
            };
            using var response = await http.SendAsync(request, token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Discord] Error unarchiving thread {ThreadId}", threadId);
            return false;
        }
    }

    private async Task<IReadOnlyList<DiscordThread>> GetThreadsAsync(string path, CancellationToken token)
    {
        try
        {
            using var response = await http.GetAsync(path, token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("[Discord] Failed to list threads ({Path}): HTTP {Status}", path,
                    (int)response.StatusCode);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var result = new List<DiscordThread>();
            if (doc.RootElement.TryGetProperty("threads", out var threads) &&
                threads.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in threads.EnumerateArray())
                {
                    if (!t.TryGetProperty("id", out var idEl) || !ulong.TryParse(idEl.GetString(), out var id))
                        continue;
                    var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var archived = t.TryGetProperty("thread_metadata", out var meta) &&
                                   meta.TryGetProperty("archived", out var a) && a.GetBoolean();
                    ulong? parentId = t.TryGetProperty("parent_id", out var p) &&
                                      ulong.TryParse(p.GetString(), out var pid)
                        ? pid
                        : null;
                    result.Add(new DiscordThread(id, name, archived, parentId));
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Discord] Error listing threads ({Path})", path);
            return [];
        }
    }

    /// <summary>
    /// Shared send loop with the same 429 / 5xx / network retry policy as <see cref="SendMessageAsync" />,
    /// driven by a request factory so each attempt gets a fresh (disposable) request/content.
    /// </summary>
    private async Task<bool> SendWithRetryAsync(ulong channelId, Func<HttpRequestMessage> requestFactory,
        CancellationToken token)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = requestFactory();
                using var response = await http.SendAsync(request, token);

                if (response.IsSuccessStatusCode)
                    return true;

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var delay = await GetRetryAfterAsync(response, token);
                    logger.LogWarning("[Discord] Rate limited on channel {ChannelId}; retrying in {Delay}s",
                        channelId, delay.TotalSeconds);
                    await Task.Delay(delay, token);
                    continue;
                }

                if ((int)response.StatusCode >= 500)
                {
                    logger.LogWarning(
                        "[Discord] Server error {Status} sending to channel {ChannelId} (attempt {Attempt}/{Max})",
                        (int)response.StatusCode, channelId, attempt, MaxAttempts);
                    await DelayBackoffAsync(attempt, token);
                    continue;
                }

                logger.LogError("[Discord] Failed to send to channel {ChannelId}: HTTP {Status}", channelId,
                    (int)response.StatusCode);
                return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[Discord] Network error sending to channel {ChannelId} (attempt {Attempt}/{Max})",
                    channelId, attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                    await DelayBackoffAsync(attempt, token);
            }
        }

        logger.LogError("[Discord] Giving up sending to channel {ChannelId} after {Max} attempts", channelId,
            MaxAttempts);
        return false;
    }

    /// <summary>
    /// Check whether the bot can access a channel (<c>GET /channels/{id}</c>). Used at startup to
    /// surface wrong ids or missing permissions.
    /// </summary>
    internal async Task<(bool Ok, string? Error)> CheckChannelAsync(ulong channelId,
        CancellationToken token = default)
    {
        try
        {
            using var response = await http.GetAsync($"channels/{channelId}", token);
            if (response.IsSuccessStatusCode)
                return (true, null);

            var reason = response.StatusCode switch
            {
                HttpStatusCode.NotFound => "channel not found (wrong channel id, or bot not in the server)",
                HttpStatusCode.Forbidden => "missing access (grant the bot View Channel / Send Messages / Embed Links)",
                HttpStatusCode.Unauthorized => "invalid bot token",
                _ => $"HTTP {(int)response.StatusCode}"
            };
            return (false, reason);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<TimeSpan> GetRetryAfterAsync(HttpResponseMessage response, CancellationToken token)
    {
        // Prefer the Retry-After header (seconds); fall back to the JSON body's retry_after.
        if (response.Headers.TryGetValues("Retry-After", out var values) &&
            double.TryParse(values.FirstOrDefault(), out var headerSeconds) && headerSeconds > 0)
            return TimeSpan.FromSeconds(Math.Min(headerSeconds, 30));

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (doc.RootElement.TryGetProperty("retry_after", out var retry) &&
                retry.TryGetDouble(out var bodySeconds) && bodySeconds > 0)
                return TimeSpan.FromSeconds(Math.Min(bodySeconds, 30));
        }
        catch
        {
            // ignore body parse errors; fall through to default
        }

        return TimeSpan.FromSeconds(1);
    }

    private static Task DelayBackoffAsync(int attempt, CancellationToken token) =>
        Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1)), token);
}
