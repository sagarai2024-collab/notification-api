using System.Net.Mail;
using NotificationApi.Dtos;
using NotificationApi.Models;

namespace NotificationApi.Validation;

public interface INotificationRequestValidator
{
    ValidationResult Validate(CreateNotificationRequest request, DateTimeOffset now, string? idempotencyKey = null);
}

public class ValidationResult
{
    // field name -> messages
    public Dictionary<string, List<string>> Errors { get; } = new();
    public bool IsValid => Errors.Count == 0;

    // Parsed values, only meaningful when IsValid is true.
    public NotificationType Type { get; set; }
    public NotificationPriority Priority { get; set; }

    public void Add(string field, string message)
    {
        if (!Errors.TryGetValue(field, out var list))
            Errors[field] = list = new List<string>();
        list.Add(message);
    }
}

public class NotificationRequestValidator : INotificationRequestValidator
{
    private const int MaxSubjectLength = 200;
    private const int MaxMessageLength = 5000;
    private const int MaxIdempotencyKeyLength = 100;

    public ValidationResult Validate(CreateNotificationRequest request, DateTimeOffset now, string? idempotencyKey = null)
    {
        var result = new ValidationResult();

        if (idempotencyKey is { Length: > MaxIdempotencyKeyLength })
            result.Add("idempotencyKey", $"Idempotency-Key cannot be longer than {MaxIdempotencyKeyLength} characters.");

        if (string.IsNullOrWhiteSpace(request.CustomerId))
            result.Add("customerId", "Customer ID is required.");

        // Type
        var typeIsValid = false;
        if (string.IsNullOrWhiteSpace(request.Type))
            result.Add("type", "Notification type is required.");
        else if (!TryParseEnum(request.Type, out NotificationType type))
            result.Add("type", $"Unsupported notification type '{request.Type}'. Supported: {string.Join(", ", Enum.GetNames<NotificationType>())}.");
        else
        {
            result.Type = type;
            typeIsValid = true;
        }

        // Recipient (the email check applies to Email notifications)
        if (string.IsNullOrWhiteSpace(request.Recipient))
            result.Add("recipient", "Recipient is required.");
        else if (typeIsValid && result.Type == NotificationType.Email && !IsValidEmail(request.Recipient))
            result.Add("recipient", "Recipient must be a valid email address.");

        if (request.Subject is { Length: > MaxSubjectLength })
            result.Add("subject", $"Subject cannot be longer than {MaxSubjectLength} characters.");

        if (string.IsNullOrWhiteSpace(request.Message))
            result.Add("message", "Message is required.");
        else if (request.Message.Length > MaxMessageLength)
            result.Add("message", $"Message cannot be longer than {MaxMessageLength} characters.");

        // Priority is optional and defaults to Normal.
        if (string.IsNullOrWhiteSpace(request.Priority))
            result.Priority = NotificationPriority.Normal;
        else if (!TryParseEnum(request.Priority, out NotificationPriority priority))
            result.Add("priority", "Priority must be one of: Low, Normal, High.");
        else
            result.Priority = priority;

        // "Exactly now" is allowed; only strictly earlier than now is rejected.
        if (request.RequestedDeliveryTime is { } deliveryTime && deliveryTime < now)
            result.Add("requestedDeliveryTime", "Requested delivery time cannot be in the past.");

        return result;
    }

    // Enum.TryParse also accepts numbers like "7" or "1", so we reject those explicitly.
    private static bool TryParseEnum<T>(string value, out T parsed) where T : struct, Enum
    {
        value = value.Trim();
        parsed = default;
        return !int.TryParse(value, out _)
            && Enum.TryParse(value, ignoreCase: true, out parsed)
            && Enum.IsDefined(parsed);
    }

    private static bool IsValidEmail(string email)
    {
        email = email.Trim();
        if (!MailAddress.TryCreate(email, out var address)) return false;

        // MailAddress also accepts "Name <a@b.com>"; we want the bare address only.
        if (address.Address != email) return false;

        // "user@localhost" is technically valid but not a real customer mailbox.
        return address.Host.Contains('.') && !address.Host.EndsWith('.');
    }
}
