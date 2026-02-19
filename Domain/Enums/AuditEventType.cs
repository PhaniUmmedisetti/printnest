namespace PrintNest.Domain.Enums;

/// <summary>
/// All audit event types written to the AuditEvents table.
///
/// Every significant action in the system produces an audit event.
/// Events are written within the same transaction as the action — they are never lost.
///
/// To add a new event type: add it here and call IAuditService.RecordAsync() at the relevant point.
/// </summary>
public enum AuditEventType
{
    // ── Job lifecycle ──────────────────────────────────────────────
    JobCreated,
    UploadFinalized,
    JobQuoted,
    JobPaid,

    // ── OTP ───────────────────────────────────────────────────────
    OtpGenerated,
    OtpAttemptFailed,   // wrong code entered — meta contains attempt count
    OtpConsumed,        // correct code entered — OTP nulled, job released

    // ── Device actions ────────────────────────────────────────────
    JobReleased,        // device claimed job via OTP
    FileDownloadStarted,
    PrintingStarted,
    JobCompleted,
    JobFailed,

    // ── Background worker actions ─────────────────────────────────
    JobExpired,
    FileDeleted,
    FileDeleteFailed,   // MinIO delete failed — DeletePending set to true

    // ── Device management ─────────────────────────────────────────
    DeviceRegistered,
    DeviceHeartbeat,
    DeviceDeactivated,

    // ── Store management ──────────────────────────────────────────
    StoreCreated,
    StoreUpdated
}
