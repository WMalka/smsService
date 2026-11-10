using System.Text;

namespace Sms.Core.Domain;

/// <summary>Keyset paging position: the last item seen, ordered by (CreatedAt DESC, Id DESC). Opaque Base64 for clients.</summary>
public sealed record MessageCursor(DateTime CreatedAt, Guid Id)
{
    private const char Separator = '|';

    public string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{CreatedAt.Ticks}{Separator}{Id}"));

    public static bool TryDecode(string? encoded, out MessageCursor? cursor)
    {
        cursor = null;
        var parts = DecodeBase64(encoded)?.Split(Separator);
        if (parts is not { Length: 2 })
            return false;
        if (!long.TryParse(parts[0], out var ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return false;
        if (!Guid.TryParse(parts[1], out var id))
            return false;

        cursor = new MessageCursor(new DateTime(ticks, DateTimeKind.Utc), id);
        return true;
    }

    private static string? DecodeBase64(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return null;
        var buffer = new byte[encoded.Length];
        return Convert.TryFromBase64String(encoded, buffer, out var length)
            ? Encoding.UTF8.GetString(buffer, 0, length)
            : null;
    }
}
