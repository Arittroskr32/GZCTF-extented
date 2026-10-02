using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GZCTF.Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GZCTF.Test.UnitTests.Discord.ActivityLog;

public class ActivityFileUploadTests
{
    private static DiscordApiClient Client(CaptureHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://discord.com/api/v10/") };
        return new DiscordApiClient(http, NullLogger<DiscordApiClient>.Instance);
    }

    [Fact]
    public async Task SendMessageWithFile_SendsMultipart_WithPayloadJsonAndFile_AndEmptyAllowedMentions()
    {
        var handler = new CaptureHandler { Response = _ => new HttpResponseMessage(HttpStatusCode.OK) };
        var client = Client(handler);

        var message = new DiscordMessage { Embeds = [new DiscordEmbed { Title = "Report" }] };
        var ok = await client.SendMessageWithFileAsync(123, message, "timeline.txt",
            Encoding.UTF8.GetBytes("hello timeline"));

        Assert.True(ok);
        Assert.NotNull(handler.ContentType);
        Assert.Contains("multipart/form-data", handler.ContentType!);
        Assert.Contains("payload_json", handler.Body!);
        Assert.Contains("timeline.txt", handler.Body!);
        Assert.Contains("hello timeline", handler.Body!);

        // allowed_mentions must still be an empty parse list in the JSON part.
        var jsonStart = handler.Body!.IndexOf("{\"content\"", StringComparison.Ordinal);
        if (jsonStart < 0)
            jsonStart = handler.Body!.IndexOf("{\"embeds\"", StringComparison.Ordinal);
        Assert.True(jsonStart >= 0, "payload_json object not found");
        Assert.Contains("\"allowed_mentions\":{\"parse\":[]}", handler.Body!.Replace(" ", ""));
    }

    [Fact]
    public async Task SendMessageWithFile_RetriesOnServerError_ThenReturnsFalse()
    {
        var handler = new CaptureHandler { Response = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) };
        var client = Client(handler);

        var ok = await client.SendMessageWithFileAsync(123, new DiscordMessage(), "t.txt", [1, 2, 3]);

        Assert.False(ok);
        Assert.True(handler.CallCount > 1, "should retry on 5xx");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Response { get; set; }
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (request.Content is not null)
            {
                ContentType = request.Content.Headers.ContentType?.ToString();
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return Response!(request);
        }
    }
}
