using NotificationApi.Dtos;
using NotificationApi.Models;
using NotificationApi.Validation;

namespace NotificationApi.Tests;

public class ValidatorTests
{
    private static readonly DateTimeOffset Now = ServiceFixture.Start;
    private readonly NotificationRequestValidator _validator = new();

    [Fact]
    public void Valid_request_passes()
    {
        var result = _validator.Validate(ServiceFixture.ValidRequest(), Now);

        Assert.True(result.IsValid);
        Assert.Equal(NotificationType.Email, result.Type);
        Assert.Equal(NotificationPriority.Normal, result.Priority);
    }

    [Fact]
    public void Empty_request_reports_every_missing_required_field()
    {
        var result = _validator.Validate(new CreateNotificationRequest(), Now);

        Assert.Contains("customerId", result.Errors.Keys);
        Assert.Contains("type", result.Errors.Keys);
        Assert.Contains("recipient", result.Errors.Keys);
        Assert.Contains("message", result.Errors.Keys);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing@domain")]
    [InlineData("two@@example.com")]
    [InlineData("Name <a@example.com>")]
    [InlineData("a@example.")]
    public void Invalid_email_is_rejected(string recipient)
    {
        var result = _validator.Validate(ServiceFixture.ValidRequest(recipient: recipient), Now);

        Assert.Contains("recipient", result.Errors.Keys);
    }

    [Theory]
    [InlineData("low", NotificationPriority.Low)]
    [InlineData("HIGH", NotificationPriority.High)]
    public void Priority_is_case_insensitive(string input, NotificationPriority expected)
    {
        var result = _validator.Validate(ServiceFixture.ValidRequest(priority: input), Now);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Priority);
    }

    [Theory]
    [InlineData("Urgent")]
    [InlineData("7")]   // a number must not sneak through as an enum value
    [InlineData("1")]
    public void Unknown_priority_is_rejected(string priority)
    {
        var result = _validator.Validate(ServiceFixture.ValidRequest(priority: priority), Now);

        Assert.Contains("priority", result.Errors.Keys);
    }

    [Fact]
    public void Unsupported_type_is_rejected()
    {
        var request = ServiceFixture.ValidRequest();
        request.Type = "Sms";

        Assert.Contains("type", _validator.Validate(request, Now).Errors.Keys);
    }

    [Fact]
    public void Delivery_time_in_the_past_is_rejected()
    {
        var result = _validator.Validate(
            ServiceFixture.ValidRequest(deliveryTime: Now.AddSeconds(-1)), Now);

        Assert.Contains("requestedDeliveryTime", result.Errors.Keys);
    }

    [Fact]
    public void Delivery_time_exactly_now_is_allowed()
    {
        var result = _validator.Validate(ServiceFixture.ValidRequest(deliveryTime: Now), Now);

        Assert.True(result.IsValid);
    }
}
