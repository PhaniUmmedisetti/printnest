using Microsoft.EntityFrameworkCore;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Enums;
using PrintNest.Domain.Errors;
using PrintNest.Domain.StateMachine;
using PrintNest.Infrastructure.Persistence;
using System.Text.Json;

namespace PrintNest.Application.Commands;

/// <summary>
/// Calculates the price for a job based on the selected print options.
///
/// Pricing logic (MVP — configurable per store in future):
///   B&W:   ₹2 per page × copies
///   Color: not available in MVP (disabled in UI, rejected here)
///
/// Steps:
///   1. Load job — must be in Uploaded state
///   2. Validate options
///   3. Calculate price
///   4. Store OptionsJson + PriceCents
///   5. Transition Uploaded → Quoted
/// </summary>
public sealed class QuoteJobCommand
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;

    // Pricing constants — will be moved to per-store config in future
    private const int BwPricePerPagePaise = 200;  // ₹2.00 in paise
    private const int MinimumChargePaise = 500;   // ₹5.00 minimum

    public QuoteJobCommand(AppDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public sealed record PrintOptions(
        int Copies,
        string Color  // "BW" only in MVP. "COLOR" is rejected.
    );

    public sealed record Input(Guid JobId, PrintOptions Options);

    public sealed record Output(
        string Status,
        int TotalAmountCents,
        string Currency
    );

    public async Task<Output> ExecuteAsync(Input input)
    {
        var job = await _db.PrintJobs.FirstOrDefaultAsync(j => j.JobId == input.JobId)
            ?? throw new DomainException(ErrorCodes.JobNotFound, "Job not found.", httpStatus: 404);

        // ── Validate options ──────────────────────────────────────
        if (input.Options.Copies < 1 || input.Options.Copies > 100)
            throw new DomainException(
                ErrorCodes.ValidationError,
                "Copies must be between 1 and 100.",
                httpStatus: 422
            );

        if (!string.Equals(input.Options.Color, "BW", StringComparison.OrdinalIgnoreCase))
            throw new DomainException(
                ErrorCodes.ValidationError,
                "Color printing is coming soon. Please select B&W.",
                httpStatus: 422
            );

        // ── Calculate price ───────────────────────────────────────
        // In MVP we don't know page count yet (file is in MinIO, not inspected server-side).
        // Price is calculated as: copies × per-copy flat rate.
        // Page count will be added when PDF inspection is implemented.
        var totalPaise = Math.Max(input.Options.Copies * BwPricePerPagePaise, MinimumChargePaise);

        // ── Store options ─────────────────────────────────────────
        job.OptionsJson = JsonSerializer.Serialize(new
        {
            copies = input.Options.Copies,
            color = input.Options.Color.ToUpperInvariant()
        });
        job.PriceCents = totalPaise;
        job.Currency = "INR";

        // ── Transition ────────────────────────────────────────────
        JobStateMachine.Transition(job, JobStatus.Quoted, actor: "user");

        await _audit.RecordAsync(job.JobId, AuditEventType.JobQuoted, new
        {
            copies = input.Options.Copies,
            color = input.Options.Color,
            totalPaise
        });

        await _db.SaveChangesAsync();

        return new Output(job.Status.ToString(), job.PriceCents, job.Currency);
    }
}
