using Sms.Core.Domain;
using Sms.Core.Sending;
using static Sms.Core.Tests.TestData;

namespace Sms.Core.Tests.Domain;

public class MessageTests
{
    private readonly DateTime _now = Start;

    [Fact]
    public void NewMessage_DuplicateNumbers_CreatesOneRecipientPerNumber()
    {
        var message = NewMessage(_now, "050-1234567", "+972501234567", "0521112233");

        Assert.Equal(["+972501234567", "+972521112233"], message.Recipients.Select(r => r.PhoneNumber));
    }

    [Fact]
    public void NewMessage_AllRecipientsPending_StatusIsPending()
    {
        var message = Message.Create(Body(), [Phone("0501234567"), Phone("0521112233")], "key-1", _now);

        Assert.Equal(MessageStatus.Pending, message.Status);
        Assert.NotEqual(Guid.Empty, message.Id);
        Assert.Equal("key-1", message.IdempotencyKey);
        Assert.Equal("Shift starts at 07:00", message.Body);
        Assert.Equal(0, message.Version);
        Assert.Equal(_now, message.CreatedAt);
        Assert.Equal(_now, message.UpdatedAt);
        Assert.All(message.Recipients, r =>
        {
            Assert.Equal(RecipientStatus.Pending, r.Status);
            Assert.Equal(message.Id, r.MessageId);
            Assert.Equal(_now, r.NextActionAt);
            Assert.Equal(0, r.Attempts);
        });
    }

    [Fact]
    public void RecordSendResult_AllAccepted_MessageIsSent()
    {
        var message = NewMessage(_now, "0501234567", "0521112233");
        var later = _now.AddSeconds(1);

        foreach (var recipient in message.Recipients)
            message.RecordSendResult(recipient.Id, SendResult.Accepted("p-" + recipient.PhoneNumber), later, Policy, DeliveryCheckDelay);

        Assert.Equal(MessageStatus.Sent, message.Status);
        var first = message.Recipients[0];
        Assert.Equal(RecipientStatus.Sent, first.Status);
        Assert.Equal("p-+972501234567", first.ProviderMessageId);
        Assert.Equal(later, first.SentAt);
        Assert.Equal(later + DeliveryCheckDelay, first.NextActionAt);
        Assert.Equal(1, first.Attempts);
        Assert.Null(first.LastError);
        Assert.Equal(later, message.UpdatedAt);
        Assert.Equal(2, message.Version);
    }

    [Fact]
    public void RecordSendResult_SomeAcceptedSomeFailed_MessageIsPartiallySent()
    {
        var message = NewMessage(_now, "0501234567", "0521112233");

        message.RecordSendResult(message.Recipients[0].Id, SendResult.Accepted("p1"), _now, Policy, DeliveryCheckDelay);
        message.RecordSendResult(message.Recipients[1].Id, SendResult.Permanent("Rejected"), _now, Policy, DeliveryCheckDelay);

        Assert.Equal(MessageStatus.PartiallySent, message.Status);
        Assert.Equal(RecipientStatus.Failed, message.Recipients[1].Status);
        Assert.Equal("Rejected", message.Recipients[1].LastError);
    }

    [Fact]
    public void RecordSendResult_OneStillPending_MessageIsPending()
    {
        var message = NewMessage(_now, "0501234567", "0521112233");

        message.RecordSendResult(message.Recipients[0].Id, SendResult.Accepted("p1"), _now, Policy, DeliveryCheckDelay);

        Assert.Equal(MessageStatus.Pending, message.Status);
    }

    [Fact]
    public void RecordSendResult_AllPermanentFailures_MessageIsFailed()
    {
        var message = NewMessage(_now, "0501234567");

        message.RecordSendResult(message.Recipients[0].Id, SendResult.Permanent("Rejected"), _now, Policy, DeliveryCheckDelay);

        Assert.Equal(MessageStatus.Failed, message.Status);
    }

    [Fact]
    public void RecordSendResult_OnSentRecipient_Throws()
    {
        var message = NewMessage(_now, "0501234567");
        var id = message.Recipients[0].Id;
        message.RecordSendResult(id, SendResult.Accepted("p1"), _now, Policy, DeliveryCheckDelay);

        Assert.Throws<InvalidStatusTransitionException>(() =>
            message.RecordSendResult(id, SendResult.Accepted("p2"), _now, Policy, DeliveryCheckDelay));
    }

    [Fact]
    public void RecordSendResult_UnknownRecipient_Throws()
    {
        var message = NewMessage(_now, "0501234567");

        Assert.Throws<InvalidStatusTransitionException>(() =>
            message.RecordSendResult(Guid.NewGuid(), SendResult.Accepted("p1"), _now, Policy, DeliveryCheckDelay));
    }

    [Fact]
    public void RecordSendResult_TransientFailure_SchedulesExponentialRetry()
    {
        var message = NewMessage(_now, "0501234567");
        var recipient = message.Recipients[0];

        message.RecordSendResult(recipient.Id, SendResult.Transient("Timeout"), _now, Policy, DeliveryCheckDelay);
        Assert.Equal(_now.AddSeconds(2), recipient.NextActionAt);

        var second = _now.AddSeconds(2);
        message.RecordSendResult(recipient.Id, SendResult.Transient("Timeout"), second, Policy, DeliveryCheckDelay);
        Assert.Equal(second.AddSeconds(4), recipient.NextActionAt);

        Assert.Equal(RecipientStatus.Pending, recipient.Status);
        Assert.Equal(2, recipient.Attempts);
        Assert.Equal("Timeout", recipient.LastError);
        Assert.Equal(MessageStatus.Pending, message.Status);
    }

    [Fact]
    public void RecordSendResult_ThirdTransientFailure_MarksRecipientFailed()
    {
        var message = NewMessage(_now, "0501234567");
        var recipient = message.Recipients[0];

        for (var attempt = 0; attempt < 3; attempt++)
            message.RecordSendResult(recipient.Id, SendResult.Transient("Provider unavailable"), _now, Policy, DeliveryCheckDelay);

        Assert.Equal(RecipientStatus.Failed, recipient.Status);
        Assert.Equal(3, recipient.Attempts);
        Assert.Equal("Provider unavailable", recipient.LastError);
        Assert.Equal(MessageStatus.Failed, message.Status);
    }

    [Fact]
    public void RecordSendResult_ClearsLock()
    {
        var message = NewMessage(_now, "0501234567");
        var recipient = message.Recipients[0];
        message.LockRecipient(recipient.Id, _now.AddSeconds(60), _now);

        message.RecordSendResult(recipient.Id, SendResult.Transient("Timeout"), _now, Policy, DeliveryCheckDelay);

        Assert.Null(recipient.LockedUntil);
    }

    [Fact]
    public void LockRecipient_SetsLockedUntil_AndBumpsVersion()
    {
        var message = NewMessage(_now, "0501234567");
        var recipient = message.Recipients[0];
        var later = _now.AddSeconds(1);

        message.LockRecipient(recipient.Id, later.AddSeconds(60), later);

        Assert.Equal(later.AddSeconds(60), recipient.LockedUntil);
        Assert.Equal(1, message.Version);
        Assert.Equal(later, message.UpdatedAt);
    }

    [Fact]
    public void LockRecipient_OnSentRecipient_Throws()
    {
        var message = SentMessage();

        Assert.Throws<InvalidStatusTransitionException>(() =>
            message.LockRecipient(message.Recipients[0].Id, _now.AddSeconds(60), _now));
    }

    [Fact]
    public void RecordDeliveryStatus_Delivered_AllRecipients_MessageIsDelivered()
    {
        var message = SentMessage("0501234567", "0521112233");

        foreach (var recipient in message.Recipients)
            message.RecordDeliveryStatus(recipient.Id, DeliveryStatus.Delivered, _now, DeliveryCheckDelay);

        Assert.Equal(MessageStatus.Delivered, message.Status);
        Assert.All(message.Recipients, r => Assert.Equal(RecipientStatus.Delivered, r.Status));
    }

    [Fact]
    public void RecordDeliveryStatus_SomeDelivered_MessageIsStillSent()
    {
        var message = SentMessage("0501234567", "0521112233");

        message.RecordDeliveryStatus(message.Recipients[0].Id, DeliveryStatus.Delivered, _now, DeliveryCheckDelay);

        Assert.Equal(MessageStatus.Sent, message.Status);
    }

    [Fact]
    public void RecordDeliveryStatus_NotDelivered_MarksRecipientFailed()
    {
        var message = SentMessage("0501234567", "0521112233");

        message.RecordDeliveryStatus(message.Recipients[0].Id, DeliveryStatus.NotDelivered, _now, DeliveryCheckDelay);

        Assert.Equal(RecipientStatus.Failed, message.Recipients[0].Status);
        Assert.Equal("Not delivered", message.Recipients[0].LastError);
        Assert.Equal(MessageStatus.PartiallySent, message.Status);
    }

    [Fact]
    public void RecordDeliveryStatus_Unknown_StaysSentAndSchedulesNextCheck()
    {
        var message = SentMessage();
        var recipient = message.Recipients[0];
        var later = _now.AddSeconds(5);

        message.RecordDeliveryStatus(recipient.Id, DeliveryStatus.Unknown, later, DeliveryCheckDelay);

        Assert.Equal(RecipientStatus.Sent, recipient.Status);
        Assert.Equal(later + DeliveryCheckDelay, recipient.NextActionAt);
        Assert.Equal(later, message.UpdatedAt);
    }

    [Fact]
    public void RecordDeliveryStatus_Unknown_LongAfterSending_WaitsHalfTheTimeSinceSent()
    {
        var message = SentMessage();
        var recipient = message.Recipients[0];
        var later = _now.AddMinutes(20);

        message.RecordDeliveryStatus(recipient.Id, DeliveryStatus.Unknown, later, DeliveryCheckDelay);

        Assert.Equal(later.AddMinutes(10), recipient.NextActionAt);
    }

    [Fact]
    public void RecordDeliveryStatus_Unknown_HoursAfterSending_WaitsAtMostOneHour()
    {
        var message = SentMessage();
        var recipient = message.Recipients[0];
        var later = _now.AddHours(10);

        message.RecordDeliveryStatus(recipient.Id, DeliveryStatus.Unknown, later, DeliveryCheckDelay);

        Assert.Equal(later.AddHours(1), recipient.NextActionAt);
    }

    [Fact]
    public void RecordDeliveryStatus_OnPendingRecipient_Throws()
    {
        var message = NewMessage(_now, "0501234567");

        Assert.Throws<InvalidStatusTransitionException>(() =>
            message.RecordDeliveryStatus(message.Recipients[0].Id, DeliveryStatus.Delivered, _now, DeliveryCheckDelay));
    }

    [Fact]
    public void RecordDeliveryStatus_OnDeliveredRecipient_Throws()
    {
        var message = SentMessage();
        var id = message.Recipients[0].Id;
        message.RecordDeliveryStatus(id, DeliveryStatus.Delivered, _now, DeliveryCheckDelay);

        Assert.Throws<InvalidStatusTransitionException>(() =>
            message.RecordDeliveryStatus(id, DeliveryStatus.Delivered, _now, DeliveryCheckDelay));
    }

    [Fact]
    public void DueForSending_ReturnsOnlyPendingDueAndUnlockedRecipients()
    {
        var message = NewMessage(_now, "0501234567", "0521112233", "0541112233");
        var locked = message.Recipients[0];
        var sent = message.Recipients[1];
        var free = message.Recipients[2];
        message.LockRecipient(locked.Id, _now.AddSeconds(60), _now);
        message.RecordSendResult(sent.Id, SendResult.Accepted("p1"), _now, Policy, DeliveryCheckDelay);

        Assert.Equal([free], message.DueForSending(_now));
        Assert.Empty(message.DueForSending(_now.AddSeconds(-1)));
        Assert.Equal([locked, free], message.DueForSending(_now.AddSeconds(60)));
    }

    [Fact]
    public void DueForDeliveryCheck_ReturnsSentRecipientsDueAndSentAfterCutoff()
    {
        var message = SentMessage();
        var recipient = message.Recipients[0];
        var due = _now + DeliveryCheckDelay;

        Assert.Empty(message.DueForDeliveryCheck(due.AddSeconds(-1), sentAfter: _now.AddHours(-24)));
        Assert.Equal([recipient], message.DueForDeliveryCheck(due, sentAfter: _now.AddHours(-24)));
        Assert.Empty(message.DueForDeliveryCheck(due, sentAfter: _now));
    }

    private Message SentMessage(params string[] numbers)
    {
        var message = NewMessage(_now, numbers.Length == 0 ? ["0501234567"] : numbers);
        foreach (var recipient in message.Recipients)
            message.RecordSendResult(recipient.Id, SendResult.Accepted("p-" + recipient.Id), _now, Policy, DeliveryCheckDelay);
        return message;
    }
}
