using Sms.Core.Domain;

namespace Sms.Core.Tests.Domain;

public class MessageBodyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void MessageBody_EmptyOrWhitespace_IsRejected(string? raw)
    {
        Assert.False(MessageBody.TryCreate(raw, out var body, out var error));
        Assert.Null(body);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void MessageBody_LongerThan500_IsRejected()
    {
        Assert.False(MessageBody.TryCreate(new string('a', 501), out _, out var error));
        Assert.Contains("500", error);
    }

    [Fact]
    public void MessageBody_Exactly500AfterTrimming_IsAccepted()
    {
        Assert.True(MessageBody.TryCreate("  " + new string('a', 500) + "  ", out var body, out var error));
        Assert.Equal(500, body!.Value.Length);
        Assert.Null(error);
    }

    [Fact]
    public void MessageBody_WithQuotesAndSqlWords_IsAccepted()
    {
        const string text = "Don't forget: meeting at 10; bring the \"OR 1=1\" report'); DROP TABLE Messages;--";

        Assert.True(MessageBody.TryCreate(text, out var body, out _));
        Assert.Equal(text, body!.Value);
    }

    [Fact]
    public void MessageBody_WithLineBreaksAndHebrew_IsAccepted()
    {
        Assert.True(MessageBody.TryCreate("שלום\r\nShift\tstarts", out var body, out _));
        Assert.Equal("שלום\r\nShift\tstarts", body!.Value);
    }

    [Fact]
    public void MessageBody_WithControlCharacter_IsRejected()
    {
        Assert.False(MessageBody.TryCreate("bad\u0007bell", out _, out var error));
        Assert.Contains("control", error);
    }

    [Fact]
    public void MessageBody_IsTrimmed()
    {
        MessageBody.TryCreate("  hello  ", out var body, out _);

        Assert.Equal("hello", body!.Value);
    }
}
