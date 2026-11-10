using Sms.Core.Domain;

namespace Sms.Core.Tests.Domain;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void RetryPolicy_DelayAfter_DoublesWithEachFailure(int failedAttempts, int expectedSeconds)
    {
        var policy = new RetryPolicy(3, TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), policy.DelayAfter(failedAttempts));
    }
}
