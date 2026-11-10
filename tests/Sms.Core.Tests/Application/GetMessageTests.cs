using Sms.Core.Application;
using Sms.Core.Domain;
using Sms.Core.Tests.Fakes;

namespace Sms.Core.Tests.Application;

public class GetMessageTests
{
    private readonly FakeMessageRepository _repository = new();

    [Fact]
    public async Task GetMessage_Existing_ReturnsMessageWithRecipients()
    {
        var message = TestData.NewMessage(TestData.Start, "0501234567");
        await _repository.AddAsync(message, default);

        var dto = await new GetMessage(_repository).ExecuteAsync(message.Id, default);

        Assert.Equal(message.Id, dto.Id);
        var recipient = Assert.Single(dto.Recipients);
        Assert.Equal(message.Recipients[0].Id, recipient.Id);
        Assert.Equal("+972501234567", recipient.To);
    }

    [Fact]
    public async Task GetMessage_UnknownId_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<MessageNotFoundException>(() =>
            new GetMessage(_repository).ExecuteAsync(Guid.NewGuid(), default));
    }
}
