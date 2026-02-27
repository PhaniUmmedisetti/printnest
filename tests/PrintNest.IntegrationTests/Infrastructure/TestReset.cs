using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;

namespace PrintNest.IntegrationTests.Infrastructure;

internal static class TestReset
{
    public static async Task ResetDatabaseAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        const string sql = """
            TRUNCATE TABLE
                audit_events,
                used_file_tokens,
                print_jobs,
                devices,
                stores
            RESTART IDENTITY CASCADE;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task ResetBucketAsync(
        IAmazonS3 s3Client,
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;

        do
        {
            var list = await s3Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucketName,
                ContinuationToken = continuationToken
            }, cancellationToken);

            if (list.S3Objects.Count > 0)
            {
                var deleteRequest = new DeleteObjectsRequest
                {
                    BucketName = bucketName,
                    Objects = list.S3Objects
                        .Select(o => new KeyVersion { Key = o.Key })
                        .ToList()
                };

                await s3Client.DeleteObjectsAsync(deleteRequest, cancellationToken);
            }

            continuationToken = list.IsTruncated ? list.NextContinuationToken : null;
        } while (continuationToken is not null);
    }
}
