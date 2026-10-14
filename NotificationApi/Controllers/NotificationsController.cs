using Microsoft.AspNetCore.Mvc;
using NotificationApi.Dtos;
using NotificationApi.Models;
using NotificationApi.Services;

namespace NotificationApi.Controllers;

[ApiController]
[Route("api/notifications")]
[Produces("application/json")]
public class NotificationsController : ControllerBase
{
    private const int MaxPageSize = 100;

    private readonly INotificationService _service;

    public NotificationsController(INotificationService service)
    {
        _service = service;
    }

    /// <summary>Create a new notification request.</summary>
    /// <remarks>
    /// If the same request was already accepted recently (default 5 minutes), the existing
    /// notification is returned with 200 OK instead of creating a second one.
    /// By default "same" means same content. Send an <c>Idempotency-Key</c> header to decide it yourself:
    /// the same key for the same customer is always treated as the same request.
    /// </remarks>
    /// <param name="request">The notification to create.</param>
    /// <param name="idempotencyKey">Optional. Any unique string up to 100 characters, e.g. an order number.</param>
    [HttpPost]
    [ProducesResponseType(typeof(NotificationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(NotificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateNotificationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var result = await _service.CreateAsync(request, idempotencyKey, ct);

        if (!result.IsValid)
        {
            var errors = result.Errors!.ToDictionary(e => e.Key, e => e.Value.ToArray());
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        var response = NotificationResponse.From(result.Notification!);

        if (result.IsDuplicate)
            return Ok(response);

        return CreatedAtAction(nameof(GetById), new { id = response.Id }, response);
    }

    /// <summary>Get one notification by its ID.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(NotificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var notification = await _service.GetByIdAsync(id, ct);

        return notification is null
            ? NotFound(new ProblemDetails { Title = "Notification not found", Detail = $"No notification with ID {id}.", Status = 404 })
            : Ok(NotificationResponse.From(notification));
    }

    /// <summary>Get the notifications of a customer, newest first, one page at a time.</summary>
    /// <param name="customerId">Customer ID (not case sensitive).</param>
    /// <param name="status">Optional filter: Pending, Processing, Sent or Failed.</param>
    /// <param name="page">Page number, starting at 1.</param>
    /// <param name="pageSize">Items per page, 1 to 100.</param>
    [HttpGet("customer/{customerId}")]
    [ProducesResponseType(typeof(PagedResponse<NotificationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetByCustomer(
        string customerId,
        [FromQuery] NotificationStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
            errors["page"] = new[] { "Page must be 1 or more." };
        if (pageSize is < 1 or > MaxPageSize)
            errors["pageSize"] = new[] { $"Page size must be between 1 and {MaxPageSize}." };
        if (status is { } s && !Enum.IsDefined(s))
            errors["status"] = new[] { "Status must be one of: Pending, Processing, Sent, Failed." };
        if (errors.Count > 0)
            return ValidationProblem(new ValidationProblemDetails(errors));

        var result = await _service.GetByCustomerAsync(customerId, status, page, pageSize, ct);

        return Ok(new PagedResponse<NotificationResponse>(
            page, pageSize, result.TotalCount, result.Items.Select(NotificationResponse.From).ToList()));
    }

    /// <summary>Process all notifications that are due. High priority goes first.</summary>
    /// <remarks>
    /// A failed send is retried automatically later (see "willRetry") until the maximum number of
    /// attempts is reached, then the notification becomes Failed.
    /// </remarks>
    [HttpPost("process")]
    [ProducesResponseType(typeof(ProcessNotificationsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Process(CancellationToken ct)
    {
        var processed = await _service.ProcessPendingAsync(ct);
        var items = processed.Select(NotificationResponse.From).ToList();

        return Ok(new ProcessNotificationsResponse(
            Total: items.Count,
            Sent: items.Count(i => i.Status == NotificationStatus.Sent),
            Failed: items.Count(i => i.Status == NotificationStatus.Failed),
            WillRetry: items.Count(i => i.Status == NotificationStatus.Pending),
            Notifications: items));
    }
}
