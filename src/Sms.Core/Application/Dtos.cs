using Sms.Core.Domain;

namespace Sms.Core.Application;

public sealed record SendMessageCommand(IReadOnlyList<string>? To, string? Body, string? IdempotencyKey);

public sealed record ListMessagesQuery(string? Status, string? Cursor, int? Limit);

public sealed record RecipientDto(Guid Id, string To, RecipientStatus Status, int Attempts, string? LastError)
{
    public static RecipientDto From(MessageRecipient recipient) =>
        new(recipient.Id, recipient.PhoneNumber, recipient.Status, recipient.Attempts, recipient.LastError);
}

public sealed record MessageDto(
    Guid Id, string Body, MessageStatus Status, IReadOnlyList<RecipientDto> Recipients,
    DateTime CreatedAt, DateTime UpdatedAt)
{
    public static MessageDto From(Message message) => new(
        message.Id, message.Body, message.Status,
        message.Recipients.Select(RecipientDto.From).ToList(),
        message.CreatedAt, message.UpdatedAt);
}

public sealed record MessagePage(IReadOnlyList<MessageSummary> Items, string? NextCursor);
