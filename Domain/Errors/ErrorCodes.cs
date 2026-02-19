namespace PrintNest.Domain.Errors;

/// <summary>
/// All error codes returned by the API in the standard error envelope:
/// { "error": { "code": "...", "message": "..." } }
///
/// These are machine-readable strings for clients and frontend to switch on.
/// Never change an existing code value — it is a breaking change for all clients.
/// Add new codes freely.
/// </summary>
public static class ErrorCodes
{
    // ── OTP errors ────────────────────────────────────────────────
    /// <summary>OTP is wrong, already used, or job does not exist. Deliberately generic — never reveal job existence.</summary>
    public const string OtpInvalid = "OTP_INVALID";

    /// <summary>OTP has passed its 6-hour expiry window.</summary>
    public const string OtpExpired = "OTP_EXPIRED";

    /// <summary>Too many OTP attempts in the allowed window.</summary>
    public const string OtpRateLimited = "OTP_RATE_LIMITED";

    /// <summary>Job has been locked after too many failed OTP attempts.</summary>
    public const string OtpLocked = "OTP_LOCKED";

    // ── Job / state machine errors ────────────────────────────────
    /// <summary>Requested transition is not allowed from the job's current state.</summary>
    public const string JobStateInvalid = "JOB_STATE_INVALID";

    /// <summary>Job was not found. Only returned on user-facing endpoints — never on device OTP endpoints.</summary>
    public const string JobNotFound = "JOB_NOT_FOUND";

    // ── Device auth errors ────────────────────────────────────────
    /// <summary>Device is unknown, signature is invalid, or timestamp is out of range. Deliberately generic.</summary>
    public const string DeviceUnauthorized = "DEVICE_UNAUTHORIZED";

    // ── File token errors ─────────────────────────────────────────
    /// <summary>File token signature is invalid or the token is malformed.</summary>
    public const string TokenInvalid = "TOKEN_INVALID";

    /// <summary>File token has expired (120-second TTL).</summary>
    public const string TokenExpired = "TOKEN_EXPIRED";

    /// <summary>File token has already been used (single-use enforcement via JTI table).</summary>
    public const string TokenAlreadyUsed = "TOKEN_ALREADY_USED";

    // ── Storage errors ────────────────────────────────────────────
    /// <summary>MinIO operation failed (upload not found, delete failed, stream error).</summary>
    public const string StorageError = "STORAGE_ERROR";

    // ── Concurrency errors ────────────────────────────────────────
    /// <summary>Two devices attempted to release the same job simultaneously. First one wins.</summary>
    public const string LockConflict = "LOCK_CONFLICT";

    // ── Validation errors ─────────────────────────────────────────
    /// <summary>Request body or parameters failed validation.</summary>
    public const string ValidationError = "VALIDATION_ERROR";

    // ── Admin errors ──────────────────────────────────────────────
    /// <summary>Admin API key is missing or incorrect.</summary>
    public const string AdminUnauthorized = "ADMIN_UNAUTHORIZED";
}
