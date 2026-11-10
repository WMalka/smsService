using Sms.Core.Domain;

namespace Sms.Core.Application;

/// <summary>Throws MessageNotFoundException when missing.</summary>
public sealed class GetMessage(IMessageRepository repository)
{
    public async Task<MessageDto> ExecuteAsync(Guid id, CancellationToken cancellationToken)
    {
        var message = await repository.GetAsync(id, cancellationToken) ?? throw new MessageNotFoundException(id);
        return MessageDto.From(message);
    }
}
