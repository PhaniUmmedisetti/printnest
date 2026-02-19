using System.Text.Json;
using PrintNest.Domain.Errors;

namespace PrintNest.Api.Middleware;

/// <summary>
/// Catches all exceptions and converts them to the standard error envelope:
///   { "error": { "code": "ERROR_CODE", "message": "Human readable" } }
///
/// DomainException → uses its Code and HttpStatus directly.
/// Any other exception → 500 INTERNAL_SERVER_ERROR (message is never exposed to clients).
///
/// Register first in the middleware pipeline so it catches errors from all other middleware.
/// </summary>
public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ErrorHandlingMiddleware(
        RequestDelegate next,
        ILogger<ErrorHandlingMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DomainException ex)
        {
            // Business rule violation — expected, log at warning level
            _logger.LogWarning("Domain error {Code}: {Message}", ex.Code, ex.Message);
            await WriteErrorAsync(context, ex.HttpStatus, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            // Unexpected — log at error level, never expose internals to client
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);

            var message = _env.IsDevelopment()
                ? ex.Message  // helpful in dev
                : "An unexpected error occurred."; // safe in production

            await WriteErrorAsync(context, 500, "INTERNAL_ERROR", message);
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";

        var body = JsonSerializer.Serialize(new
        {
            error = new { code, message }
        }, JsonOptions);

        await context.Response.WriteAsync(body);
    }
}
