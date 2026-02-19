namespace PrintNest.Domain.Entities;

/// <summary>
/// Tracks consumed file token JTIs to prevent token reuse.
///
/// When a device uses a file token to download a file, the token's JTI is inserted here
/// within the same transaction as the state transition to Downloading.
///
/// If the same JTI appears again, the download is rejected with TOKEN_ALREADY_USED.
///
/// Cleanup: rows older than 24 hours are deleted by the background cleanup worker
/// (tokens expire in 120s anyway, so rows older than 24h are purely dead weight).
/// </summary>
public sealed class UsedFileToken
{
    /// <summary>JWT ID (jti claim). Primary key — unique per token.</summary>
    public string Jti { get; init; } = string.Empty;

    /// <summary>Job the token was issued for. For audit/debugging purposes.</summary>
    public Guid JobId { get; init; }

    /// <summary>Device the token was issued to. For audit/debugging purposes.</summary>
    public string DeviceId { get; init; } = string.Empty;

    public DateTime UsedAtUtc { get; init; } = DateTime.UtcNow;
}
