using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GZCTF.Discord;

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
