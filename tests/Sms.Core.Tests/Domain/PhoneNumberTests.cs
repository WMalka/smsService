using Sms.Core.Domain;

namespace Sms.Core.Tests.Domain;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("050-1234567")]
    [InlineData("0501234567")]
    [InlineData("050 123 4567")]
    [InlineData("+972501234567")]
    [InlineData(" +972-50-123-4567 ")]
    public void PhoneNumber_LocalFormatWithDash_IsNormalizedToE164(string raw)
    {
        Assert.True(PhoneNumber.TryCreate(raw, out var phone));
        Assert.Equal("+972501234567", phone!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("03-1234567")]       // landline
    [InlineData("+97231234567")]     // landline E.164
    [InlineData("050123456")]        // too short
    [InlineData("05012345678")]      // too long
    [InlineData("+9725012345678")]   // too long E.164
    [InlineData("+15551234567")]     // other country
    [InlineData("050-123456a")]      // letters
    public void PhoneNumber_LandlineOrTooShort_IsRejected(string? raw)
    {
        Assert.False(PhoneNumber.TryCreate(raw, out var phone));
        Assert.Null(phone);
    }

    [Fact]
    public void PhoneNumber_Masked_HidesMiddleDigits()
    {
        PhoneNumber.TryCreate("050-1234567", out var phone);

        Assert.Equal("+97250****567", phone!.Masked);
    }
}
