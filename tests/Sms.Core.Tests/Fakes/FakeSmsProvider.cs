using System.Collections.Concurrent;
using Sms.Core.Sending;

namespace Sms.Core.Tests.Fakes;

/// <summary>
/// Returns scripted results in call order (default: Accepted / Unknown), records every call,
/// and measures how many calls were in flight at the same time.
/// </summary>
internal sealed class FakeSmsProvider : ISmsProvider
{
    private readonly ConcurrentQueue<SendResult> _sendResults = new();
    private readonly ConcurrentQueue<DeliveryStatus> _deliveryStatuses = new();
    private int _inFlight;
    private int _maxInFlight;

    public ConcurrentQueue<SmsRequest> SendRequests { get; } = new();
    public ConcurrentQueue<string> DeliveryChecks { get; } = new();
    public int MaxInFlight => _maxInFlight;
    public TimeSpan CallDuration { get; set; } = TimeSpan.FromMilliseconds(1);

    public void ScriptSendResults(params SendResult[] results)
    {
        foreach (var result in results)
            _sendResults.Enqueue(result);
    }

    public void ScriptDeliveryStatuses(params DeliveryStatus[] statuses)
    {
        foreach (var status in statuses)
            _deliveryStatuses.Enqueue(status);
    }

    public async Task<SendResult> SendAsync(SmsRequest request, CancellationToken cancellationToken)
    {
        SendRequests.Enqueue(request);
        await SimulateCallAsync(cancellationToken);
        return _sendResults.TryDequeue(out var result) ? result : SendResult.Accepted($"provider-{request.Reference}");
    }

    public async Task<DeliveryStatus> GetDeliveryStatusAsync(string providerMessageId, CancellationToken cancellationToken)
    {
        DeliveryChecks.Enqueue(providerMessageId);
        await SimulateCallAsync(cancellationToken);
        return _deliveryStatuses.TryDequeue(out var status) ? status : DeliveryStatus.Unknown;
    }

    private async Task SimulateCallAsync(CancellationToken cancellationToken)
    {
        var inFlight = Interlocked.Increment(ref _inFlight);
        UpdateMaxInFlight(inFlight);
        try
        {
            await Task.Delay(CallDuration, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void UpdateMaxInFlight(int inFlight)
    {
        int observed;
        do
        {
            observed = _maxInFlight;
            if (inFlight <= observed)
                return;
        } while (Interlocked.CompareExchange(ref _maxInFlight, inFlight, observed) != observed);
    }
}
