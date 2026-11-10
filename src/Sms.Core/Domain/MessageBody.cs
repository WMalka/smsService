namespace Sms.Core.Domain;

/// <summary>SMS text: 1–500 characters after trimming, no control characters except line breaks.</summary>
public sealed record MessageBody
{
    public const int MaxLength = 500;

    public string Value { get; }

    private MessageBody(string value) => Value = value;

    public static bool TryCreate(string? raw, out MessageBody? body, out string? error)
    {
        body = null;
        var text = raw?.Trim() ?? "";
        error = Validate(text);
        if (error is not null)
            return false;

        body = new MessageBody(text);
        return true;
    }

    public override string ToString() => Value;

    private static string? Validate(string text)
    {
        if (text.Length == 0)
            return "Message body is required.";
        if (text.Length > MaxLength)
            return $"Message body must be at most {MaxLength} characters.";
        if (text.Any(IsForbiddenControlCharacter))
            return "Message body must not contain control characters other than line breaks and tabs.";
        return null;
    }

    private static bool IsForbiddenControlCharacter(char c) => char.IsControl(c) && c is not ('\r' or '\n' or '\t');
}
