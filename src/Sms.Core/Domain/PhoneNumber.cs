using System.Text.RegularExpressions;

namespace Sms.Core.Domain;

/// <summary>An Israeli mobile number, always stored in E.164 form (+9725XXXXXXXX).</summary>
public sealed partial record PhoneNumber
{
    public const string InvalidMessage = "Phone number must be an Israeli mobile number.";

    private const string IsraelCountryCode = "+972";

    public string Value { get; }

    private PhoneNumber(string value) => Value = value;

    /// <summary>Accepts 05XXXXXXXX, 05X-XXXXXXX (spaces/dashes allowed) or +9725XXXXXXXX.</summary>
    public static bool TryCreate(string? raw, out PhoneNumber? phone)
    {
        phone = null;
        if (raw is null)
            return false;

        var compact = raw.Replace(" ", "").Replace("-", "");
        if (LocalMobile().IsMatch(compact))
            compact = IsraelCountryCode + compact[1..];

        if (!E164Mobile().IsMatch(compact))
            return false;

        phone = new PhoneNumber(compact);
        return true;
    }

    /// <summary>For logs: +97250****567.</summary>
    public string Masked => $"{Value[..6]}****{Value[^3..]}";

    public override string ToString() => Value;

    [GeneratedRegex(@"^05[0-9]{8}$")]
    private static partial Regex LocalMobile();

    [GeneratedRegex(@"^\+9725[0-9]{8}$")]
    private static partial Regex E164Mobile();
}
