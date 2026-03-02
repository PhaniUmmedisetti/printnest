namespace PrintNest.Domain.Enums;

/// <summary>
/// All audit event types written to the AuditEvents table.
///
/// Every significant action in the system produces an audit event.
/// Events are written within the same transaction as the action â€” they are never lost.
///
/// To add a new event type: add it here and call IAuditService.RecordAsync() at the relevant point.
/// </summary>
public enum AuditEventType
{
    // â”€â”€ Job lifecycle â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    JobCreated,
    UploadFinalized,
    JobQuoted,
    JobPaid,

    // â”€â”€ OTP â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    OtpGenerated,
    OtpAttemptFailed,   // wrong code entered â€” meta contains attempt count
    OtpConsumed,        // correct code entered â€” OTP nulled, job released

    // â”€â”€ Device actions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    JobReleased,        // device claimed job via OTP
    FileDownloadStarted,
    PrintingStarted,
    JobCompleted,
    JobFailed,

    // â”€â”€ Background worker actions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    JobExpired,
    FileDeleted,
    FileDeleteFailed,   // MinIO delete failed â€” DeletePending set to true

    // â”€â”€ Device management â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    DeviceRegistered,
    DeviceHeartbeat,
    DeviceDeactivated,

    // â”€â”€ Store management â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    StoreCreated,
    StoreUpdated
}
