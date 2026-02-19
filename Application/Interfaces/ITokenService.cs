namespace PrintNest.Application.Interfaces;

/// <summary>
/// Issues and validates short-lived, device-bound file tokens (JWTs).
///
/// File tokens are issued at job release time and consumed exactly once during file download.
/// Token reuse is prevented by storing the JTI in the UsedFileTokens table.
///
/// Implementations live in Infrastructure/Auth/JwtTokenService.cs.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Issues a new file token for a specific job and device.
    ///
    /// JWT claims:
    ///   sub  = jobId
    ///   did  = deviceId  (custom claim)
    ///   jti  = random GUID (for single-use enforcement)
    ///   exp  = now + tokenTtlSeconds (default: 120)
    ///   iat  = now
    ///
    /// Signing: HS256 with key from configuration "Jwt:SigningKey".
    /// </summary>
    string IssueFileToken(Guid jobId, string deviceId);

    /// <summary>
    /// Validates a file token and returns its claims.
    ///
    /// Throws DomainException(ErrorCodes.TokenExpired) if exp has passed.
    /// Throws DomainException(ErrorCodes.TokenInvalid) if signature is bad or claims are missing.
    ///
    /// Does NOT check single-use — that is the caller's responsibility via UsedFileTokens table.
    /// </summary>
    FileTokenClaims ValidateFileToken(string token);
}

/// <summary>
/// Claims extracted from a validated file token.
/// </summary>
public sealed record FileTokenClaims(
    Guid JobId,
    string DeviceId,
    string Jti
);
