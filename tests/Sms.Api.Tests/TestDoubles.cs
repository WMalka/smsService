using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Sms.Core.Sending;

namespace Sms.Api.Tests;

internal sealed class FakeSmsProvider : ISmsProvider
{
    public ConcurrentBag<SmsRequest> Sent { get; } = [];

    public Task<SendResult> SendAsync(SmsRequest request, CancellationToken cancellationToken)
    {
        Sent.Add(request);
        return Task.FromResult(SendResult.Accepted($"provider-{request.Reference}"));
    }

    public Task<DeliveryStatus> GetDeliveryStatusAsync(string providerMessageId, CancellationToken cancellationToken) =>
        Task.FromResult(DeliveryStatus.Delivered);
}

/// <summary>Answers every HTTP call with <paramref name="respond"/> and records the requests and their bodies.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public static StubHttpHandler Returning(HttpStatusCode status, string json = "{}") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return await respond(request, cancellationToken);
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Lines);

    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{formatter(state, exception)} {exception}");
    }
}
