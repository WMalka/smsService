namespace Sms.Core.Sending;

/// <summary>
/// The boundary to the SMS provider. Uses only our own types; provider URLs, JSON and error codes stay in the adapter.
/// Implementations never throw for provider/network errors: they return a failure outcome.
/// </summary>
public interface ISmsProvider
{
    Task<SendResult> SendAsync(SmsRequest request, CancellationToken cancellationToken);
    Task<DeliveryStatus> GetDeliveryStatusAsync(string providerMessageId, CancellationToken cancellationToken);
}

/// <summary><paramref name="Reference"/> is the recipient id; the provider must not send twice for the same reference.</summary>
public sealed record SmsRequest(Guid Reference, string To, string Body);

public enum SendOutcome { Accepted, TransientFailure, PermanentFailure }

public sealed record SendResult(SendOutcome Outcome, string? ProviderMessageId = null, string? Error = null)
{
    public static SendResult Accepted(string providerMessageId) => new(SendOutcome.Accepted, providerMessageId);
    public static SendResult Transient(string error) => new(SendOutcome.TransientFailure, Error: error);
    public static SendResult Permanent(string error) => new(SendOutcome.PermanentFailure, Error: error);
}

/// <summary>Unknown = not confirmed yet, or the check itself failed.</summary>
public enum DeliveryStatus { Unknown, Delivered, NotDelivered }
