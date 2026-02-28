using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Enums;
using PrintNest.Domain.Errors;
using PrintNest.Domain.StateMachine;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Application.Commands;

/// <summary>
/// Device enters OTP at kiosk → job is released to that device.
///
/// This is the most security-critical command in the system.
///
/// Steps:
///   1. Rate limit check: ≤6 attempts per minute across all jobs for this device
///   2. Find job with matching OTP hash — uses constant-time comparison via Argon2id
///   3. Validate OTP: not expired, not locked, not already consumed
///   4. Atomically transition Paid → Released (sets AssignedDeviceId, nulls OTP)
///   5. Issue a short-lived file token (JWT, 120s, single-use)
///   6. Return job summary + file token
///
/// DOUBLE-RELEASE PREVENTION:
///   The UPDATE uses optimistic concurrency — EF Core's concurrency token on Status.
///   If two devices submit the same OTP simultaneously, only one UPDATE succeeds.
///   The other gets a concurrency exception → LOCK_CONFLICT.
///
/// PRIVACY:
///   - Never reveal whether the job exists when OTP is wrong
///   - All failures return the same generic message: "Invalid code."
/// </summary>
public sealed class ReleaseJobCommand
{
    private readonly AppDbContext _db;
    private readonly IOtpService _otp;
    private readonly ITokenService _token;
    private readonly IAuditService _audit;
    private readonly IServiceProvider _services;

    private const int MaxAttemptsPerJob = 6;
    private const int MaxAttemptsPerMinutePerDevice = 6;
    private const int LockDurationMinutes = 30;

    public ReleaseJobCommand(
        AppDbContext db,
        IOtpService otp,
        ITokenService token,
        IAuditService audit,
        IServiceProvider services)
    {
        _db = db;
        _otp = otp;
        _token = token;
        _audit = audit;
        _services = services;
    }

    public sealed record Input(
        string DeviceId,
        string? StoreId,
        string OtpPlaintext
    );

    public sealed record Output(
        Guid JobId,
        string Status,
        JobSummary Summary,
        string FileToken,
        int FileTokenExpiresInSeconds
    );

    public sealed record JobSummary(
        int Copies,
        string Color,
        int PriceCents,
        string Currency
    );

    public async Task<Output> ExecuteAsync(Input input)
    {
        // ── Rate limit: per device per minute ─────────────────────
        // Count recent release attempts from this device in the last 60 seconds
        var oneMinuteAgo = DateTime.UtcNow.AddMinutes(-1);
        // MetaJson is stored as jsonb in Postgres. Filtering by substring on jsonb can
        // translate to unsupported SQL operators, so we narrow in SQL first, then match in memory.
        var recentAttemptMeta = await _db.AuditEvents
            .Where(e =>
                e.Type == AuditEventType.OtpAttemptFailed &&
                e.CreatedAtUtc > oneMinuteAgo &&
                e.MetaJson != null)
            .Select(e => e.MetaJson!)
            .ToListAsync();

        var recentAttempts = recentAttemptMeta.Count(meta => IsOtpFailureForDevice(meta, input.DeviceId));

        if (recentAttempts >= MaxAttemptsPerMinutePerDevice)
            throw new DomainException(ErrorCodes.OtpRateLimited, "Invalid code.", httpStatus: 429);

        // Block release on hard consumable failures so the kiosk does not accept jobs it cannot print.
        var device = await _db.Devices
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DeviceId == input.DeviceId && d.IsActive);

        var blockingCode = GetBlockingAlertCode(device);
        if (blockingCode is not null)
        {
            throw new DomainException(
                ErrorCodes.PrinterNotReady,
                $"Printer not ready ({blockingCode}). Please replace consumables and retry.",
                httpStatus: 409);
        }

        // ── Find a Paid job with a non-expired OTP ─────────────────
        // We load all Paid jobs with active OTPs and check each one.
        // This avoids leaking which job IDs exist via timing differences.
        var candidates = await _db.PrintJobs
            .Where(j =>
                j.Status == JobStatus.Paid &&
                j.OtpHash != null &&
                j.OtpExpiryUtc > DateTime.UtcNow &&
                (j.OtpLockedUntilUtc == null || j.OtpLockedUntilUtc < DateTime.UtcNow))
            .ToListAsync();

        // Check each candidate — Argon2id verify is intentionally slow
        var job = candidates.FirstOrDefault(j =>
            j.OtpHash != null && _otp.Verify(input.OtpPlaintext, j.OtpHash));

        if (job is null)
        {
            // Record the failed attempt in audit (no job reference since we don't know which job)
            // We use a sentinel JobId (all zeros) for device-level failed attempts
            await _audit.RecordAsync(Guid.Empty, AuditEventType.OtpAttemptFailed, new
            {
                deviceId = input.DeviceId,
                reason = "no_matching_otp"
            });
            await _db.SaveChangesAsync();

            throw new DomainException(ErrorCodes.OtpInvalid, "Invalid code.", httpStatus: 400);
        }

        // ── Assign device + store ─────────────────────────────────
        job.AssignedDeviceId = input.DeviceId;
        job.AssignedStoreId = input.StoreId;

        // ── Transition: Paid → Released ───────────────────────────
        // This call also nulls OtpHash (consumes the OTP) via JobStateMachine.ApplyEffects
        JobStateMachine.Transition(job, JobStatus.Released, actor: "device");

        // ── Issue file token ──────────────────────────────────────
        var fileToken = _token.IssueFileToken(job.JobId, input.DeviceId);

        // ── Audit ─────────────────────────────────────────────────
        await _audit.RecordAsync(job.JobId, AuditEventType.OtpConsumed, new
        {
            deviceId = input.DeviceId,
            storeId = input.StoreId
        });
        await _audit.RecordAsync(job.JobId, AuditEventType.JobReleased, new
        {
            deviceId = input.DeviceId,
            storeId = input.StoreId
        });

        // ── Persist (all changes atomic) ──────────────────────────
        var concurrencyHook = _services.GetService<IReleaseConcurrencyTestHook>();
        if (concurrencyHook is not null)
            await concurrencyHook.BeforeSaveAsync(job.JobId, input.DeviceId);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another device claimed this job between our read and write
            throw new DomainException(ErrorCodes.LockConflict, "Invalid code.", httpStatus: 409);
        }

        // ── Parse options for summary ─────────────────────────────
        var summary = ParseOptions(job);

        return new Output(
            job.JobId,
            job.Status.ToString(),
            summary,
            fileToken,
            FileTokenExpiresInSeconds: 120
        );
    }

    private static JobSummary ParseOptions(Domain.Entities.PrintJob job)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(job.OptionsJson ?? "{}");
            var root = doc.RootElement;
            return new JobSummary(
                Copies: root.TryGetProperty("copies", out var c) ? c.GetInt32() : 1,
                Color: root.TryGetProperty("color", out var col) ? col.GetString() ?? "BW" : "BW",
                PriceCents: job.PriceCents,
                Currency: job.Currency
            );
        }
        catch
        {
            return new JobSummary(1, "BW", job.PriceCents, job.Currency);
        }
    }

    private static bool IsOtpFailureForDevice(string metaJson, string deviceId)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metaJson);
            if (!doc.RootElement.TryGetProperty("deviceId", out var did))
                return false;

            return string.Equals(did.GetString(), deviceId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetBlockingAlertCode(Domain.Entities.Device? device)
    {
        if (device is null) return null;
        if (device.PrinterPaperOut is true) return "PAPER_OUT";
        if (device.PrinterCartridgeMissing is true) return "CARTRIDGE_MISSING";
        if (device.PrinterDoorOpen is true) return "DOOR_OPEN";
        if (string.Equals(device.PrinterInkState, "EMPTY", StringComparison.OrdinalIgnoreCase)) return "INK_EMPTY";
        return null;
    }
}
