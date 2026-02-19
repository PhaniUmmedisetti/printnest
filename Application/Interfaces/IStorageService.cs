namespace PrintNest.Application.Interfaces;

/// <summary>
/// Abstracts all file storage operations (MinIO in implementation).
///
/// Implementations live in Infrastructure/Storage/MinioStorageService.cs.
/// Tests use a fake/in-memory implementation.
///
/// All methods throw DomainException(ErrorCodes.StorageError) on storage failure.
/// Never throw raw exceptions from implementations — wrap them.
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// Generates a presigned PUT URL that the client uses to upload their file directly to MinIO.
    ///
    /// The client uploads directly — the file never passes through this API server.
    /// Call VerifyObjectExistsAsync() after the client claims the upload is done.
    /// </summary>
    /// <param name="objectKey">MinIO object path. Format: jobs/{jobId}.pdf</param>
    /// <param name="expiresInSeconds">How long the URL is valid. Default: 900 (15 min).</param>
    Task<string> GeneratePresignedUploadUrlAsync(string objectKey, int expiresInSeconds = 900);

    /// <summary>
    /// Checks that the object exists in storage (confirms the client actually uploaded).
    /// Returns false if the object does not exist — does not throw.
    /// </summary>
    Task<bool> VerifyObjectExistsAsync(string objectKey);

    /// <summary>
    /// Streams the file content to the provided output stream.
    /// Used by the device file download endpoint — file is never written to API server disk.
    /// </summary>
    Task StreamFileAsync(string objectKey, Stream destination, CancellationToken ct = default);

    /// <summary>
    /// Deletes the object from storage.
    /// Returns true on success, false if the object was already gone (idempotent).
    /// Throws DomainException(ErrorCodes.StorageError) on actual failure.
    /// </summary>
    Task<bool> DeleteFileAsync(string objectKey);
}
