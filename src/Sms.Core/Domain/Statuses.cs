namespace Sms.Core.Domain;

public enum RecipientStatus { Pending, Sent, Delivered, Failed }

public enum MessageStatus { Pending, Sent, Delivered, PartiallySent, Failed }

/// <summary>Max attempts per recipient; wait FirstRetryDelay before the 2nd attempt, doubling each time.</summary>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan FirstRetryDelay)
{
    /// <summary>Delay before the next attempt, after <paramref name="failedAttempts"/> failures (1 → FirstRetryDelay, 2 → 2x, ...).</summary>
    public TimeSpan DelayAfter(int failedAttempts) => FirstRetryDelay * Math.Pow(2, failedAttempts - 1);
}
