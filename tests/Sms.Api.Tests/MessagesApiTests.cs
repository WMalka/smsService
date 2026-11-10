using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sms.Core.Application;
using Sms.Core.Domain;
using Sms.Infrastructure.Persistence;

namespace Sms.Api.Tests;

public sealed class MessagesApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly SmsApiFactory _factory = new();
    private readonly HttpClient _client;

    public MessagesApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task PostMessages_Valid_Returns202WithLocation()
    {
        var response = await PostAsync(["050-1234567", "+972521112233"], "Shift starts at 07:00");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = await ReadAsync<MessageDto>(response);
        Assert.Equal($"/messages/{message.Id}", response.Headers.Location?.ToString());
        Assert.Equal(MessageStatus.Pending, message.Status);
        Assert.Equal(["+972501234567", "+972521112233"], message.Recipients.Select(r => r.To));
        Assert.Empty(_factory.Provider.Sent);
    }

    [Fact]
    public async Task PostMessages_RepeatedIdempotencyKey_ReturnsSameResponse()
    {
        var first = await PostAsync(["050-1234567"], "Hello", idempotencyKey: "key-1");
        var second = await PostAsync(["050-1234567"], "Hello", idempotencyKey: "key-1");

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Single((await ListAsync("")).Items);
    }

    [Fact]
    public async Task PostMessages_InvalidSecondNumber_Returns400WithIndexedError()
    {
        var response = await PostAsync(["050-1234567", "03-1234567"], "Hello");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync<ValidationProblemDetails>(response);
        Assert.Equal([PhoneNumber.InvalidMessage], problem.Errors["to[1]"]);
        Assert.Empty((await ListAsync("")).Items);
    }

    [Fact]
    public async Task PostMessages_SqlInjectionText_IsStoredVerbatim_AndTablesAreIntact()
    {
        const string body = """Robert'); DROP TABLE Messages;-- bring the "OR 1=1" report""";

        var posted = await ReadAsync<MessageDto>(await PostAsync(["050-1234567"], body));
        var stored = await ReadAsync<MessageDto>(await _client.GetAsync($"/messages/{posted.Id}"));

        Assert.Equal(body, stored.Body);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmsDbContext>();
        Assert.Equal(1, await db.Messages.CountAsync());
        Assert.Equal(1, await db.MessageRecipients.CountAsync());
        Assert.True(await db.Employees.AnyAsync(), "the seeded Employees table should still have rows");
    }

    [Fact]
    public async Task GetMessage_UnknownId_Returns404()
    {
        var response = await _client.GetAsync($"/messages/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ListMessages_FilterByStatus_ReturnsOnlyThatStatus()
    {
        var sent = await ReadAsync<MessageDto>(await PostAsync(["050-1234567"], "First"));
        await DispatchOnceAsync();
        var pending = await ReadAsync<MessageDto>(await PostAsync(["050-1234567"], "Second"));

        Assert.Equal([sent.Id], (await ListAsync("?status=sent")).Items.Select(i => i.Id));
        Assert.Equal([pending.Id], (await ListAsync("?status=Pending")).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task ListMessages_UnknownStatus_Returns400()
    {
        var response = await _client.GetAsync("/messages?status=Sending");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("status", (await ReadAsync<ValidationProblemDetails>(response)).Errors.Keys);
    }

    [Fact]
    public async Task ListMessages_NewMessageArrivesBetweenPages_NoDuplicatesOrGaps()
    {
        var existing = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            existing.Add((await ReadAsync<MessageDto>(await PostAsync(["050-1234567"], $"Message {i}"))).Id);
            // Pairs share a CreatedAt, so the Id tie-breaker is exercised too.
            if (i % 2 == 1)
                _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var seen = new List<Guid>();
        var page = await ListAsync("?limit=2");
        seen.AddRange(page.Items.Select(i => i.Id));
        _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        await PostAsync(["050-1234567"], "Arrived while paging");
        while (page.NextCursor is not null)
        {
            page = await ListAsync($"?limit=2&cursor={Uri.EscapeDataString(page.NextCursor)}");
            seen.AddRange(page.Items.Select(i => i.Id));
        }

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(existing.OrderBy(id => id), seen.OrderBy(id => id));
    }

    [Fact]
    public async Task Health_DatabaseReachable_Returns200()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    private Task<HttpResponseMessage> PostAsync(string[] to, string body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/messages") { Content = JsonContent.Create(new { to, body }) };
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _client.SendAsync(request);
    }

    private async Task<MessagePage> ListAsync(string query) =>
        await ReadAsync<MessagePage>(await _client.GetAsync($"/messages{query}"));

    private async Task DispatchOnceAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DispatchPending>().ExecuteAsync(CancellationToken.None);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;
}
