using Sms.Core.Domain;

namespace Sms.Core.Application;

/// <summary>Bound from the "Dispatcher" configuration section.</summary>
public sealed class DispatcherOptions
{
    public const string Section = "Dispatcher";

    public int IntervalSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 50;
    public int MaxParallelSends { get; set; } = 10;
    public int LockSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 3;
    public int FirstRetryDelaySeconds { get; set; } = 2;
    public int DeliveryCheckSeconds { get; set; } = 10;
    public int DeliveryTrackingHours { get; set; } = 24;
    public int MaxRecipientsPerMessage { get; set; } = 100;

    public RetryPolicy RetryPolicy => new(MaxAttempts, TimeSpan.FromSeconds(FirstRetryDelaySeconds));
}
