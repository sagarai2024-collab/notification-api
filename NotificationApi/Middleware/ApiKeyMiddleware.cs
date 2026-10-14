using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NotificationApi.Configuration;

namespace NotificationApi.Middleware;

// Very simple authentication: if Auth:ApiKey is set, every /api call must send the header X-Api-Key.
// If it is empty, authentication is off (handy for local testing).
public class ApiKeyMiddleware
{
    public const string HeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly byte[] _expectedKey;

    public ApiKeyMiddleware(RequestDelegate next, IOptions<AuthOptions> options)
    {
        _next = next;
        _expectedKey = Encoding.UTF8.GetBytes(options.Value.ApiKey ?? "");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var protectedPath = context.Request.Path.StartsWithSegments("/api");

        if (_expectedKey.Length == 0 || !protectedPath || IsKeyValid(context))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = 401,
            Title = "Unauthorized",
            Detail = $"Missing or wrong {HeaderName} header."
        });
    }

    private bool IsKeyValid(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var provided))
            return false;

        // Fixed-time comparison, so response time does not leak how much of the key was right.
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided.ToString()), _expectedKey);
    }
}
