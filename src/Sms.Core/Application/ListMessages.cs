using Sms.Core.Domain;

namespace Sms.Core.Application;

/// <summary>Keyset paging. Invalid status, cursor or limit → DomainValidationException ("status", "cursor", "limit").</summary>
public sealed class ListMessages(IMessageRepository repository)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public async Task<MessagePage> ExecuteAsync(ListMessagesQuery query, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var status = ParseStatus(query.Status, errors);
        var cursor = ParseCursor(query.Cursor, errors);
        var limit = query.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaxLimit)
            errors["limit"] = [$"Limit must be between 1 and {MaxLimit}."];

        if (errors.Count > 0)
            throw new DomainValidationException(errors);

        // One extra item tells us whether another page exists, without a COUNT query.
        var items = await repository.ListAsync(status, cursor, limit + 1, cancellationToken);
        if (items.Count <= limit)
            return new MessagePage(items, NextCursor: null);

        var page = items.Take(limit).ToList();
        var last = page[^1];
        return new MessagePage(page, new MessageCursor(last.CreatedAt, last.Id).Encode());
    }

    private static MessageStatus? ParseStatus(string? raw, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        // Matched by name only: Enum.TryParse would also accept "1" or "Sent,Failed".
        var name = Enum.GetNames<MessageStatus>().FirstOrDefault(n => n.Equals(raw, StringComparison.OrdinalIgnoreCase));
        if (name is not null)
            return Enum.Parse<MessageStatus>(name);

        errors["status"] = [$"Status must be one of: {string.Join(", ", Enum.GetNames<MessageStatus>())}."];
        return null;
    }

    private static MessageCursor? ParseCursor(string? raw, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        if (MessageCursor.TryDecode(raw, out var cursor))
            return cursor;

        errors["cursor"] = ["Cursor is invalid."];
        return null;
    }
}
