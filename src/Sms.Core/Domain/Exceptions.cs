namespace Sms.Core.Domain;

/// <summary>Invalid input. <see cref="Errors"/> maps a field name (e.g. "to[1]", "body") to its messages.</summary>
public sealed class DomainValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("One or more validation errors occurred.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public DomainValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] }) { }
}

public sealed class MessageNotFoundException(Guid id) : Exception($"Message {id} was not found.");

/// <summary>A status change that the rules do not allow (e.g. recording a send result for a Sent recipient).</summary>
public sealed class InvalidStatusTransitionException(string message) : Exception(message);
