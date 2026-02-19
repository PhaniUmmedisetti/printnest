using System.Text.Json;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Entities;
using PrintNest.Domain.Enums;

namespace PrintNest.Infrastructure.Persistence;

/// <summary>
/// Writes audit events to the AuditEvents table via EF Core.
///
/// Calls must happen within the same DbContext unit of work (before SaveChangesAsync).
/// This ensures audit events are written atomically with the action they record.
///
/// Never call SaveChangesAsync() inside this service — leave that to the command handler.
/// </summary>
public sealed class AuditService : IAuditService
{
    private readonly AppDbContext _db;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AuditService(AppDbContext db) => _db = db;

    public Task RecordAsync(Guid jobId, AuditEventType type, object? meta = null)
    {
        var ev = new AuditEvent
        {
            JobId = jobId,
            Type = type,
            MetaJson = meta is null ? null : JsonSerializer.Serialize(meta, JsonOptions),
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.AuditEvents.Add(ev);

        // Intentionally synchronous add — EF tracks it, actual write happens with SaveChangesAsync
        return Task.CompletedTask;
    }
}
