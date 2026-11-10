using Sms.Core.Domain;

namespace Sms.Core.Application;

public interface IMessageRepository
{
    /// <summary>Message with its recipients, or null.</summary>
    Task<Message?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Message?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Inserts and saves. Throws <see cref="DuplicateIdempotencyKeyException"/> if the key already exists.</summary>
    Task AddAsync(Message message, CancellationToken cancellationToken);

    /// <summary>Newest first by (CreatedAt DESC, Id DESC), items strictly after <paramref name="after"/>, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<MessageSummary>> ListAsync(MessageStatus? status, MessageCursor? after, int limit, CancellationToken cancellationToken);

    /// <summary>Tracked messages (with all recipients) that have at least one recipient due for sending (see Message.DueForSending).</summary>
    Task<IReadOnlyList<Message>> GetWithRecipientsDueForSendingAsync(DateTime now, int batchSize, CancellationToken cancellationToken);

    /// <summary>Tracked messages (with all recipients) that have at least one recipient due for a delivery check (see Message.DueForDeliveryCheck).</summary>
    Task<IReadOnlyList<Message>> GetWithRecipientsDueForDeliveryCheckAsync(DateTime now, DateTime sentAfter, int batchSize, CancellationToken cancellationToken);

    /// <summary>Saves changes to tracked messages. Throws <see cref="ConcurrencyConflictException"/> on a stale Version.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record MessageSummary(
    Guid Id, string Body, MessageStatus Status,
    int RecipientCount, int DeliveredCount, int SentCount, int FailedCount,
    DateTime CreatedAt);

public sealed class DuplicateIdempotencyKeyException(string key) : Exception($"Idempotency key '{key}' already exists.");

public sealed class ConcurrencyConflictException(Exception inner) : Exception("The data was changed by someone else.", inner);
