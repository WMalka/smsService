using System.Collections.Concurrent;
using Sms.Core.Domain;
using Sms.Core.Sending;

namespace Sms.Core.Application;

/// <summary>One worker tick: lock + send due recipients in parallel, then check delivery of Sent recipients.</summary>
public sealed class DispatchPending(IMessageRepository repository, ISmsProvider provider, TimeProvider time, DispatcherOptions options)
{
    private sealed record WorkItem(Message Message, MessageRecipient Recipient);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await SendDueRecipientsAsync(cancellationToken);
        await CheckDeliveryOfSentRecipientsAsync(cancellationToken);
    }

    private async Task SendDueRecipientsAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var messages = await repository.GetWithRecipientsDueForSendingAsync(now, options.BatchSize, cancellationToken);
        var work = messages.SelectMany(m => m.DueForSending(now).Select(r => new WorkItem(m, r))).ToList();
        if (work.Count == 0)
            return;

        // The lock is saved before calling the provider, so a crash mid-send leaves recipients to be retried after it expires.
        foreach (var item in work)
            item.Message.LockRecipient(item.Recipient.Id, now.AddSeconds(options.LockSeconds), now);
        await repository.SaveChangesAsync(cancellationToken);

        var results = await CallProviderInParallelAsync(work,
            (item, ct) => provider.SendAsync(new SmsRequest(item.Recipient.Id, item.Recipient.PhoneNumber, item.Message.Body), ct),
            cancellationToken);

        var appliedAt = Now;
        foreach (var (item, result) in results)
            item.Message.RecordSendResult(item.Recipient.Id, result, appliedAt, options.RetryPolicy, DeliveryCheckDelay);
        await repository.SaveChangesAsync(cancellationToken);
    }

    private async Task CheckDeliveryOfSentRecipientsAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var sentAfter = now.AddHours(-options.DeliveryTrackingHours);
        var messages = await repository.GetWithRecipientsDueForDeliveryCheckAsync(now, sentAfter, options.BatchSize, cancellationToken);
        var work = messages.SelectMany(m => m.DueForDeliveryCheck(now, sentAfter).Select(r => new WorkItem(m, r))).ToList();
        if (work.Count == 0)
            return;

        var results = await CallProviderInParallelAsync(work,
            (item, ct) => provider.GetDeliveryStatusAsync(item.Recipient.ProviderMessageId!, ct),
            cancellationToken);

        var appliedAt = Now;
        foreach (var (item, status) in results)
            item.Message.RecordDeliveryStatus(item.Recipient.Id, status, appliedAt, DeliveryCheckDelay);
        await repository.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Only provider calls run in parallel: the repository (DbContext) and the aggregates are not thread-safe.</summary>
    private async Task<IReadOnlyCollection<(WorkItem Item, TResult Result)>> CallProviderInParallelAsync<TResult>(
        IEnumerable<WorkItem> work, Func<WorkItem, CancellationToken, Task<TResult>> call, CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<(WorkItem, TResult)>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaxParallelSends,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(work, parallelOptions, async (item, ct) => results.Add((item, await call(item, ct))));
        return results;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private TimeSpan DeliveryCheckDelay => TimeSpan.FromSeconds(options.DeliveryCheckSeconds);
}
