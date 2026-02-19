using PrintNest.Domain.Enums;

namespace PrintNest.Application.Interfaces;

/// <summary>
/// Records audit events to the AuditEvents table.
///
/// Always call within the same DbContext transaction as the action being audited.
/// This ensures the event is never written without the action, and never lost if the action rolls back.
///
/// Implementation: Infrastructure/Persistence/AuditService.cs (simple EF Core insert — no async queue).
///
/// SECURITY: MetaJson must NEVER contain OTP values, file content, or SharedSecrets.
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Records an audit event. Call before SaveChangesAsync() so it's in the same transaction.
    /// </summary>
    /// <param name="jobId">The job this event relates to.</param>
    /// <param name="type">Event type from AuditEventType enum.</param>
    /// <param name="meta">
    /// Optional context object. Will be serialized to JSON.
    /// Example: new { deviceId = "dev_abc", attemptCount = 3 }
    /// Never include sensitive fields (OTP, secrets, file bytes).
    /// </param>
    Task RecordAsync(Guid jobId, AuditEventType type, object? meta = null);
}
