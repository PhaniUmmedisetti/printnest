using PrintNest.Domain.Entities;

namespace PrintNest.Application.Interfaces;

/// <summary>
/// Authenticates inbound device requests using HMAC-SHA256.
///
/// Every device request must include:
///   X-Device-Id  : the device's registered ID
///   X-Timestamp  : Unix timestamp in seconds (string)
///   X-Signature  : HMACSHA256(secret, timestamp + "\n" + METHOD + "\n" + path + "\n" + bodyHash)
///
/// Where bodyHash = SHA256(body bytes) as lowercase hex.
/// For requests with no body (GET), bodyHash = SHA256("") = e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
///
/// Implementations live in Infrastructure/Auth/HmacDeviceAuthService.cs.
/// </summary>
public interface IDeviceAuthService
{
    /// <summary>
    /// Validates a device request.
    ///
    /// Checks (in order):
    ///   1. X-Device-Id header present
    ///   2. Device exists in DB and IsActive = true
    ///   3. X-Timestamp header present and within ±5 minutes of server time (replay protection)
    ///   4. X-Signature matches expected HMAC
    ///
    /// Returns the authenticated Device on success.
    ///
    /// Throws DomainException(ErrorCodes.DeviceUnauthorized, httpStatus: 401) on any failure.
    /// The error message is deliberately generic — never reveals which check failed.
    /// </summary>
    Task<Device> AuthenticateAsync(
        string? deviceId,
        string? timestamp,
        string? signature,
        string httpMethod,
        string path,
        byte[] bodyBytes
    );
}
