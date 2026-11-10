using Microsoft.Extensions.Time.Testing;
using Sms.Core.Application;
using Sms.Core.Domain;
using Sms.Core.Tests.Fakes;

namespace Sms.Core.Tests.Application;

public class SendMessageTests
{
    private readonly FakeMessageRepository _repository = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(TestData.Start));
    private readonly DispatcherOptions _options = new() { MaxRecipientsPerMessage = 3 };
    private readonly SendMessage _sendMessage;

    public SendMessageTests() => _sendMessage = new SendMessage(_repository, _time, _options);

    [Fact]
    public async Task SendMessage_SavesPendingMessage_AndDoesNotCallProvider()
    {
        // SendMessage has no ISmsProvider dependency at all; sending is the worker's job.
        var dto = await _sendMessage.ExecuteAsync(new(["050-1234567", "+972521112233"], "  Shift starts at 07:00 ", null), default);

        var saved = Assert.Single(_repository.Messages);
        Assert.Equal(saved.Id, dto.Id);
        Assert.Equal(MessageStatus.Pending, dto.Status);
        Assert.Equal("Shift starts at 07:00", dto.Body);
        Assert.Equal(TestData.Start, dto.CreatedAt);
        Assert.Equal(TestData.Start, dto.UpdatedAt);
        Assert.Equal(["+972501234567", "+972521112233"], dto.Recipients.Select(r => r.To));
        Assert.All(dto.Recipients, r =>
        {
            Assert.Equal(RecipientStatus.Pending, r.Status);
            Assert.Equal(0, r.Attempts);
            Assert.Null(r.LastError);
        });
    }

    [Fact]
    public async Task SendMessage_OneInvalidNumber_RejectsWholeRequest()
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            _sendMessage.ExecuteAsync(new(["050-1234567", "03-1234567"], "Hello", null), default));

        Assert.Equal(["to[1]"], error.Errors.Keys);
        Assert.Equal([PhoneNumber.InvalidMessage], error.Errors["to[1]"]);
        Assert.Empty(_repository.Messages);
    }

    [Fact]
    public async Task SendMessage_InvalidNumbersAndBody_ReportsAllErrorsTogether()
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            _sendMessage.ExecuteAsync(new(["bad", "050-1234567", "also bad"], "   ", null), default));

        Assert.Equal(["body", "to[0]", "to[2]"], error.Errors.Keys.Order());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendMessage_NoRecipients_IsRejected(bool toIsNull)
    {
        string[]? to = toIsNull ? null : [];

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            _sendMessage.ExecuteAsync(new(to, "Hello", null), default));

        Assert.Equal(["At least one recipient is required."], error.Errors["to"]);
    }

    [Fact]
    public async Task SendMessage_MoreThanMaxRecipients_IsRejected()
    {
        string[] to = ["0501111111", "0502222222", "0503333333", "0504444444"];

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            _sendMessage.ExecuteAsync(new(to, "Hello", null), default));

        Assert.Equal(["At most 3 recipients are allowed."], error.Errors["to"]);
    }

    [Fact]
    public async Task SendMessage_SameIdempotencyKey_ReturnsOriginalMessage()
    {
        var first = await _sendMessage.ExecuteAsync(new(["0501234567"], "Hello", "key-1"), default);
        _time.Advance(TimeSpan.FromSeconds(5));

        var second = await _sendMessage.ExecuteAsync(new(["0521112233"], "Different", "key-1"), default);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Hello", second.Body);
        Assert.Equal(first.Recipients, second.Recipients);
        Assert.Single(_repository.Messages);
    }

    [Fact]
    public async Task SendMessage_SameIdempotencyKeyInsertedConcurrently_ReturnsThatMessage()
    {
        var other = Message.Create(TestData.Body("From the other request"), [TestData.Phone("0501234567")], "key-1", TestData.Start);
        _repository.InsertedConcurrentlyBeforeNextAdd = other;

        var dto = await _sendMessage.ExecuteAsync(new(["0501234567"], "Hello", "key-1"), default);

        Assert.Equal(other.Id, dto.Id);
        Assert.Single(_repository.Messages);
    }
}
