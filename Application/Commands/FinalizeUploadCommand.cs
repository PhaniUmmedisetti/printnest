using Microsoft.EntityFrameworkCore;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Enums;
using PrintNest.Domain.Errors;
using PrintNest.Domain.StateMachine;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Application.Commands;

/// <summary>
/// Confirms that the client has finished uploading the file to MinIO.
///
/// Steps:
///   1. Load job — must be in Draft state
///   2. Verify the object actually exists in MinIO (client might lie)
///   3. Store the client-provided SHA256
///   4. Transition Draft → Uploaded
///   5. Save
/// </summary>
public sealed class FinalizeUploadCommand
{
    private readonly AppDbContext _db;
    private readonly IStorageService _storage;
    private readonly IAuditService _audit;

    public FinalizeUploadCommand(AppDbContext db, IStorageService storage, IAuditService audit)
    {
        _db = db;
        _storage = storage;
        _audit = audit;
    }

    public sealed record Input(Guid JobId, string? Sha256);

    public async Task ExecuteAsync(Input input)
    {
        var job = await _db.PrintJobs.FirstOrDefaultAsync(j => j.JobId == input.JobId)
            ?? throw new DomainException(ErrorCodes.JobNotFound, "Job not found.", httpStatus: 404);

        // ── Verify file exists in MinIO ───────────────────────────
        if (string.IsNullOrEmpty(job.ObjectKey))
            throw new DomainException(ErrorCodes.StorageError, "No object key assigned.", httpStatus: 500);

        var exists = await _storage.VerifyObjectExistsAsync(job.ObjectKey);
        if (!exists)
            throw new DomainException(
                ErrorCodes.StorageError,
                "File not found in storage. Please re-upload.",
                httpStatus: 400
            );

        // ── Store SHA256 (provided by client — trusted in MVP) ────
        job.Sha256 = input.Sha256;

        // ── Transition ────────────────────────────────────────────
        JobStateMachine.Transition(job, JobStatus.Uploaded, actor: "user");

        await _audit.RecordAsync(job.JobId, AuditEventType.UploadFinalized, new
        {
            objectKey = job.ObjectKey
            // Never log sha256 in meta — it's a fingerprint of the user's file
        });

        await _db.SaveChangesAsync();
    }
}
