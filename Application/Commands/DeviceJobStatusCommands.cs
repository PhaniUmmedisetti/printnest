using Microsoft.EntityFrameworkCore;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Enums;
using PrintNest.Domain.Errors;
using PrintNest.Domain.StateMachine;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Application.Commands;

/// <summary>
/// Groups the three device-side job status update commands:
///   - MarkDownloadingCommand   (Released → Downloading)
///   - MarkPrintingCommand      (Downloading → Printing)
///   - CompleteJobCommand       (Printing → Completed)
///   - FailJobCommand           (Printing → Failed)
///
/// All commands enforce that the requesting device owns the job (AssignedDeviceId match).
/// </summary>

// ── Released → Downloading ────────────────────────────────────────────────────

/// <summary>
/// Called when the device starts downloading the file via the file token.
/// Transitions Released → Downloading.
/// This is called automatically by the file streaming endpoint — device does not call this directly.
/// </summary>
public sealed class MarkDownloadingCommand
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;

    public MarkDownloadingCommand(AppDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task ExecuteAsync(Guid jobId, string deviceId)
    {
        var job = await _db.PrintJobs.FirstOrDefaultAsync(j => j.JobId == jobId)
            ?? throw new DomainException(ErrorCodes.JobNotFound, "Job not found.", httpStatus: 404);

        DeviceOwnershipGuard.Enforce(job, deviceId);

        JobStateMachine.Transition(job, JobStatus.Downloading, actor: "device");

        await _audit.RecordAsync(job.JobId, AuditEventType.FileDownloadStarted, new { deviceId });
        await _db.SaveChangesAsync();
    }
}

// ── Downloading → Printing ────────────────────────────────────────────────────

/// <summary>
/// Called by device when it submits the CUPS print job.
/// Transitions Downloading → Printing.
/// </summary>
public sealed class MarkPrintingCommand
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;

    public MarkPrintingCommand(AppDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public sealed record Input(Guid JobId, string DeviceId, string? CupsJobId, string? PrinterName);

    public async Task ExecuteAsync(Input input)
    {
        var job = await _db.PrintJobs.FirstOrDefaultAsync(j => j.JobId == input.JobId)
            ?? throw new DomainException(ErrorCodes.JobNotFound, "Job not found.", httpStatus: 404);

        DeviceOwnershipGuard.Enforce(job, input.DeviceId);

        JobStateMachine.Transition(job, JobStatus.Printing, actor: "device");

        await _audit.RecordAsync(job.JobId, AuditEventType.PrintingStarted, new
        {
            deviceId = input.DeviceId,
            cupsJobId = input.CupsJobId,
            printerName = input.PrinterName
        });
        await _db.SaveChangesAsync();
    }
}

// ── Printing → Completed ──────────────────────────────────────────────────────

/// <summary>
/// Called by device when CUPS reports the print job completed successfully.
/// Transitions Printing → Completed.
/// The cleanup worker will then delete the file from MinIO and move to Deleted.
/// </summary>
public sealed class CompleteJobCommand
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;

    public CompleteJobCommand(AppDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public sealed record Input(Guid JobId, string DeviceId, string? CupsJobId, object? Metrics);

    public async Task ExecuteAsync(Input input)
    {
        var job = await _db.PrintJobs.FirstOrDefaultAsync(j => j.JobId == input.JobId)
            ?? throw new DomainException(ErrorCodes.JobNotFound, "Job not found.", httpStatus: 404);

        DeviceOwnershipGuard.Enforce(job, input.DeviceId);

        JobStateMachine.Transition(job, JobStatus.Completed, actor: "device");

        await _audit.RecordAsync(job.JobId, AuditEventType.JobCompleted, new
        {
            deviceId = input.DeviceId,
            cupsJobId = input.CupsJobId
            // metrics intentionally omitted — may contain timing data that indirectly identifies user
        });
        await _db.SaveChangesAsync();
    }
}

// ── Printing → Failed ─────────────────────────────────────────────────────────

/// <summary>
/// Called by device when the print job fails (paper jam, printer error, CUPS failure).
/// Transitions Printing → Failed.
/// The cleanup worker will delete the file from MinIO and move to Deleted.
/// </summary>
public sealed class FailJobCommand
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;

    public FailJobCommand(AppDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public sealed record Input(
        Guid JobId,
        string DeviceId,
        string? CupsJobId,
        string? FailureCode,
        string? FailureMessage,
        bool IsRetryable
    );

    public async Task ExecuteAsync(Input input)
    {
        var job = await _db.PrintJobs.FirstOrDefaultAsync(j => j.JobId == input.JobId)
            ?? throw new DomainException(ErrorCodes.JobNotFound, "Job not found.", httpStatus: 404);

        DeviceOwnershipGuard.Enforce(job, input.DeviceId);

        JobStateMachine.Transition(job, JobStatus.Failed, actor: "device");

        await _audit.RecordAsync(job.JobId, AuditEventType.JobFailed, new
        {
            deviceId = input.DeviceId,
            cupsJobId = input.CupsJobId,
            failureCode = input.FailureCode,
            failureMessage = input.FailureMessage,
            isRetryable = input.IsRetryable
        });
        await _db.SaveChangesAsync();
    }
}

// ── Shared helper ─────────────────────────────────────────────────────────────

/// <summary>
/// Ensures the requesting device is the one assigned to a job at release time.
/// Prevents one device from updating another device's job.
/// </summary>
internal static class DeviceOwnershipGuard
{
    internal static void Enforce(Domain.Entities.PrintJob job, string deviceId)
    {
        if (!string.Equals(job.AssignedDeviceId, deviceId, StringComparison.Ordinal))
            throw new DomainException(
                ErrorCodes.DeviceUnauthorized,
                "This job is not assigned to your device.",
                httpStatus: 403
            );
    }
}
