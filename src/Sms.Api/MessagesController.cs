using Microsoft.AspNetCore.Mvc;
using Sms.Core.Application;

namespace Sms.Api;

/// <summary>Body of POST /messages.</summary>
/// <param name="To">Israeli mobile numbers: 05XXXXXXXX, 05X-XXXXXXX or +9725XXXXXXXX (1–100, duplicates are sent once).</param>
/// <param name="Body">SMS text, 1–500 characters.</param>
public sealed record SendMessageRequest(string[]? To, string? Body);

/// <summary>Send SMS messages and follow their status.</summary>
[ApiController]
[Route("messages")]
[Tags("Messages")]
[Produces("application/json")]
public sealed class MessagesController(SendMessage sendMessage, GetMessage getMessage, ListMessages listMessages)
    : ControllerBase
{
    /// <summary>Send one SMS body to one or more phone numbers.</summary>
    /// <remarks>
    /// The message is saved and sent in the background, so the response is 202 and Location points to its status.
    /// Repeating the same Idempotency-Key returns the original message and sends nothing new.
    /// </remarks>
    /// <param name="request">Recipients and text.</param>
    /// <param name="idempotencyKey">Optional. A unique value per submission (e.g. a UUID).</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpPost]
    [ProducesResponseType<MessageDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MessageDto>> Send(
        SendMessageRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var message = await sendMessage.ExecuteAsync(
            new SendMessageCommand(request.To, request.Body, idempotencyKey), cancellationToken);
        return Accepted($"/messages/{message.Id}", message);
    }

    /// <summary>Get one message with the status of every recipient.</summary>
    /// <param name="id">Message id from the send response.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<MessageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<MessageDto> Get(Guid id, CancellationToken cancellationToken) =>
        getMessage.ExecuteAsync(id, cancellationToken);

    /// <summary>List messages, newest first, with an optional status filter.</summary>
    /// <param name="status">Pending, Sent, Delivered, PartiallySent or Failed (case-insensitive).</param>
    /// <param name="cursor">The nextCursor from the previous page; omit for the first page.</param>
    /// <param name="limit">Page size, 1–100 (default 20).</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet]
    [ProducesResponseType<MessagePage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<MessagePage> List(string? status, string? cursor, int? limit, CancellationToken cancellationToken) =>
        listMessages.ExecuteAsync(new ListMessagesQuery(status, cursor, limit), cancellationToken);
}
