using PrintNest.Domain.Entities;
using PrintNest.Domain.Enums;
using PrintNest.Domain.Errors;

namespace PrintNest.Domain.StateMachine;

/// <summary>
/// Enforces all PrintJob state transitions.
///
/// THIS IS THE ONLY PLACE where job status changes are authorized.
/// No other class may set PrintJob.Status directly.
///
/// How to use:
///   JobStateMachine.Transition(job, JobStatus.Uploaded, actor: "user");
///   // If transition is invalid, throws DomainException(ErrorCodes.JobStateInvalid)
///   // If valid, sets job.Status and job.UpdatedAtUtc
///
/// How to add a new transition:
///   1. Add the new JobStatus value in Domain/Enums/JobStatus.cs
///   2. Add the (From, To) pair to AllowedTransitions below
///   3. If the transition has guards (extra conditions), add them in ApplyGuards()
///   4. If the transition sets extra fields, add them in ApplyEffects()
/// </summary>
public static class JobStateMachine
{
    /// <summary>
    /// Complete list of valid (from → to) transitions.
    /// Any transition not in this set is rejected.
    ///
    /// Actor legend:
    ///   "user"    — customer via public API endpoints
    ///   "device"  — kiosk/Pi via device API endpoints
    ///   "worker"  — background worker (no HTTP actor)
    /// </summary>
    private static readonly HashSet<(JobStatus From, JobStatus To)> AllowedTransitions = new()
    {
        // ── User-triggered ────────────────────────────────────────
        (JobStatus.Draft,        JobStatus.Uploaded),    // user: finalize upload
        (JobStatus.Uploaded,     JobStatus.Quoted),      // user: request quote
        (JobStatus.Quoted,       JobStatus.Paid),        // user: mock payment

        // ── Device-triggered ──────────────────────────────────────
        (JobStatus.Paid,         JobStatus.Released),    // device: OTP validated
        (JobStatus.Released,     JobStatus.Downloading), // device: started file download
        (JobStatus.Released,     JobStatus.Failed),      // device/worker: interrupted before download completed
        (JobStatus.Downloading,  JobStatus.Printing),    // device: file received, CUPS job submitted
        (JobStatus.Downloading,  JobStatus.Failed),      // device/worker: download interrupted
        (JobStatus.Printing,     JobStatus.Completed),   // device: CUPS reported success
        (JobStatus.Printing,     JobStatus.Failed),      // device: CUPS reported failure
        (JobStatus.Failed,       JobStatus.Released),    // device: retryable failed job re-released with same OTP

        // ── Background worker only ────────────────────────────────
        (JobStatus.Draft,        JobStatus.Expired),     // worker: abandoned before upload (24h)
        (JobStatus.Uploaded,     JobStatus.Expired),     // worker: uploaded but never quoted/paid (24h)
        (JobStatus.Quoted,       JobStatus.Expired),     // worker: quoted but never paid (24h)
        (JobStatus.Paid,         JobStatus.Expired),     // worker: 7-day job lifetime exceeded
        (JobStatus.Released,     JobStatus.Expired),     // worker: legacy fallback for stale released jobs
        (JobStatus.Downloading,  JobStatus.Failed),      // worker: stuck in Downloading > 10 min
        (JobStatus.Printing,     JobStatus.Failed),      // worker: stuck in Printing > 10 min (watchdog)
        (JobStatus.Failed,       JobStatus.Expired),     // worker: retry window/job lifetime exceeded
        (JobStatus.Completed,    JobStatus.Deleted),     // worker: file deleted from MinIO
        (JobStatus.Failed,       JobStatus.Deleted),     // worker: file deleted from MinIO
        (JobStatus.Expired,      JobStatus.Deleted),     // worker: file deleted from MinIO
    };

    /// <summary>
    /// Validates and applies a state transition to a PrintJob.
    ///
    /// On success: sets job.Status = to, job.UpdatedAtUtc = UtcNow, and any side-effect fields.
    /// On failure: throws DomainException — never returns silently.
    /// </summary>
    /// <param name="job">The job to transition. Modified in-place.</param>
    /// <param name="to">The target state.</param>
    /// <param name="actor">Who is triggering this — "user", "device", or "worker". Used in error context.</param>
    public static void Transition(PrintJob job, JobStatus to, string actor = "unknown")
    {
        var from = job.Status;

        // ── Guard: is this transition in the allowed set? ─────────
        if (!AllowedTransitions.Contains((from, to)))
        {
            throw new DomainException(
                ErrorCodes.JobStateInvalid,
                $"Cannot transition job {job.JobId} from {from} to {to}. Actor: {actor}.",
                httpStatus: 409
            );
        }

        // ── Guard: additional business rule checks per transition ──
        ApplyGuards(job, from, to, actor);

        // ── Apply: set new status + side-effect fields ─────────────
        job.Status = to;
        job.UpdatedAtUtc = DateTime.UtcNow;
        ApplyEffects(job, to);
    }

    /// <summary>
    /// Additional business rule guards that go beyond "is the transition allowed?".
    ///
    /// Example: transitioning to Released requires OtpHash to be present (can't release without OTP).
    /// Add new guards here when a transition has preconditions beyond state alone.
    /// </summary>
    private static void ApplyGuards(PrintJob job, JobStatus from, JobStatus to, string actor)
    {
        switch (to)
        {
            case JobStatus.Released:
                // OTP must exist at release time.
                if (job.OtpHash is null)
                    throw new DomainException(
                        ErrorCodes.OtpInvalid,
                        "Invalid code.",
                        httpStatus: 400
                    );

                // Fresh releases from Paid still respect the normal OTP expiry window.
                // Retryable failed jobs intentionally keep the same OTP alive until
                // completion/expiry of the whole job, so we skip the 6-hour OTP expiry there.
                if (from == JobStatus.Paid &&
                    job.OtpExpiryUtc.HasValue &&
                    job.OtpExpiryUtc.Value < DateTime.UtcNow)
                    throw new DomainException(
                        ErrorCodes.OtpExpired,
                        "Invalid code.",   // deliberately generic — don't reveal expiry to attacker
                        httpStatus: 400
                    );

                if (from == JobStatus.Failed && !job.RetryAllowed)
                    throw new DomainException(
                        ErrorCodes.OtpInvalid,
                        "Invalid code.",
                        httpStatus: 400
                    );

                // Job must not be locked due to too many failed attempts
                if (job.OtpLockedUntilUtc.HasValue && job.OtpLockedUntilUtc.Value > DateTime.UtcNow)
                    throw new DomainException(
                        ErrorCodes.OtpLocked,
                        "Invalid code.",
                        httpStatus: 400
                    );
                break;

            case JobStatus.Uploaded:
                // Object key must be set before we can finalize
                if (string.IsNullOrEmpty(job.ObjectKey))
                    throw new DomainException(
                        ErrorCodes.JobStateInvalid,
                        "Cannot finalize: no object key assigned to this job.",
                        httpStatus: 400
                    );
                break;

            case JobStatus.Quoted:
                // Options must be set before we can quote
                if (string.IsNullOrEmpty(job.OptionsJson))
                    throw new DomainException(
                        ErrorCodes.JobStateInvalid,
                        "Cannot quote: print options have not been set.",
                        httpStatus: 400
                    );
                break;
        }
    }

    /// <summary>
    /// Side effects applied automatically when entering a new state.
    /// These keep derived fields consistent with the current status.
    ///
    /// Add new effects here when a state transition should automatically set/clear fields.
    /// </summary>
    private static void ApplyEffects(PrintJob job, JobStatus newStatus)
    {
        switch (newStatus)
        {
            case JobStatus.Released:
                // Record when the release lock was acquired
                job.ReleaseLockUtc = DateTime.UtcNow;
                // OTP remains valid until successful completion or terminal cleanup
                job.OtpAttempts = 0;
                job.OtpLockedUntilUtc = null;
                job.RetryAllowed = false;
                break;

            case JobStatus.Expired:
                // Clear OTP fields — expired jobs can never be released
                job.OtpHash = null;
                job.OtpExpiryUtc = null;
                job.OtpLockedUntilUtc = null;
                job.RetryAllowed = false;
                job.AssignedDeviceId = null;
                job.AssignedStoreId = null;
                job.ReleaseLockUtc = null;
                break;

            case JobStatus.Completed:
                job.OtpHash = null;
                job.OtpExpiryUtc = null;
                job.OtpAttempts = 0;
                job.OtpLockedUntilUtc = null;
                job.RetryAllowed = false;
                job.PrintedAtUtc = DateTime.UtcNow;
                break;

            case JobStatus.Deleted:
                job.RetryAllowed = false;
                job.DeletedAtUtc = DateTime.UtcNow;
                break;
        }
    }

    /// <summary>
    /// Returns true if the given transition is valid from the current job state.
    /// Use this for read-only checks — does not modify the job or throw.
    /// </summary>
    public static bool CanTransition(PrintJob job, JobStatus to)
        => AllowedTransitions.Contains((job.Status, to));
}
