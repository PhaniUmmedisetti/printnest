using Microsoft.EntityFrameworkCore;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Enums;
using PrintNest.Domain.Errors;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Application.Commands;

/// <summary>
/// Generates the initial OTP for a paid job.
///
/// The user can click "Generate OTP" after payment.
/// Each click invalidates the previous OTP and issues a new one.
/// The OTP is valid for 6 hours from generation time.
///
/// This does NOT change the job's state — the job remains in Paid.
/// The OTP remains attached to the job until successful completion or terminal cleanup.
/// Retryable failed jobs reuse the same OTP rather than generating a fresh one.
///
/// Steps:
///   1. Load job — must be in Paid state
///   2. Generate new OTP (plaintext + hash)
///   3. Store hash + expiry, reset attempt counters
///   4. Return plaintext OTP to caller (shown to user ONCE — never stored)
/// </summary>
public sealed class GenerateOtpCommand
{
    private readonly AppDbContext _db;
    private readonly IOtpService _otp;
    private readonly IAuditService _audit;

    private static readonly TimeSpan OtpValidity = TimeSpan.FromHours(6);

    public GenerateOtpCommand(AppDbContext db, IOtpService otp, IAuditService audit)
    {
        _db = db;
        _otp = otp;
        _audit = audit;
    }

    public sealed record Output(
        string Otp,             // plaintext — show to user once
        DateTime ExpiresAtUtc
    );

    public async Task<Output> ExecuteAsync(Guid jobId)
    {
        var job = await _db.PrintJobs.FirstOrDefaultAsync(j => j.JobId == jobId)
            ?? throw new DomainException(ErrorCodes.JobNotFound, "Job not found.", httpStatus: 404);

        if (job.Status != JobStatus.Paid)
            throw new DomainException(
                ErrorCodes.JobStateInvalid,
                "OTP can only be generated for paid jobs.",
                httpStatus: 409
            );

        // Generate initial OTP for the paid job.
        var result = _otp.Generate();
        var expiresAt = DateTime.UtcNow.Add(OtpValidity);

        // Store hash (NEVER the plaintext)
        job.OtpHash = result.Hash;
        job.OtpExpiryUtc = expiresAt;

        // Reset attempt counters — new OTP = fresh start
        job.OtpAttempts = 0;
        job.OtpLastAttemptUtc = null;
        job.OtpLockedUntilUtc = null;
        job.UpdatedAtUtc = DateTime.UtcNow;

        await _audit.RecordAsync(job.JobId, AuditEventType.OtpGenerated, new
        {
            expiresAtUtc = expiresAt
            // Never log the OTP plaintext or hash
        });

        await _db.SaveChangesAsync();

        return new Output(result.Plaintext, expiresAt);
    }
}
