using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintNest.Application.Commands;
using PrintNest.Domain.Errors;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Api.Controllers.Public;

/// <summary>
/// Customer-facing endpoints. No authentication required.
///
/// Base route: /api/v1/public/printjobs
///
/// All business logic lives in Application/Commands — controllers are thin.
/// Controllers only: bind request → call command → return response.
/// </summary>
[ApiController]
[Route("api/v1/public/printjobs")]
public sealed class PrintJobsController : ControllerBase
{
    private readonly CreateJobCommand _createJob;
    private readonly FinalizeUploadCommand _finalizeUpload;
    private readonly QuoteJobCommand _quoteJob;
    private readonly PayJobCommand _payJob;
    private readonly GenerateOtpCommand _generateOtp;
    private readonly AppDbContext _db;

    public PrintJobsController(
        CreateJobCommand createJob,
        FinalizeUploadCommand finalizeUpload,
        QuoteJobCommand quoteJob,
        PayJobCommand payJob,
        GenerateOtpCommand generateOtp,
        AppDbContext db)
    {
        _createJob = createJob;
        _finalizeUpload = finalizeUpload;
        _quoteJob = quoteJob;
        _payJob = payJob;
        _generateOtp = generateOtp;
        _db = db;
    }

    /// <summary>
    /// Step 1: Create a job and get a presigned upload URL.
    /// POST /api/v1/public/printjobs
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateJob([FromBody] CreateJobRequest req)
    {
        var result = await _createJob.ExecuteAsync(new CreateJobCommand.Input(
            req.FileName,
            req.FileSizeBytes,
            req.ContentType
        ));

        return Ok(new
        {
            jobId = result.JobId,
            upload = new
            {
                method = "PUT",
                url = result.UploadUrl,
                headers = new { contentType = "application/pdf" },
                expiresInSeconds = result.UploadExpiresInSeconds
            }
        });
    }

    /// <summary>
    /// Step 2: Confirm upload completed. Client calls this after PUT to presigned URL.
    /// POST /api/v1/public/printjobs/{jobId}/finalize
    /// </summary>
    [HttpPost("{jobId:guid}/finalize")]
    public async Task<IActionResult> FinalizeUpload(Guid jobId, [FromBody] FinalizeUploadRequest req)
    {
        await _finalizeUpload.ExecuteAsync(new FinalizeUploadCommand.Input(jobId, req.Sha256));
        return Ok(new { status = "Uploaded" });
    }

    /// <summary>
    /// Step 3: Get a price quote by selecting print options.
    /// POST /api/v1/public/printjobs/{jobId}/quote
    /// </summary>
    [HttpPost("{jobId:guid}/quote")]
    public async Task<IActionResult> Quote(Guid jobId, [FromBody] QuoteRequest req)
    {
        var result = await _quoteJob.ExecuteAsync(new QuoteJobCommand.Input(
            jobId,
            new QuoteJobCommand.PrintOptions(req.Copies, req.Color)
        ));

        return Ok(new
        {
            status = result.Status,
            pricing = new
            {
                currency = result.Currency,
                totalAmountCents = result.TotalAmountCents
            }
        });
    }

    /// <summary>
    /// Step 4: Confirm mock payment. Transitions job to Paid.
    /// POST /api/v1/public/printjobs/{jobId}/pay-mock
    /// </summary>
    [HttpPost("{jobId:guid}/pay-mock")]
    public async Task<IActionResult> PayMock(Guid jobId)
    {
        var result = await _payJob.ExecuteAsync(jobId);
        return Ok(new
        {
            status = result.Status,
            priceCents = result.PriceCents,
            currency = result.Currency
        });
    }

    /// <summary>
    /// Step 5: Generate the initial OTP. Returns OTP once — store it, it won't be shown again.
    /// POST /api/v1/public/printjobs/{jobId}/otp/generate
    /// </summary>
    [HttpPost("{jobId:guid}/otp/generate")]
    public async Task<IActionResult> GenerateOtp(Guid jobId)
    {
        var result = await _generateOtp.ExecuteAsync(jobId);
        return Ok(new
        {
            otp = result.Otp,
            expiresAtUtc = result.ExpiresAtUtc
        });
    }

    /// <summary>
    /// Get current job status. Safe to poll.
    /// GET /api/v1/public/printjobs/{jobId}
    /// </summary>
    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> GetStatus(Guid jobId)
    {
        var job = await _db.PrintJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.JobId == jobId);

        if (job is null)
            throw new DomainException(ErrorCodes.JobNotFound, "Job not found.", httpStatus: 404);

        var isFailed = job.Status == Domain.Enums.JobStatus.Failed;
        return Ok(new
        {
            jobId = job.JobId,
            status = job.Status.ToString(),
            priceCents = job.PriceCents,
            currency = job.Currency,
            otpExpiresAtUtc = job.Status == Domain.Enums.JobStatus.Paid ? job.OtpExpiryUtc : null,
            canReuseOtp = isFailed && job.RetryAllowed && job.OtpHash != null,
            failure = isFailed ? new { code = job.LastFailureCode, message = job.LastFailureMessage } : null,
            assignedStoreId = job.AssignedStoreId,
            createdAtUtc = job.CreatedAtUtc,
            updatedAtUtc = job.UpdatedAtUtc
            // Never return: OtpHash, ObjectKey, AssignedDeviceId, Sha256
        });
    }
}

// ── Request models ────────────────────────────────────────────────────────────

public sealed record CreateJobRequest(string FileName, long FileSizeBytes, string ContentType);
public sealed record FinalizeUploadRequest(string? Sha256);
public sealed record QuoteRequest(int Copies, string Color);
