namespace PrintNest.Application.Interfaces;

/// <summary>
/// Generates and verifies OTPs for print job release.
///
/// OTPs are 6-digit numeric codes, hashed with Argon2id before storage.
/// The plaintext OTP is returned to the caller once and never stored.
///
/// Implementation: Infrastructure/Auth/OtpService.cs
/// </summary>
public interface IOtpService
{
    /// <summary>
    /// Generates a new cryptographically random 6-digit OTP.
    ///
    /// Returns both the plaintext (to show the user ONCE) and the Argon2id hash (to store in DB).
    /// The plaintext must never be stored anywhere after this call.
    /// </summary>
    OtpResult Generate();

    /// <summary>
    /// Verifies a plaintext OTP against a stored Argon2id hash.
    ///
    /// Returns true if the code matches.
    /// This is a slow operation by design (Argon2id) — do not call in a tight loop.
    /// </summary>
    bool Verify(string plaintext, string storedHash);
}

/// <summary>
/// Result of OTP generation. Plaintext is shown to user once; hash is stored in DB.
/// </summary>
public sealed record OtpResult(
    /// <summary>6-digit numeric code to show to the user. Never store this.</summary>
    string Plaintext,

    /// <summary>Argon2id hash to store in PrintJob.OtpHash.</summary>
    string Hash
);
