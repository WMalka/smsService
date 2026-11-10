using Sms.Core.Domain;

namespace Sms.Core.Application;

/// <summary>Validates, de-duplicates numbers, saves a Pending message. A repeated idempotency key returns the original.</summary>
public sealed class SendMessage(IMessageRepository repository, TimeProvider time, DispatcherOptions options)
{
    public async Task<MessageDto> ExecuteAsync(SendMessageCommand command, CancellationToken cancellationToken)
    {
        var key = command.IdempotencyKey;
        if (key is not null && await repository.FindByIdempotencyKeyAsync(key, cancellationToken) is { } existing)
            return MessageDto.From(existing);

        var (body, phones) = Validate(command);
        var message = Message.Create(body, phones, key, time.GetUtcNow().UtcDateTime);

        try
        {
            await repository.AddAsync(message, cancellationToken);
        }
        catch (DuplicateIdempotencyKeyException)
        {
            // Another request with the same key was saved between our lookup and our insert.
            var winner = await repository.FindByIdempotencyKeyAsync(key!, cancellationToken);
            return MessageDto.From(winner!);
        }

        return MessageDto.From(message);
    }

    private (MessageBody Body, List<PhoneNumber> Phones) Validate(SendMessageCommand command)
    {
        var errors = new Dictionary<string, string[]>();
        var phones = ValidateRecipients(command.To, errors);

        if (!MessageBody.TryCreate(command.Body, out var body, out var bodyError))
            errors["body"] = [bodyError!];

        if (errors.Count > 0)
            throw new DomainValidationException(errors);

        return (body!, phones);
    }

    private List<PhoneNumber> ValidateRecipients(IReadOnlyList<string>? to, Dictionary<string, string[]> errors)
    {
        var phones = new List<PhoneNumber>();
        if (to is null || to.Count == 0)
        {
            errors["to"] = ["At least one recipient is required."];
            return phones;
        }

        if (to.Count > options.MaxRecipientsPerMessage)
            errors["to"] = [$"At most {options.MaxRecipientsPerMessage} recipients are allowed."];

        for (var i = 0; i < to.Count; i++)
        {
            if (PhoneNumber.TryCreate(to[i], out var phone))
                phones.Add(phone!);
            else
                errors[$"to[{i}]"] = [PhoneNumber.InvalidMessage];
        }

        return phones;
    }
}
