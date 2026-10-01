using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GZCTF.Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GZCTF.Test.UnitTests.Discord;

public class DiscordApiClientTests
{
    private static DiscordApiClient CreateClient(QueueHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://discord.com/api/v10/") };
        return new DiscordApiClient(http, NullLogger<DiscordApiClient>.Instance);
    }

    private static DiscordMessage SampleMessage() =>
        new() { Embeds = [new DiscordEmbed { Title = "t" }] };

    [Fact]
    public async Task SendMessage_Success_ReturnsTrue()
    {
        var handler = new QueueHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var client = CreateClient(handler);
        var ok = await client.SendMessageAsync(123, SampleMessage());

        Assert.True(ok);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendMessage_RateLimited_HonoursRetryAfterThenSucceeds()
    {
        var handler = new QueueHandler();
        handler.Enqueue(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.TryAddWithoutValidation("Retry-After", "0.01");
            return resp;
        });
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var client = CreateClient(handler);
        var ok = await client.SendMessageAsync(123, SampleMessage());

        Assert.True(ok);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task SendMessage_ServerErrors_RetriesThenReturnsFalse()
    {
        var handler = new QueueHandler();
        handler.DefaultResponse = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var client = CreateClient(handler);
        var ok = await client.SendMessageAsync(123, SampleMessage());

        Assert.False(ok);
        Assert.True(handler.CallCount > 1, "should have retried on 5xx");
    }

    [Fact]
    public async Task SendMessage_NetworkException_DoesNotThrowAndReturnsFalse()
    {
        var handler = new QueueHandler { DefaultResponse = _ => throw new HttpRequestException("boom") };

        var client = CreateClient(handler);
        var ok = await client.SendMessageAsync(123, SampleMessage());

        Assert.False(ok);
        Assert.True(handler.CallCount > 1, "should have retried on network error");
    }

    [Fact]
    public async Task SendMessage_ClientError_ReturnsFalseWithoutRetry()
    {
        var handler = new QueueHandler { DefaultResponse = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) };

        var client = CreateClient(handler);
        var ok = await client.SendMessageAsync(123, SampleMessage());

        Assert.False(ok);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendMessage_SendsEmptyAllowedMentions()
    {
        string? capturedBody = null;
        var handler = new QueueHandler
        {
            DefaultResponse = req =>
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        var client = CreateClient(handler);
        await client.SendMessageAsync(123, SampleMessage());

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.TryGetProperty("allowed_mentions", out var am));
        Assert.True(am.TryGetProperty("parse", out var parse));
        Assert.Equal(JsonValueKind.Array, parse.ValueKind);
        Assert.Equal(0, parse.GetArrayLength());
    }

    [Fact]
    public async Task CheckChannel_NotFound_ReportsReason()
    {
        var handler = new QueueHandler { DefaultResponse = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var client = CreateClient(handler);

        var (ok, error) = await client.CheckChannelAsync(123);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task CheckChannel_Success_ReturnsOk()
    {
        var handler = new QueueHandler { DefaultResponse = _ => new HttpResponseMessage(HttpStatusCode.OK) };
        var client = CreateClient(handler);

        var (ok, error) = await client.CheckChannelAsync(123);

        Assert.True(ok);
        Assert.Null(error);
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

        public int CallCount { get; private set; }

        public Func<HttpRequestMessage, HttpResponseMessage>? DefaultResponse { get; set; }

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) => _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var factory = _responses.Count > 0 ? _responses.Dequeue() : DefaultResponse;
            if (factory is null)
                throw new InvalidOperationException("No response configured");

            return Task.FromResult(factory(request));
        }
    }
}
