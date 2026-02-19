using PrintNest.Application.Interfaces;

namespace PrintNest.Api.Middleware;

/// <summary>
/// Authenticates all requests under /api/v1/device/* using HMAC-SHA256.
///
/// On success: sets HttpContext.Items["AuthenticatedDevice"] to the Device entity.
/// On failure: DomainException is thrown → caught by ErrorHandlingMiddleware → 401 response.
///
/// How to access the authenticated device in a controller:
///   var device = (Device)HttpContext.Items["AuthenticatedDevice"]!;
/// </summary>
public sealed class DeviceAuthMiddleware
{
    private readonly RequestDelegate _next;

    public DeviceAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IDeviceAuthService authService)
    {
        // Read body bytes for signature verification — must buffer the request body
        context.Request.EnableBuffering();
        var bodyBytes = await ReadBodyAsync(context.Request);
        context.Request.Body.Position = 0; // rewind so controllers can read it

        var device = await authService.AuthenticateAsync(
            deviceId:   context.Request.Headers["X-Device-Id"].FirstOrDefault(),
            timestamp:  context.Request.Headers["X-Timestamp"].FirstOrDefault(),
            signature:  context.Request.Headers["X-Signature"].FirstOrDefault(),
            httpMethod: context.Request.Method,
            path:       context.Request.Path.Value ?? "/",
            bodyBytes:  bodyBytes
        );

        // Store authenticated device for controllers to use
        context.Items["AuthenticatedDevice"] = device;

        await _next(context);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentLength is 0 || request.Body == Stream.Null)
            return Array.Empty<byte>();

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }
}
