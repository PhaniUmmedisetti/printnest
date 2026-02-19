using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Errors;

namespace PrintNest.Infrastructure.Storage;

/// <summary>
/// MinIO (S3-compatible) implementation of IStorageService.
///
/// Configuration keys (from environment / appsettings):
///   Storage:Endpoint     — e.g. http://localhost:9000
///   Storage:AccessKey
///   Storage:SecretKey
///   Storage:BucketName   — default: printfiles
///   Storage:UseHttps     — default: false (local dev)
///
/// The bucket must exist before the app starts.
/// docker-compose creates it via MinIO init container.
/// </summary>
public sealed class MinioStorageService : IStorageService
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public MinioStorageService(IConfiguration config)
    {
        var endpoint = config["Storage:Endpoint"]
            ?? throw new InvalidOperationException("Storage:Endpoint is required.");
        var accessKey = config["Storage:AccessKey"]
            ?? throw new InvalidOperationException("Storage:AccessKey is required.");
        var secretKey = config["Storage:SecretKey"]
            ?? throw new InvalidOperationException("Storage:SecretKey is required.");

        _bucket = config["Storage:BucketName"] ?? "printfiles";

        // Configure AWS SDK to point at MinIO
        _s3 = new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true, // required for MinIO — virtual-hosted style not supported
            UseHttp = !bool.Parse(config["Storage:UseHttps"] ?? "false")
        });
    }

    /// <summary>
    /// Generates a presigned PUT URL. Client uploads directly to MinIO — never through this API.
    /// </summary>
    public async Task<string> GeneratePresignedUploadUrlAsync(string objectKey, int expiresInSeconds = 900)
    {
        try
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = objectKey,
                Verb = HttpVerb.PUT,
                Expires = DateTime.UtcNow.AddSeconds(expiresInSeconds),
                ContentType = "application/pdf"
            };

            // AWS SDK GetPreSignedURL is synchronous
            return await Task.FromResult(_s3.GetPreSignedURL(request));
        }
        catch (Exception ex)
        {
            throw new DomainException(
                ErrorCodes.StorageError,
                $"Failed to generate upload URL: {ex.Message}",
                httpStatus: 500
            );
        }
    }

    /// <summary>
    /// Checks that the object exists in MinIO (HEAD request — no data transfer).
    /// </summary>
    public async Task<bool> VerifyObjectExistsAsync(string objectKey)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_bucket, objectKey);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception ex)
        {
            throw new DomainException(
                ErrorCodes.StorageError,
                $"Failed to verify object: {ex.Message}",
                httpStatus: 500
            );
        }
    }

    /// <summary>
    /// Streams the file from MinIO to the destination stream.
    /// File is never written to disk on the API server.
    /// </summary>
    public async Task StreamFileAsync(string objectKey, Stream destination, CancellationToken ct = default)
    {
        try
        {
            var response = await _s3.GetObjectAsync(_bucket, objectKey, ct);
            await response.ResponseStream.CopyToAsync(destination, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new DomainException(
                ErrorCodes.StorageError,
                "File not found in storage.",
                httpStatus: 404
            );
        }
        catch (Exception ex) when (ex is not DomainException)
        {
            throw new DomainException(
                ErrorCodes.StorageError,
                $"Failed to stream file: {ex.Message}",
                httpStatus: 500
            );
        }
    }

    /// <summary>
    /// Deletes a file from MinIO. Returns true on success, false if already gone.
    /// </summary>
    public async Task<bool> DeleteFileAsync(string objectKey)
    {
        try
        {
            await _s3.DeleteObjectAsync(_bucket, objectKey);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false; // Already deleted — idempotent
        }
        catch (Exception ex)
        {
            throw new DomainException(
                ErrorCodes.StorageError,
                $"Failed to delete file: {ex.Message}",
                httpStatus: 500
            );
        }
    }
}
