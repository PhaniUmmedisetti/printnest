using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Entities;
using PrintNest.Domain.Errors;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Infrastructure.Auth;

/// <summary>
/// Authenticates device requests using HMAC-SHA256 signed headers.
///
/// Signature format:
///   HMACSHA256(secret, timestamp + "\n" + METHOD + "\n" + path + "\n" + bodyHash)
///
/// Where:
///   secret    = base64url-decoded SharedSecret from DB
///   timestamp = Unix seconds as string (from X-Timestamp header)
///   METHOD    = uppercase HTTP method (GET, POST, etc.)
///   path      = full request path, e.g. /api/v1/device/heartbeat
///   bodyHash  = lowercase hex SHA256 of raw body bytes
///               For empty body: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
///
/// Replay protection: timestamp must be within ±5 minutes of server time.
/// </summary>
public sealed class HmacDeviceAuthService : IDeviceAuthService
{
    private readonly AppDbContext _db;
    private const int MaxTimestampDriftSeconds = 300; // 5 minutes

    // SHA256 of empty body — precomputed for efficiency on GET requests
    private const string EmptyBodyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public HmacDeviceAuthService(AppDbContext db) => _db = db;

    public async Task<Device> AuthenticateAsync(
        string? deviceId,
        string? timestamp,
        string? signature,
        string httpMethod,
        string path,
        byte[] bodyBytes)
    {
        // ── All headers must be present ───────────────────────────
        if (string.IsNullOrEmpty(deviceId) ||
            string.IsNullOrEmpty(timestamp) ||
            string.IsNullOrEmpty(signature))
            Reject();

        // ── Device must exist and be active ───────────────────────
        var device = await _db.Devices.FirstOrDefaultAsync(d =>
            d.DeviceId == deviceId && d.IsActive);

        if (device is null) Reject();

        // ── Timestamp must be within drift window ─────────────────
        if (!long.TryParse(timestamp, out var tsSeconds)) Reject();

        var serverTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(serverTime - tsSeconds) > MaxTimestampDriftSeconds) Reject();

        // ── Compute expected signature ────────────────────────────
        var bodyHash = bodyBytes.Length == 0
            ? EmptyBodyHash
            : Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();

        var message = $"{timestamp}\n{httpMethod.ToUpperInvariant()}\n{path}\n{bodyHash}";
        var secretBytes = Convert.FromBase64String(device!.SharedSecret);

        using var hmac = new HMACSHA256(secretBytes);
        var expectedSig = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(message))
        ).ToLowerInvariant();

        // ── Constant-time comparison to prevent timing attacks ────
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedSig),
            Encoding.UTF8.GetBytes(signature!.ToLowerInvariant())))
            Reject();

        return device!;
    }

    /// <summary>
    /// Always throws the same generic error regardless of which check failed.
    /// Never reveals whether the device exists, which header is missing, or what the expected signature is.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject() =>
        throw new DomainException(ErrorCodes.DeviceUnauthorized, "Unauthorized.", httpStatus: 401);
}
