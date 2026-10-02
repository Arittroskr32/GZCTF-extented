using System.Text;
using System.Threading.Channels;
using GZCTF.Discord;
using GZCTF.Discord.ActivityLog;
using GZCTF.Middlewares;
using Microsoft.AspNetCore.Mvc;

namespace GZCTF.Controllers;

/// <summary>
/// Admin/monitor APIs for the container activity log. All endpoints require the Monitor role. When the
/// activity-log feature is disabled (no <c>activity_log</c> section in <c>discord.yml</c>), the endpoints
/// return 400 rather than failing, since the backing services are not registered.
/// </summary>
[ApiController]
[Route("api/Game/{gameId:int}/[controller]")]
[RequireMonitor]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status403Forbidden)]
public class ActivityLogController(IServiceProvider serviceProvider) : ControllerBase
{
    /// <summary>
    /// Trigger generation of the activity report for a game (or a single challenge), posted to the private
    /// activity channel on Discord.
    /// </summary>
    /// <remarks>Requires Monitor permission.</remarks>
    /// <param name="gameId">Game id</param>
    /// <param name="challengeId">Optional challenge id; omit to report every challenge with activity</param>
    /// <param name="token"></param>
    /// <response code="200">Report generation was queued</response>
    /// <response code="400">Activity log is disabled</response>
    [HttpPost("Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateReport([FromRoute] int gameId, [FromQuery] int? challengeId,
        CancellationToken token)
    {
        var writer = serviceProvider.GetService<ChannelWriter<ReportRequest>>();
        if (writer is null)
            return Disabled();

        await writer.WriteAsync(new ReportRequest(gameId, challengeId), token);
        return Ok(new RequestResponse("Activity report generation queued", StatusCodes.Status200OK));
    }

    /// <summary>
    /// Return the stored activity timeline for a game (or a single challenge) as JSON or CSV, for review
    /// without Discord.
    /// </summary>
    /// <remarks>Requires Monitor permission.</remarks>
    /// <param name="gameId">Game id</param>
    /// <param name="challengeId">Optional challenge id</param>
    /// <param name="format">"json" (default) or "csv"</param>
    /// <param name="token"></param>
    /// <response code="200">The timeline</response>
    /// <response code="400">Activity log is disabled</response>
    [HttpGet]
    [ProducesResponseType(typeof(ActivityLogEntryModel[]), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetTimeline([FromRoute] int gameId, [FromQuery] int? challengeId,
        [FromQuery] string? format, CancellationToken token)
    {
        var repository = serviceProvider.GetService<IActivityLogRepository>();
        var config = serviceProvider.GetService<DiscordConfig>();
        if (repository is null || config is null)
            return Disabled();

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var reports = serviceProvider.GetRequiredService<ActivityReportService>();
            var csv = await reports.BuildCsvAsync(gameId, challengeId, token);
            return File(Encoding.UTF8.GetBytes(csv), "text/csv",
                $"activity-game-{gameId}{(challengeId is null ? "" : $"-challenge-{challengeId}")}.csv");
        }

        var entries = await repository.GetTimelineAsync(gameId, challengeId, token);
        return Ok(entries.Select(e => ActivityLogEntryModel.FromEntry(e, config.ShowSubmittedFlag)).ToArray());
    }

    private IActionResult Disabled() =>
        BadRequest(new RequestResponse("Activity log is not enabled", StatusCodes.Status400BadRequest));
}
