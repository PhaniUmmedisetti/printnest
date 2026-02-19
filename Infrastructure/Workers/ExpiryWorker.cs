using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintNest.Domain.Enums;
using PrintNest.Domain.StateMachine;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Infrastructure.Workers;

/// <summary>
/// Background worker that automatically expires and fails stuck jobs.
///
/// Runs every 60 seconds. Handles four cases:
///
/// 1. EXPIRY: Paid jobs older than 7 days → Expired
///    (User paid but never came to the store within the job lifetime)
///
/// 2. STUCK IN RELEASED: Released jobs where UpdatedAtUtc > 10 min ago → Expired
///    (Device claimed the job via OTP but never downloaded the file)
///
/// 3. STUCK IN DOWNLOADING/PRINTING: Jobs in these states > 10 min → Failed
///    (Device started but never finished — printer crash, network loss, etc.)
///
/// 4. ABANDONED PRE-PAYMENT: Draft/Uploaded/Quoted jobs older than 24h → Expired
///    (User abandoned after upload but before paying — file must not stay in MinIO forever)
///
/// All transitions are atomic per-job. One failure does not stop the batch.
/// </summary>
public sealed class ExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExpiryWorker> _logger;

    private static readonly TimeSpan RunInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan JobLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan StuckJobTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PrePaymentAbandonTimeout = TimeSpan.FromHours(24);

    public ExpiryWorker(IServiceScopeFactory scopeFactory, ILogger<ExpiryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit on startup to let the app fully initialize
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Log but never crash — worker must keep running
                _logger.LogError(ex, "[ExpiryWorker] Unhandled error during run.");
            }

            await Task.Delay(RunInterval, stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var jobLifetimeCutoff = now - JobLifetime;
        var stuckCutoff = now - StuckJobTimeout;
        var abandonCutoff = now - PrePaymentAbandonTimeout;

        // ── 1. Expire old Paid jobs (7 day job lifetime) ──────────
        var expiredPaidJobs = await db.PrintJobs
            .Where(j => j.Status == JobStatus.Paid && j.CreatedAtUtc < jobLifetimeCutoff)
            .ToListAsync(ct);

        foreach (var job in expiredPaidJobs)
        {
            try
            {
                JobStateMachine.Transition(job, JobStatus.Expired, actor: "worker");
                db.AuditEvents.Add(new Domain.Entities.AuditEvent
                {
                    JobId = job.JobId,
                    Type = AuditEventType.JobExpired,
                    MetaJson = """{"reason":"job_lifetime_exceeded"}""",
                    CreatedAtUtc = now
                });
                _logger.LogInformation("[ExpiryWorker] Job {JobId} expired (7-day lifetime).", job.JobId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ExpiryWorker] Could not expire job {JobId}.", job.JobId);
            }
        }

        // ── 2. Expire stuck Released jobs (device never downloaded) ─
        var stuckReleasedJobs = await db.PrintJobs
            .Where(j => j.Status == JobStatus.Released && j.UpdatedAtUtc < stuckCutoff)
            .ToListAsync(ct);

        foreach (var job in stuckReleasedJobs)
        {
            try
            {
                JobStateMachine.Transition(job, JobStatus.Expired, actor: "worker");
                db.AuditEvents.Add(new Domain.Entities.AuditEvent
                {
                    JobId = job.JobId,
                    Type = AuditEventType.JobExpired,
                    MetaJson = """{"reason":"stuck_in_released"}""",
                    CreatedAtUtc = now
                });
                _logger.LogInformation("[ExpiryWorker] Job {JobId} expired (stuck in Released).", job.JobId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ExpiryWorker] Could not expire stuck Released job {JobId}.", job.JobId);
            }
        }

        // ── 3. Fail stuck Downloading/Printing jobs ───────────────
        var stuckActiveJobs = await db.PrintJobs
            .Where(j =>
                (j.Status == JobStatus.Downloading || j.Status == JobStatus.Printing) &&
                j.UpdatedAtUtc < stuckCutoff)
            .ToListAsync(ct);

        foreach (var job in stuckActiveJobs)
        {
            try
            {
                JobStateMachine.Transition(job, JobStatus.Failed, actor: "worker");
                db.AuditEvents.Add(new Domain.Entities.AuditEvent
                {
                    JobId = job.JobId,
                    Type = AuditEventType.JobFailed,
                    MetaJson = $"{{\"reason\":\"watchdog_timeout\",\"stuckInStatus\":\"{job.Status}\"}}",
                    CreatedAtUtc = now
                });
                _logger.LogWarning("[ExpiryWorker] Job {JobId} failed (watchdog timeout in {Status}).",
                    job.JobId, job.Status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ExpiryWorker] Could not fail stuck job {JobId}.", job.JobId);
            }
        }

        // ── 4. Expire abandoned pre-payment jobs (24h with no activity) ─
        // Draft: created but file never uploaded. Uploaded: file in MinIO but never quoted.
        // Quoted: quoted but user never paid. All have files (or presigned URLs) that need cleanup.
        var abandonedPrePaymentJobs = await db.PrintJobs
            .Where(j =>
                (j.Status == JobStatus.Draft ||
                 j.Status == JobStatus.Uploaded ||
                 j.Status == JobStatus.Quoted) &&
                j.CreatedAtUtc < abandonCutoff)
            .ToListAsync(ct);

        foreach (var job in abandonedPrePaymentJobs)
        {
            try
            {
                JobStateMachine.Transition(job, JobStatus.Expired, actor: "worker");
                db.AuditEvents.Add(new Domain.Entities.AuditEvent
                {
                    JobId = job.JobId,
                    Type = AuditEventType.JobExpired,
                    MetaJson = $"{{\"reason\":\"abandoned_pre_payment\",\"stuckInStatus\":\"{job.Status}\"}}",
                    CreatedAtUtc = now
                });
                _logger.LogInformation("[ExpiryWorker] Job {JobId} expired (abandoned in {Status} after 24h).",
                    job.JobId, job.Status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ExpiryWorker] Could not expire abandoned job {JobId}.", job.JobId);
            }
        }

        var totalProcessed = expiredPaidJobs.Count + stuckReleasedJobs.Count + stuckActiveJobs.Count + abandonedPrePaymentJobs.Count;
        if (totalProcessed > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[ExpiryWorker] Processed {Count} jobs.", totalProcessed);
        }
    }
}
