using System.Text;
using Sms.Core.Domain;

namespace Sms.Core.Tests.Domain;

public class MessageCursorTests
{
    [Fact]
    public void MessageCursor_RoundTrip_ReturnsSameCreatedAtAndId()
    {
        var original = new MessageCursor(new DateTime(2026, 11, 10, 8, 0, 0, 123, DateTimeKind.Utc), Guid.NewGuid());

        Assert.True(MessageCursor.TryDecode(original.Encode(), out var decoded));
        Assert.Equal(original, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded!.CreatedAt.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("aGVsbG8=")] // "hello"
    public void MessageCursor_Garbage_IsRejected(string? encoded)
    {
        Assert.False(MessageCursor.TryDecode(encoded, out var cursor));
        Assert.Null(cursor);
    }

    [Theory]
    [InlineData("abc|" + "6f1c0000-0000-0000-0000-000000000000")]
    [InlineData("-1|6f1c0000-0000-0000-0000-000000000000")]
    [InlineData("638000000000000000|not-a-guid")]
    [InlineData("638000000000000000|6f1c0000-0000-0000-0000-000000000000|extra")]
    public void MessageCursor_WellFormedBase64WithBadContent_IsRejected(string content)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

        Assert.False(MessageCursor.TryDecode(encoded, out _));
    }
}
