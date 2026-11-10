using Sms.Core.Sending;

namespace Sms.Core.Domain;

/// <summary>
/// Aggregate root: one POST = one message with its recipients.
/// Every status change goes through this class, recalculates <see cref="Status"/> and bumps <see cref="Version"/>.
/// All times are UTC <see cref="DateTime"/> (SQLite cannot compare DateTimeOffset in queries).
/// </summary>
public sealed class Message
{
    private const string NotDeliveredError = "Not delivered";
    private static readonly TimeSpan MaxDeliveryCheckDelay = TimeSpan.FromHours(1);

    private readonly List<MessageRecipient> _recipients = [];

    public Guid Id { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public string Body { get; private set; } = "";
    public MessageStatus Status { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public IReadOnlyList<MessageRecipient> Recipients => _recipients;

    private Message() { } // EF Core

    /// <summary>Creates a Pending message with one Pending recipient per distinct number.</summary>
    public static Message Create(MessageBody body, IEnumerable<PhoneNumber> to, string? idempotencyKey, DateTime now)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            Body = body.Value,
            Status = MessageStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        message._recipients.AddRange(to.Distinct().Select(phone => new MessageRecipient(message.Id, phone, now)));
        return message;
    }

    /// <summary>Recipients that are Pending, due (NextActionAt &lt;= now) and not locked (LockedUntil null or &lt;= now).</summary>
    public IReadOnlyList<MessageRecipient> DueForSending(DateTime now) =>
        _recipients
            .Where(r => r.Status == RecipientStatus.Pending && r.NextActionAt <= now && (r.LockedUntil is null || r.LockedUntil <= now))
            .ToList();

    /// <summary>Recipients that are Sent, due for a check, and were sent after <paramref name="sentAfter"/>.</summary>
    public IReadOnlyList<MessageRecipient> DueForDeliveryCheck(DateTime now, DateTime sentAfter) =>
        _recipients
            .Where(r => r.Status == RecipientStatus.Sent && r.NextActionAt <= now && r.SentAt > sentAfter)
            .ToList();

    /// <summary>Marks the recipient as being worked on until <paramref name="until"/>.</summary>
    public void LockRecipient(Guid recipientId, DateTime until, DateTime now)
    {
        var recipient = FindRecipientIn(recipientId, RecipientStatus.Pending);
        recipient.LockedUntil = until;
        Touch(now);
    }

    /// <summary>
    /// Pending recipient only. Accepted → Sent (ProviderMessageId, SentAt, NextActionAt = now + deliveryCheckDelay).
    /// TransientFailure → retry later, or Failed after policy.MaxAttempts. PermanentFailure → Failed.
    /// Always clears the lock and increments Attempts.
    /// </summary>
    public void RecordSendResult(Guid recipientId, SendResult result, DateTime now, RetryPolicy policy, TimeSpan deliveryCheckDelay)
    {
        var recipient = FindRecipientIn(recipientId, RecipientStatus.Pending);
        recipient.Attempts++;
        recipient.LockedUntil = null;

        switch (result.Outcome)
        {
            case SendOutcome.Accepted:
                recipient.Status = RecipientStatus.Sent;
                recipient.ProviderMessageId = result.ProviderMessageId;
                recipient.SentAt = now;
                recipient.NextActionAt = now + deliveryCheckDelay;
                recipient.LastError = null;
                break;
            case SendOutcome.TransientFailure when recipient.Attempts < policy.MaxAttempts:
                recipient.NextActionAt = now + policy.DelayAfter(recipient.Attempts);
                recipient.LastError = result.Error;
                break;
            default:
                recipient.Status = RecipientStatus.Failed;
                recipient.LastError = result.Error;
                break;
        }

        Touch(now);
    }

    /// <summary>
    /// Sent recipient only. Delivered → Delivered. NotDelivered → Failed ("Not delivered").
    /// Unknown → stays Sent; next check after half the time since sending, at least <paramref name="nextCheckDelay"/>, at most 1 h.
    /// </summary>
    public void RecordDeliveryStatus(Guid recipientId, DeliveryStatus status, DateTime now, TimeSpan nextCheckDelay)
    {
        var recipient = FindRecipientIn(recipientId, RecipientStatus.Sent);

        switch (status)
        {
            case DeliveryStatus.Delivered:
                recipient.Status = RecipientStatus.Delivered;
                break;
            case DeliveryStatus.NotDelivered:
                recipient.Status = RecipientStatus.Failed;
                recipient.LastError = NotDeliveredError;
                break;
            default:
                recipient.NextActionAt = now + NextDeliveryCheckDelay(recipient, now, nextCheckDelay);
                break;
        }

        Touch(now);
    }

    // Checks thin out over time: waiting half the time since sending makes 24 h of tracking
    // cost about 30 provider calls per recipient instead of ~8,600 at a fixed 10 s interval.
    private static TimeSpan NextDeliveryCheckDelay(MessageRecipient recipient, DateTime now, TimeSpan minimum)
    {
        var halfTimeSinceSent = (now - recipient.SentAt!.Value) / 2;
        return TimeSpan.FromTicks(Math.Clamp(halfTimeSinceSent.Ticks, minimum.Ticks, MaxDeliveryCheckDelay.Ticks));
    }

    private MessageRecipient FindRecipientIn(Guid recipientId, RecipientStatus requiredStatus)
    {
        var recipient = _recipients.SingleOrDefault(r => r.Id == recipientId)
            ?? throw new InvalidStatusTransitionException($"Recipient {recipientId} does not belong to message {Id}.");

        if (recipient.Status != requiredStatus)
            throw new InvalidStatusTransitionException(
                $"Recipient {recipientId} is {recipient.Status}; expected {requiredStatus}.");

        return recipient;
    }

    private void Touch(DateTime now)
    {
        UpdatedAt = now;
        Version++;
        Status = CalculateStatus();
    }

    private MessageStatus CalculateStatus()
    {
        if (_recipients.Any(r => r.Status == RecipientStatus.Pending))
            return MessageStatus.Pending;
        if (_recipients.All(r => r.Status == RecipientStatus.Delivered))
            return MessageStatus.Delivered;
        if (_recipients.All(r => r.Status == RecipientStatus.Failed))
            return MessageStatus.Failed;
        if (_recipients.Any(r => r.Status == RecipientStatus.Failed))
            return MessageStatus.PartiallySent;
        return MessageStatus.Sent;
    }
}
