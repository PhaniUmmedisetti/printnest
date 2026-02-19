using Microsoft.EntityFrameworkCore;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Enums;
using PrintNest.Domain.Errors;
using PrintNest.Domain.StateMachine;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Application.Commands;

/// <summary>
/// Confirms mock payment and transitions the job to Paid state.
/// Does NOT generate an OTP — OTP is generated on-demand when the user clicks "Generate OTP".
///
/// In MVP this is a no-op payment (no real payment gateway).
/// When Razorpay is integrated, this command will verify the payment signature before transitioning.
///
/// Steps:
///   1. Load job — must be in Quoted state
///   2. Transition Quoted → Paid
///   3. Save
/// </summary>
public sealed class PayJobCommand
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;

    public PayJobCommand(AppDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public sealed record Output(
        string Status,
        int PriceCents,
        string Currency
    );

    public async Task<Output> ExecuteAsync(Guid jobId)
    {
        var job = await _db.PrintJobs.FirstOrDefaultAsync(j => j.JobId == jobId)
            ?? throw new DomainException(ErrorCodes.JobNotFound, "Job not found.", httpStatus: 404);

        JobStateMachine.Transition(job, JobStatus.Paid, actor: "user");

        await _audit.RecordAsync(job.JobId, AuditEventType.JobPaid, new
        {
            priceCents = job.PriceCents,
            currency = job.Currency
        });

        await _db.SaveChangesAsync();

        return new Output(job.Status.ToString(), job.PriceCents, job.Currency);
    }
}
