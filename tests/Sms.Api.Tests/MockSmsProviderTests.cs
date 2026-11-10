using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sms.Core.Sending;
using Sms.Infrastructure.Sending;

namespace Sms.Api.Tests;

public sealed class MockSmsProviderTests
{
    private static readonly SmsRequest Request = new(Guid.NewGuid(), "+972501234567", "Shift starts at 07:00");

    private static MockSmsProvider CreateProvider(StubHttpHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://provider.test"), Timeout = timeout ?? TimeSpan.FromSeconds(10) },
            Options.Create(new SmsProviderOptions()),
            NullLogger<MockSmsProvider>.Instance);

    [Fact]
    public async Task MockSmsProvider_MapsOurRequestToProviderJson()
    {
        var handler = StubHttpHandler.Returning(HttpStatusCode.OK, """{"providerId":"p-1"}""");

        var result = await CreateProvider(handler).SendAsync(Request, CancellationToken.None);

        Assert.Equal(SendResult.Accepted("p-1"), result);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/send", request.RequestUri!.AbsolutePath);
        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal(Request.To, json.GetProperty("to").GetString());
        Assert.Equal(Request.Body, json.GetProperty("body").GetString());
        Assert.Equal(Request.Reference.ToString(), json.GetProperty("reference").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task MockSmsProvider_4xx_ReturnsPermanentFailure(HttpStatusCode status)
    {
        var result = await CreateProvider(StubHttpHandler.Returning(status)).SendAsync(Request, CancellationToken.None);

        Assert.Equal(SendOutcome.PermanentFailure, result.Outcome);
    }

    [Fact]
    public async Task MockSmsProvider_5xx_ReturnsTransientFailure()
    {
        var result = await CreateProvider(StubHttpHandler.Returning(HttpStatusCode.ServiceUnavailable))
            .SendAsync(Request, CancellationToken.None);

        Assert.Equal(SendOutcome.TransientFailure, result.Outcome);
    }

    [Fact]
    public async Task MockSmsProvider_Timeout_ReturnsTransientFailure()
    {
        var hanging = new StubHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });

        var result = await CreateProvider(hanging, TimeSpan.FromMilliseconds(50)).SendAsync(Request, CancellationToken.None);

        Assert.Equal(SendOutcome.TransientFailure, result.Outcome);
    }

    [Fact]
    public async Task MockSmsProvider_Unreachable_ReturnsTransientFailure()
    {
        var unreachable = new StubHttpHandler((_, _) => throw new HttpRequestException("connection refused"));

        var result = await CreateProvider(unreachable).SendAsync(Request, CancellationToken.None);

        Assert.Equal(SendOutcome.TransientFailure, result.Outcome);
    }

    [Fact]
    public async Task MockSmsProvider_CallerCancels_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvider(StubHttpHandler.Returning(HttpStatusCode.OK)).SendAsync(Request, cancelled.Token));
    }

    [Theory]
    [InlineData("delivered", DeliveryStatus.Delivered)]
    [InlineData("failed", DeliveryStatus.NotDelivered)]
    [InlineData("pending", DeliveryStatus.Unknown)]
    public async Task MockSmsProvider_Status_IsMapped(string providerStatus, DeliveryStatus expected)
    {
        var handler = StubHttpHandler.Returning(HttpStatusCode.OK, $$"""{"status":"{{providerStatus}}"}""");

        var status = await CreateProvider(handler).GetDeliveryStatusAsync("p-1", CancellationToken.None);

        Assert.Equal(expected, status);
        Assert.Equal("/status/p-1", Assert.Single(handler.Requests).Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task MockSmsProvider_StatusCheckFails_ReturnsUnknown()
    {
        var status = await CreateProvider(StubHttpHandler.Returning(HttpStatusCode.InternalServerError))
            .GetDeliveryStatusAsync("p-1", CancellationToken.None);

        Assert.Equal(DeliveryStatus.Unknown, status);
    }
}
