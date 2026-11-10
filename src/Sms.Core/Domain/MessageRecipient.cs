namespace Sms.Core.Domain;

/// <summary>One phone number of a message = one SMS. Changed only through <see cref="Message"/>.</summary>
public sealed class MessageRecipient
{
    public Guid Id { get; private set; }
    public Guid MessageId { get; private set; }
    public string PhoneNumber { get; private set; } = "";
    public RecipientStatus Status { get; internal set; }
    public int Attempts { get; internal set; }
    /// <summary>When the worker looks next: send attempt (Pending) or delivery check (Sent). Always UTC.</summary>
    public DateTime NextActionAt { get; internal set; }
    public DateTime? LockedUntil { get; internal set; }
    public string? ProviderMessageId { get; internal set; }
    public DateTime? SentAt { get; internal set; }
    public string? LastError { get; internal set; }

    private MessageRecipient() { } // EF Core

    internal MessageRecipient(Guid messageId, PhoneNumber phone, DateTime now)
    {
        Id = Guid.NewGuid();
        MessageId = messageId;
        PhoneNumber = phone.Value;
        Status = RecipientStatus.Pending;
        NextActionAt = now;
    }
}
