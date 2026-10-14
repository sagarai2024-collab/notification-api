namespace NotificationApi.Dtos;

// Everything is nullable on purpose: the validator, not the model binder,
// decides what is missing, so the caller gets one consistent error format.
public class CreateNotificationRequest
{
    /// <example>CUST-101</example>
    public string? CustomerId { get; set; }

    /// <summary>Only "Email" is supported for now.</summary>
    /// <example>Email</example>
    public string? Type { get; set; }

    /// <example>customer@example.com</example>
    public string? Recipient { get; set; }

    /// <example>Order Confirmed</example>
    public string? Subject { get; set; }

    /// <example>Your order has been confirmed.</example>
    public string? Message { get; set; }

    /// <summary>Low, Normal or High. Defaults to Normal when omitted.</summary>
    /// <example>Normal</example>
    public string? Priority { get; set; }

    /// <summary>Optional. When omitted the notification is due immediately.</summary>
    /// <example>2030-10-06T12:30:00Z</example>
    public DateTimeOffset? RequestedDeliveryTime { get; set; }
}
