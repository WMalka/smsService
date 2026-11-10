using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sms.Core.Application;
using Sms.Core.Domain;

namespace Sms.Infrastructure.Persistence;

internal sealed class MessageRepository(SmsDbContext db) : IMessageRepository
{
    private const int SqliteConstraintViolation = 19;

    public Task<Message?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Messages.Include(m => m.Recipients).FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<Message?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.Messages.Include(m => m.Recipients).FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task AddAsync(Message message, CancellationToken cancellationToken)
    {
        db.Messages.Add(message);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyViolation(exception))
        {
            DetachFailedInsert();
            throw new DuplicateIdempotencyKeyException(message.IdempotencyKey!);
        }
    }

    public async Task<IReadOnlyList<MessageSummary>> ListAsync(
        MessageStatus? status, MessageCursor? after, int limit, CancellationToken cancellationToken)
    {
        var messages = db.Messages.AsQueryable();
        if (status is not null)
            messages = messages.Where(m => m.Status == status);
        if (after is not null)
            // Id is stored as TEXT, so the Guid tie-breaker compares (and orders, below) as text in SQL.
            messages = messages.Where(m => m.CreatedAt < after.CreatedAt
                                           || (m.CreatedAt == after.CreatedAt && m.Id.CompareTo(after.Id) < 0));

        return await messages
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(limit)
            .Select(m => new MessageSummary(
                m.Id, m.Body, m.Status,
                m.Recipients.Count(),
                m.Recipients.Count(r => r.Status == RecipientStatus.Delivered),
                m.Recipients.Count(r => r.Status == RecipientStatus.Sent),
                m.Recipients.Count(r => r.Status == RecipientStatus.Failed),
                m.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> GetWithRecipientsDueForSendingAsync(
        DateTime now, int batchSize, CancellationToken cancellationToken) =>
        await db.Messages
            .Where(m => m.Recipients.Any(r => r.Status == RecipientStatus.Pending
                                              && r.NextActionAt <= now
                                              && (r.LockedUntil == null || r.LockedUntil <= now)))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .Include(m => m.Recipients)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Message>> GetWithRecipientsDueForDeliveryCheckAsync(
        DateTime now, DateTime sentAfter, int batchSize, CancellationToken cancellationToken) =>
        await db.Messages
            .Where(m => m.Recipients.Any(r => r.Status == RecipientStatus.Sent
                                              && r.NextActionAt <= now
                                              && r.SentAt > sentAfter))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .Include(m => m.Recipients)
            .ToListAsync(cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyConflictException(exception);
        }
    }

    private static bool IsIdempotencyKeyViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintViolation } sqlite
        && sqlite.Message.Contains($"{nameof(SmsDbContext.Messages)}.{nameof(Message.IdempotencyKey)}");

    // The failed insert stays tracked otherwise, and the next SaveChanges would retry it.
    private void DetachFailedInsert()
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            entry.State = EntityState.Detached;
    }
}
