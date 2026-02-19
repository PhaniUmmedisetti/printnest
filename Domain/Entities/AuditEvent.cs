using PrintNest.Domain.Enums;

namespace PrintNest.Domain.Entities;

/// <summary>
/// Immutable record of every significant action in the system.
///
/// Written within the same DB transaction as the action it records.
/// Never updated or deleted — this is an append-only table.
///
/// MetaJson carries context-specific data (e.g., attempt count for OTP failures,
/// device ID for releases). Never contains OTP values or file content.
/// </summary>
public sealed class AuditEvent
{
    /// <summary>Auto-incremented primary key.</summary>
    public long Id { get; init; }

    /// <summary>The job this event relates to.</summary>
    public Guid JobId { get; init; }

    /// <summary>What happened. See AuditEventType for all possible values.</summary>
    public AuditEventType Type { get; init; }

    /// <summary>
    /// Additional context as JSON. Schema varies by event type.
    /// Examples:
    ///   OtpAttemptFailed: { "attemptCount": 3, "deviceId": "dev_store1_abc" }
    ///   JobReleased:      { "deviceId": "dev_store1_abc", "storeId": "store_hyd_001" }
    ///   FileDeleteFailed: { "objectKey": "jobs/uuid.pdf", "error": "connection timeout" }
    /// </summary>
    public string? MetaJson { get; init; }

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}
