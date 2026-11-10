using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sms.Core.Domain;
using Sms.Core.Sending;

namespace Sms.Infrastructure.Sending;

/// <summary>
/// Adapter to the provider supplied with the task (A3). The only class that knows its URLs, JSON and status codes.
/// Never throws for provider or network errors; only a real cancellation propagates.
/// </summary>
internal sealed class MockSmsProvider(HttpClient http, IOptions<SmsProviderOptions> options, ILogger<MockSmsProvider> logger)
    : ISmsProvider
{
    private sealed record ProviderSendRequest(string To, string Body, string Reference);
    private sealed record ProviderSendResponse(string? ProviderId);
    private sealed record ProviderStatusResponse(string? Status);

    public async Task<SendResult> SendAsync(SmsRequest request, CancellationToken cancellationToken)
    {
        var result = await TrySendAsync(request, cancellationToken);
        logger.LogInformation("Provider send for recipient {RecipientId} to {Phone}: {Outcome} {ProviderId} {Error}",
            request.Reference, Mask(request.To), result.Outcome, result.ProviderMessageId, result.Error);
        return result;
    }

    public async Task<DeliveryStatus> GetDeliveryStatusAsync(string providerMessageId, CancellationToken cancellationToken)
    {
        var path = options.Value.StatusPath.Replace("{providerId}", Uri.EscapeDataString(providerMessageId));
        try
        {
            var response = await http.GetFromJsonAsync<ProviderStatusResponse>(path, cancellationToken);
            return response?.Status switch
            {
                "delivered" => DeliveryStatus.Delivered,
                "failed" => DeliveryStatus.NotDelivered,
                _ => DeliveryStatus.Unknown,
            };
        }
        catch (Exception exception) when (IsProviderError(exception, cancellationToken))
        {
            logger.LogWarning(exception, "Provider status check failed for {ProviderId}", providerMessageId);
            return DeliveryStatus.Unknown;
        }
    }

    private async Task<SendResult> TrySendAsync(SmsRequest request, CancellationToken cancellationToken)
    {
        var body = new ProviderSendRequest(request.To, request.Body, request.Reference.ToString());
        try
        {
            using var response = await http.PostAsJsonAsync(options.Value.SendPath, body, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 400 and < 500)
                return SendResult.Permanent($"Provider rejected the message (HTTP {status}).");
            if (!response.IsSuccessStatusCode)
                return SendResult.Transient($"Provider error (HTTP {status}).");

            var accepted = await response.Content.ReadFromJsonAsync<ProviderSendResponse>(cancellationToken);
            return string.IsNullOrEmpty(accepted?.ProviderId)
                ? SendResult.Transient("Provider response had no providerId.")
                : SendResult.Accepted(accepted.ProviderId);
        }
        catch (Exception exception) when (IsProviderError(exception, cancellationToken))
        {
            return SendResult.Transient(exception is OperationCanceledException ? "Provider timeout." : "Provider unreachable.");
        }
    }

    // HttpClient reports its own timeout as a cancellation, so only a cancelled caller token means "really cancelled".
    private static bool IsProviderError(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or JsonException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static string Mask(string phone) => PhoneNumber.TryCreate(phone, out var parsed) ? parsed!.Masked : "***";
}
