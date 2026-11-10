using Microsoft.Extensions.Time.Testing;
using Sms.Core.Application;
using Sms.Core.Domain;
using Sms.Core.Sending;
using Sms.Core.Tests.Fakes;

namespace Sms.Core.Tests.Application;

public class DispatchPendingTests
{
    private readonly FakeMessageRepository _repository = new();
    private readonly FakeSmsProvider _provider = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(TestData.Start));
    private readonly DispatcherOptions _options = new();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task DispatchPending_SendsEachRecipientWithItsIdAsReference()
    {
        var message = await AddMessageAsync("0501234567", "0521112233");

        await DispatchAsync();

        var expected = message.Recipients.Select(r => new SmsRequest(r.Id, r.PhoneNumber, message.Body)).OrderBy(r => r.Reference);
        Assert.Equal(expected, _provider.SendRequests.OrderBy(r => r.Reference));
        Assert.All(message.Recipients, r => Assert.Equal(RecipientStatus.Sent, r.Status));
        Assert.Equal(MessageStatus.Sent, message.Status);
    }

    [Fact]
    public async Task DispatchPending_NeverExceedsMaxParallelSends()
    {
        _options.MaxParallelSends = 3;
        _provider.CallDuration = TimeSpan.FromMilliseconds(20);
        var numbers = Enumerable.Range(0, 20).Select(i => $"05000000{i:D2}").ToArray();
        await AddMessageAsync(numbers);

        await DispatchAsync();

        Assert.Equal(20, _provider.SendRequests.Count);
        Assert.InRange(_provider.MaxInFlight, 2, 3);
    }

    [Fact]
    public async Task DispatchPending_LocksBeforeSending_AndSavesResultsAfterwards()
    {
        await AddMessageAsync("0501234567");

        await DispatchAsync();

        // One save for the locks, one for the send results; nothing to check for delivery yet.
        Assert.Equal(2, _repository.SaveChangesCount);
    }

    [Fact]
    public async Task DispatchPending_SkipsRecipientsNotYetDue()
    {
        var message = await AddMessageAsync("0501234567");
        _provider.ScriptSendResults(SendResult.Transient("Timeout"));
        await DispatchAsync();

        _time.Advance(TimeSpan.FromSeconds(1));
        await DispatchAsync();
        Assert.Single(_provider.SendRequests);

        _time.Advance(TimeSpan.FromSeconds(1));
        await DispatchAsync();
        Assert.Equal(2, _provider.SendRequests.Count);
        Assert.Equal(RecipientStatus.Sent, message.Recipients[0].Status);
    }

    [Fact]
    public async Task DispatchPending_TransientFailures_RetriesUntilMaxAttemptsThenFails()
    {
        var message = await AddMessageAsync("0501234567");
        _provider.ScriptSendResults(SendResult.Transient("Timeout"), SendResult.Transient("Timeout"), SendResult.Transient("Timeout"));

        await DispatchAsync();
        _time.Advance(TimeSpan.FromSeconds(2));
        await DispatchAsync();
        _time.Advance(TimeSpan.FromSeconds(4));
        await DispatchAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await DispatchAsync();

        Assert.Equal(3, _provider.SendRequests.Count);
        Assert.Equal(RecipientStatus.Failed, message.Recipients[0].Status);
        Assert.Equal(MessageStatus.Failed, message.Status);
    }

    [Fact]
    public async Task DispatchPending_SkipsLockedRecipients()
    {
        var message = await AddMessageAsync("0501234567");
        message.LockRecipient(message.Recipients[0].Id, Now.AddSeconds(_options.LockSeconds), Now);

        _time.Advance(TimeSpan.FromSeconds(_options.LockSeconds - 1));
        await DispatchAsync();

        Assert.Empty(_provider.SendRequests);
        Assert.Equal(RecipientStatus.Pending, message.Recipients[0].Status);
    }

    [Fact]
    public async Task DispatchPending_ExpiredLock_IsPickedUpAgain()
    {
        // A previous worker locked the recipient and crashed before saving a result.
        var message = await AddMessageAsync("0501234567");
        message.LockRecipient(message.Recipients[0].Id, Now.AddSeconds(_options.LockSeconds), Now);

        _time.Advance(TimeSpan.FromSeconds(_options.LockSeconds));
        await DispatchAsync();

        var request = Assert.Single(_provider.SendRequests);
        Assert.Equal(message.Recipients[0].Id, request.Reference);
        Assert.Equal(RecipientStatus.Sent, message.Recipients[0].Status);
    }

    [Fact]
    public async Task DispatchPending_SentRecipient_IsCheckedUntilDelivered()
    {
        var message = await AddMessageAsync("0501234567");
        _provider.ScriptDeliveryStatuses(DeliveryStatus.Unknown, DeliveryStatus.Delivered);
        await DispatchAsync();
        Assert.Empty(_provider.DeliveryChecks);

        _time.Advance(TimeSpan.FromSeconds(_options.DeliveryCheckSeconds));
        await DispatchAsync();
        Assert.Equal(RecipientStatus.Sent, message.Recipients[0].Status);

        _time.Advance(TimeSpan.FromSeconds(_options.DeliveryCheckSeconds));
        await DispatchAsync();
        _time.Advance(TimeSpan.FromSeconds(_options.DeliveryCheckSeconds));
        await DispatchAsync();

        Assert.Equal(2, _provider.DeliveryChecks.Count);
        Assert.All(_provider.DeliveryChecks, id => Assert.Equal(message.Recipients[0].ProviderMessageId, id));
        Assert.Equal(RecipientStatus.Delivered, message.Recipients[0].Status);
        Assert.Equal(MessageStatus.Delivered, message.Status);
    }

    [Fact]
    public async Task DispatchPending_NotDelivered_MarksRecipientFailed()
    {
        var message = await AddMessageAsync("0501234567");
        _provider.ScriptDeliveryStatuses(DeliveryStatus.NotDelivered);
        await DispatchAsync();

        _time.Advance(TimeSpan.FromSeconds(_options.DeliveryCheckSeconds));
        await DispatchAsync();

        Assert.Equal(RecipientStatus.Failed, message.Recipients[0].Status);
        Assert.Equal("Not delivered", message.Recipients[0].LastError);
    }

    [Fact]
    public async Task DispatchPending_SentOlderThan24h_IsNoLongerChecked()
    {
        var message = await AddMessageAsync("0501234567");
        await DispatchAsync();

        _time.Advance(TimeSpan.FromHours(_options.DeliveryTrackingHours));
        await DispatchAsync();

        Assert.Empty(_provider.DeliveryChecks);
        Assert.Equal(RecipientStatus.Sent, message.Recipients[0].Status);
    }

    private async Task<Message> AddMessageAsync(params string[] numbers)
    {
        var message = TestData.NewMessage(Now, numbers);
        await _repository.AddAsync(message, default);
        return message;
    }

    private Task DispatchAsync() => new DispatchPending(_repository, _provider, _time, _options).ExecuteAsync(default);
}
