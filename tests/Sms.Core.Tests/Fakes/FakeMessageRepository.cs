using Sms.Core.Application;
using Sms.Core.Domain;

namespace Sms.Core.Tests.Fakes;

/// <summary>In-memory repository. Messages are held by reference, so "tracked" changes are visible without saving.</summary>
internal sealed class FakeMessageRepository : IMessageRepository
{
    private readonly List<Message> _messages = [];

    public IReadOnlyList<Message> Messages => _messages;
    public int SaveChangesCount { get; private set; }

    /// <summary>Simulates another request that inserts this message between our key lookup and our insert.</summary>
    public Message? InsertedConcurrentlyBeforeNextAdd { get; set; }

    public Task<Message?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_messages.SingleOrDefault(m => m.Id == id));

    public Task<Message?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(_messages.SingleOrDefault(m => m.IdempotencyKey == idempotencyKey));

    public Task AddAsync(Message message, CancellationToken cancellationToken)
    {
        if (InsertedConcurrentlyBeforeNextAdd is { } other)
        {
            _messages.Add(other);
            InsertedConcurrentlyBeforeNextAdd = null;
        }

        if (message.IdempotencyKey is not null && _messages.Any(m => m.IdempotencyKey == message.IdempotencyKey))
            throw new DuplicateIdempotencyKeyException(message.IdempotencyKey);

        _messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MessageSummary>> ListAsync(MessageStatus? status, MessageCursor? after, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<MessageSummary> page = _messages
            .Where(m => status is null || m.Status == status)
            .Where(m => after is null || IsAfter(m, after))
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(limit)
            .Select(ToSummary)
            .ToList();
        return Task.FromResult(page);
    }

    public Task<IReadOnlyList<Message>> GetWithRecipientsDueForSendingAsync(DateTime now, int batchSize, CancellationToken cancellationToken)
    {
        IReadOnlyList<Message> due = _messages.Where(m => m.DueForSending(now).Count > 0).Take(batchSize).ToList();
        return Task.FromResult(due);
    }

    public Task<IReadOnlyList<Message>> GetWithRecipientsDueForDeliveryCheckAsync(DateTime now, DateTime sentAfter, int batchSize, CancellationToken cancellationToken)
    {
        IReadOnlyList<Message> due = _messages.Where(m => m.DueForDeliveryCheck(now, sentAfter).Count > 0).Take(batchSize).ToList();
        return Task.FromResult(due);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCount++;
        return Task.CompletedTask;
    }

    // Newest first: "after" the cursor means older, or same time with a smaller id.
    private static bool IsAfter(Message message, MessageCursor cursor) =>
        message.CreatedAt < cursor.CreatedAt || (message.CreatedAt == cursor.CreatedAt && message.Id.CompareTo(cursor.Id) < 0);

    private static MessageSummary ToSummary(Message m) => new(
        m.Id, m.Body, m.Status,
        m.Recipients.Count,
        m.Recipients.Count(r => r.Status == RecipientStatus.Delivered),
        m.Recipients.Count(r => r.Status == RecipientStatus.Sent),
        m.Recipients.Count(r => r.Status == RecipientStatus.Failed),
        m.CreatedAt);
}
