using PrintNest.Domain.Errors;
using System.Security.Cryptography;
using System.Text;

namespace PrintNest.Api.Middleware;

/// <summary>
/// Protects all /api/v1/admin/* endpoints with a static API key.
///
/// Required header: X-Admin-Key: {value from AdminApiKey config}
///
/// Uses constant-time comparison to prevent timing attacks.
/// Returns 401 with ADMIN_UNAUTHORIZED on failure — same error regardless of what failed.
///
/// The admin key is read from configuration key "AdminApiKey" (set via environment variable).
/// App will refuse to start if the key is not set or is shorter than 32 characters.
/// </summary>
public sealed class AdminAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[] _expectedKeyBytes;

    public AdminAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;

        var key = config["AdminApiKey"]
            ?? throw new InvalidOperationException(
                "AdminApiKey is required in configuration. Set the ADMIN_API_KEY environment variable.");

        if (key.Length < 32)
            throw new InvalidOperationException(
                "AdminApiKey must be at least 32 characters for security.");

        _expectedKeyBytes = Encoding.UTF8.GetBytes(key);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var providedKey = context.Request.Headers["X-Admin-Key"].FirstOrDefault();

        if (string.IsNullOrEmpty(providedKey))
            throw new DomainException(ErrorCodes.AdminUnauthorized, "Unauthorized.", httpStatus: 401);

        var providedKeyBytes = Encoding.UTF8.GetBytes(providedKey);

        // Constant-time compare — prevents timing attacks even on wrong-length keys
        if (!CryptographicOperations.FixedTimeEquals(_expectedKeyBytes, providedKeyBytes))
            throw new DomainException(ErrorCodes.AdminUnauthorized, "Unauthorized.", httpStatus: 401);

        await _next(context);
    }
}
