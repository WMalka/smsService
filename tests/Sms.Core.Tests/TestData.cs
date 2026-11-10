using Sms.Core.Domain;

namespace Sms.Core.Tests;

internal static class TestData
{
    public static readonly DateTime Start = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
    public static readonly RetryPolicy Policy = new(3, TimeSpan.FromSeconds(2));
    public static readonly TimeSpan DeliveryCheckDelay = TimeSpan.FromSeconds(10);

    public static PhoneNumber Phone(string raw) =>
        PhoneNumber.TryCreate(raw, out var phone) ? phone! : throw new ArgumentException($"Bad test phone {raw}");

    public static MessageBody Body(string raw = "Shift starts at 07:00") =>
        MessageBody.TryCreate(raw, out var body, out _) ? body! : throw new ArgumentException("Bad test body");

    public static Message NewMessage(DateTime now, params string[] numbers) =>
        Message.Create(Body(), numbers.Select(Phone), idempotencyKey: null, now);
}
