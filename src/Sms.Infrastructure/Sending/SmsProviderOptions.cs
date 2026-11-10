namespace Sms.Infrastructure.Sending;

/// <summary>Bound from the "SmsProvider" configuration section.</summary>
public sealed class SmsProviderOptions
{
    public const string Section = "SmsProvider";

    public string BaseUrl { get; set; } = "http://localhost:5005";
    public string SendPath { get; set; } = "/send";
    public string StatusPath { get; set; } = "/status/{providerId}";
    public int TimeoutSeconds { get; set; } = 10;
}
