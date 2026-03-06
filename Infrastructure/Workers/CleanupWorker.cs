using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Enums;
using PrintNest.Domain.StateMachine;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Infrastructure.Workers;

/// <summary>
/// Background worker that deletes files from MinIO for terminal jobs and cleans up stale DB records.
///
/// Runs every 60 seconds. Handles two cases:
///
/// 1. FILE DELETION: Jobs in Completed/Expired/non-retryable Failed with DeletedAtUtc = null
///    → Delete from MinIO
///    → If success: transition to Deleted
///    → If fail: set DeletePending = true, log error, retry next run
///
/// 2. JTI CLEANUP: UsedFileTokens rows older than 24 hours → delete
///    (Tokens expire in 120s — 24h rows are pure dead weight)
///
/// This worker ensures no file stays in storage indefinitely.
/// Even if the device never calls /completed, the expiry worker moves jobs to Expired,
/// and this worker then deletes the file.
/// </summary>
public sealed class CleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CleanupWorker> _logger;

    private static readonly TimeSpan RunInterval = TimeSpan.FromMinutes(1);

    public CleanupWorker(IServiceScopeFactory scopeFactory, ILogger<CleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // offset from ExpiryWorker

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[CleanupWorker] Unhandled error during run.");
            }

            await Task.Delay(RunInterval, stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IStorageService>();

        var now = DateTime.UtcNow;

        // ── 1. Delete files for terminal jobs ─────────────────────
        // Include jobs with DeletePending = true (previous delete failed).
        // Retryable Failed jobs are intentionally excluded so the customer can regenerate
        // a fresh OTP without losing the original uploaded file.
        var jobsToDelete = await db.PrintJobs
            .Where(j =>
                j.DeletedAtUtc == null &&
                j.ObjectKey != null &&
                (j.Status == JobStatus.Completed ||
                 (j.Status == JobStatus.Failed && !j.RetryAllowed) ||
                 j.Status == JobStatus.Expired))
            .ToListAsync(ct);

        foreach (var job in jobsToDelete)
        {
            try
            {
                await storage.DeleteFileAsync(job.ObjectKey!);

                // Transition to Deleted — this sets DeletedAtUtc via ApplyEffects
                JobStateMachine.Transition(job, JobStatus.Deleted, actor: "worker");
                job.DeletePending = false;

                db.AuditEvents.Add(new Domain.Entities.AuditEvent
                {
                    JobId = job.JobId,
                    Type = AuditEventType.FileDeleted,
                    MetaJson = null, // Never log object key — it contains the job ID but let's keep it clean
                    CreatedAtUtc = now
                });

                _logger.LogInformation("[CleanupWorker] Deleted file and moved job {JobId} to Deleted.", job.JobId);
            }
            catch (Exception ex)
            {
                // Don't crash the batch — mark as pending for retry
                job.DeletePending = true;
                job.UpdatedAtUtc = now;

                db.AuditEvents.Add(new Domain.Entities.AuditEvent
                {
                    JobId = job.JobId,
                    Type = AuditEventType.FileDeleteFailed,
                    MetaJson = $"{{\"error\":\"{ex.Message.Replace("\"", "\\\"")}\"}}",
                    CreatedAtUtc = now
                });

                _logger.LogError(ex, "[CleanupWorker] Failed to delete file for job {JobId}. Marked DeletePending.", job.JobId);
            }
        }

        if (jobsToDelete.Count > 0)
            await db.SaveChangesAsync(ct);

        // ── 2. Clean up stale JTI records (older than 24h) ────────
        var jtiCutoff = now.AddHours(-24);
        var deletedJtis = await db.UsedFileTokens
            .Where(t => t.UsedAtUtc < jtiCutoff)
            .ExecuteDeleteAsync(ct);

        if (deletedJtis > 0)
            _logger.LogInformation("[CleanupWorker] Cleaned up {Count} expired JTI records.", deletedJtis);
    }
}
