using Microsoft.EntityFrameworkCore;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Entities;
using PrintNest.Domain.Enums;
using PrintNest.Domain.Errors;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Application.Commands;

/// <summary>
/// Creates a new print job and returns a presigned URL for direct file upload to MinIO.
///
/// Steps:
///   1. Validate input (PDF only, size within limit)
///   2. Create PrintJob in Draft state
///   3. Generate MinIO presigned PUT URL
///   4. Store ObjectKey on the job
///   5. Save to DB
///   6. Return jobId + upload URL to caller
///
/// The file is NOT in storage yet after this — the client must PUT to the presigned URL.
/// After upload, client calls FinalizeUploadCommand to confirm.
/// </summary>
public sealed class CreateJobCommand
{
    private readonly AppDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAuditService _audit;

    public CreateJobCommand(AppDbContext db, IStorageService storage, IAuditService audit)
    {
        _db = db;
        _storage = storage;
        _audit = audit;
    }

    public sealed record Input(
        string FileName,
        long FileSizeBytes,
        string ContentType
    );

    public sealed record Output(
        Guid JobId,
        string UploadUrl,
        int UploadExpiresInSeconds
    );

    private const long MaxFileSizeBytes = 20 * 1024 * 1024; // 20 MB
    private const string AllowedContentType = "application/pdf";

    public async Task<Output> ExecuteAsync(Input input)
    {
        // ── Validate ──────────────────────────────────────────────
        if (!input.ContentType.StartsWith(AllowedContentType, StringComparison.OrdinalIgnoreCase))
            throw new DomainException(
                ErrorCodes.ValidationError,
                "Only PDF files are accepted. Please convert your file to PDF before uploading.",
                httpStatus: 422
            );

        if (input.FileSizeBytes > MaxFileSizeBytes)
            throw new DomainException(
                ErrorCodes.ValidationError,
                $"File size exceeds the 20MB limit. Your file is {input.FileSizeBytes / (1024 * 1024)}MB.",
                httpStatus: 422
            );

        if (input.FileSizeBytes <= 0)
            throw new DomainException(
                ErrorCodes.ValidationError,
                "File size must be greater than zero.",
                httpStatus: 422
            );

        // ── Create job ────────────────────────────────────────────
        var job = new PrintJob
        {
            Status = JobStatus.Draft,
            Currency = "INR"
        };

        // ObjectKey format: jobs/{jobId}.pdf — deterministic and namespaced
        job.ObjectKey = $"jobs/{job.JobId}.pdf";

        // ── Generate presigned upload URL ─────────────────────────
        const int uploadTtlSeconds = 900; // 15 minutes
        var uploadUrl = await _storage.GeneratePresignedUploadUrlAsync(job.ObjectKey, uploadTtlSeconds);

        // ── Persist ───────────────────────────────────────────────
        _db.PrintJobs.Add(job);
        await _audit.RecordAsync(job.JobId, AuditEventType.JobCreated, new
        {
            fileName = input.FileName,
            fileSizeBytes = input.FileSizeBytes
            // Never log contentType as it could be spoofed; never log file content
        });
        await _db.SaveChangesAsync();

        return new Output(job.JobId, uploadUrl, uploadTtlSeconds);
    }
}
