using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Enums;
using PrintNest.Domain.Errors;
using PrintNest.Domain.StateMachine;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Application.Commands;

/// <summary>
/// Device enters OTP at kiosk and the matching job is released to that device.
///
/// This is the most security-critical command in the system.
///
/// Steps:
///   1. Rate limit check: at most 6 attempts per minute across all jobs for this device
///   2. Find job with matching OTP hash using constant-time Argon2 verification
///   3. Validate OTP: paid jobs use the normal 6-hour expiry; retryable failed jobs may reuse the same OTP
///   4. Atomically transition Paid/Failed to Released and bind the job to this device
///   5. Issue a short-lived file token (JWT, 120s, single-use)
///   6. Return job summary plus file token
///
/// DOUBLE-RELEASE PREVENTION:
///   The UPDATE uses optimistic concurrency via the Status concurrency token.
///   If two devices submit the same OTP simultaneously, only one UPDATE succeeds.
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

    private const int MaxAttemptsPerMinutePerDevice = 6;

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
        var now = DateTime.UtcNow;

        // Count recent failed release attempts from this device in the last 60 seconds.
        var oneMinuteAgo = now.AddMinutes(-1);
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

        // Block release when the printer cannot physically accept the job.
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

        // Paid jobs need an unexpired OTP. Retryable failed jobs may reuse the same OTP
        // until the overall 7-day job lifetime expires.
        var jobLifetimeCutoff = now.AddDays(-7);
        var candidates = await _db.PrintJobs
            .Where(j =>
                j.OtpHash != null &&
                j.CreatedAtUtc > jobLifetimeCutoff &&
                (
                    (j.Status == JobStatus.Paid && j.OtpExpiryUtc > now) ||
                    (j.Status == JobStatus.Failed && j.RetryAllowed)
                ) &&
                (j.OtpLockedUntilUtc == null || j.OtpLockedUntilUtc < now))
            .ToListAsync();

        var job = candidates.FirstOrDefault(j =>
            j.OtpHash != null && _otp.Verify(input.OtpPlaintext, j.OtpHash));

        if (job is null)
        {
            await _audit.RecordAsync(Guid.Empty, AuditEventType.OtpAttemptFailed, new
            {
                deviceId = input.DeviceId,
                reason = "no_matching_otp"
            });
            await _db.SaveChangesAsync();

            throw new DomainException(ErrorCodes.OtpInvalid, "Invalid code.", httpStatus: 400);
        }

        if (string.IsNullOrWhiteSpace(job.ObjectKey))
            throw new DomainException(
                ErrorCodes.StorageError,
                "Job file is unavailable for release.",
                httpStatus: 409
            );

        job.AssignedDeviceId = input.DeviceId;
        job.AssignedStoreId = input.StoreId;
        JobStateMachine.Transition(job, JobStatus.Released, actor: "device");

        var fileToken = _token.IssueFileToken(job.JobId, input.DeviceId);

        await _audit.RecordAsync(job.JobId, AuditEventType.JobReleased, new
        {
            deviceId = input.DeviceId,
            storeId = input.StoreId
        });

        var concurrencyHook = _services.GetService<IReleaseConcurrencyTestHook>();
        if (concurrencyHook is not null)
            await concurrencyHook.BeforeSaveAsync(job.JobId, input.DeviceId);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(ErrorCodes.LockConflict, "Invalid code.", httpStatus: 409);
        }

        return new Output(
            job.JobId,
            job.Status.ToString(),
            ParseOptions(job),
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
                Copies: root.TryGetProperty("copies", out var copies) ? copies.GetInt32() : 1,
                Color: root.TryGetProperty("color", out var color) ? color.GetString() ?? "BW" : "BW",
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
