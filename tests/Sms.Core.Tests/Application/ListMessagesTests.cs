using Sms.Core.Application;
using Sms.Core.Domain;
using Sms.Core.Sending;
using Sms.Core.Tests.Fakes;

namespace Sms.Core.Tests.Application;

public class ListMessagesTests
{
    private readonly FakeMessageRepository _repository = new();
    private readonly ListMessages _listMessages;

    public ListMessagesTests() => _listMessages = new ListMessages(_repository);

    [Fact]
    public async Task ListMessages_NoFilter_ReturnsNewestFirstWithCounts()
    {
        var older = await AddMessageAsync(minutesAfterStart: 0);
        var newer = await AddMessageAsync(minutesAfterStart: 1);

        var page = await _listMessages.ExecuteAsync(new(null, null, null), default);

        Assert.Equal([newer.Id, older.Id], page.Items.Select(i => i.Id));
        Assert.Null(page.NextCursor);
        Assert.Equal(1, page.Items[0].RecipientCount);
    }

    [Fact]
    public async Task ListMessages_FilterByStatus_IsCaseInsensitive()
    {
        var sent = await AddMessageAsync(minutesAfterStart: 0);
        sent.RecordSendResult(sent.Recipients[0].Id, SendResult.Accepted("p1"), TestData.Start, TestData.Policy, TestData.DeliveryCheckDelay);
        await AddMessageAsync(minutesAfterStart: 1);

        var page = await _listMessages.ExecuteAsync(new("sEnT", null, null), default);

        Assert.Equal([sent.Id], page.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task ListMessages_MoreThanLimit_ReturnsCursorToNextPage()
    {
        var messages = new List<Message>();
        for (var minute = 0; minute < 5; minute++)
            messages.Add(await AddMessageAsync(minute));

        var first = await _listMessages.ExecuteAsync(new(null, null, 2), default);
        var second = await _listMessages.ExecuteAsync(new(null, first.NextCursor, 2), default);
        var third = await _listMessages.ExecuteAsync(new(null, second.NextCursor, 2), default);

        var expectedOrder = messages.Select(m => m.Id).Reverse().ToList();
        Assert.Equal(expectedOrder, first.Items.Concat(second.Items).Concat(third.Items).Select(i => i.Id));
        Assert.NotNull(first.NextCursor);
        Assert.NotNull(second.NextCursor);
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task ListMessages_ExactlyLimitItems_HasNoNextCursor()
    {
        await AddMessageAsync(0);
        await AddMessageAsync(1);

        var page = await _listMessages.ExecuteAsync(new(null, null, 2), default);

        Assert.Equal(2, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("1")]
    [InlineData("Sent,Failed")]
    public async Task ListMessages_UnknownStatus_IsRejected(string status)
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            _listMessages.ExecuteAsync(new(status, null, null), default));

        Assert.Contains("status", error.Errors.Keys);
    }

    [Fact]
    public async Task ListMessages_InvalidCursor_IsRejected()
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            _listMessages.ExecuteAsync(new(null, "garbage!", null), default));

        Assert.Contains("cursor", error.Errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task ListMessages_LimitOutOfRange_IsRejected(int limit)
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            _listMessages.ExecuteAsync(new(null, null, limit), default));

        Assert.Contains("limit", error.Errors.Keys);
    }

    [Fact]
    public async Task ListMessages_DefaultLimit_Is20()
    {
        for (var minute = 0; minute < 25; minute++)
            await AddMessageAsync(minute);

        var page = await _listMessages.ExecuteAsync(new(null, null, null), default);

        Assert.Equal(ListMessages.DefaultLimit, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    private async Task<Message> AddMessageAsync(int minutesAfterStart)
    {
        var message = TestData.NewMessage(TestData.Start.AddMinutes(minutesAfterStart), "0501234567");
        await _repository.AddAsync(message, default);
        return message;
    }
}
