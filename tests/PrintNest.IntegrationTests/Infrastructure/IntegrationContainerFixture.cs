using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using PrintNest.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace PrintNest.IntegrationTests.Infrastructure;

public sealed class IntegrationContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("printnest")
        .WithUsername("printnest")
        .WithPassword("printnest")
        .Build();

    private readonly IContainer _minio = new ContainerBuilder()
        .WithImage("minio/minio:latest")
        .WithCommand("server", "/data", "--console-address", ":9001")
        .WithPortBinding(9000, true)
        .WithPortBinding(9001, true)
        .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
        .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin123")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(9000))
        .Build();

    public string BucketName { get; } = "printfiles";
    // Legacy compatibility value for older test call-sites. Admin auth now uses staff JWT login.
    public string AdminApiKey { get; } = "legacy-admin-key-unused";
    public string JwtSigningKey { get; } = "integration-jwt-signing-key-0123456789";
    public string MinioAccessKey { get; } = "minioadmin";
    public string MinioSecretKey { get; } = "minioadmin123";
    public string StaffBootstrapUsername { get; } = "admin";
    public string StaffBootstrapPassword { get; } = "integration-admin-pass-123";

    public string PostgresConnectionString => _postgres.GetConnectionString();
    public string MinioEndpoint => $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";
    public IAmazonS3 S3Client { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await EnsureDatabaseSchemaAsync();
        await _minio.StartAsync();

        var config = new AmazonS3Config
        {
            ServiceURL = MinioEndpoint,
            ForcePathStyle = true,
            UseHttp = true
        };

        S3Client = new AmazonS3Client(MinioAccessKey, MinioSecretKey, config);

        if (!await AmazonS3Util.DoesS3BucketExistV2Async(S3Client, BucketName))
            await S3Client.PutBucketAsync(new PutBucketRequest { BucketName = BucketName });
    }

    public async Task DisposeAsync()
    {
        if (S3Client is not null)
            S3Client.Dispose();

        await _minio.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    internal PrintNestWebAppFactory CreateFactory(
        int? jwtFileTokenTtlSeconds = null,
        PrintNest.Application.Interfaces.IReleaseConcurrencyTestHook? concurrencyHook = null)
        => new(this, jwtFileTokenTtlSeconds, concurrencyHook);

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await TestReset.ResetDatabaseAsync(PostgresConnectionString, cancellationToken);
        await TestReset.ResetBucketAsync(S3Client, BucketName, cancellationToken);
    }

    public async Task<bool> ObjectExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        try
        {
            await S3Client.GetObjectMetadataAsync(BucketName, objectKey, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task EnsureDatabaseSchemaAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
    }
}
