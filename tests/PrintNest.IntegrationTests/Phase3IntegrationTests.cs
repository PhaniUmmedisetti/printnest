using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintNest.Domain.Enums;
using PrintNest.Domain.Errors;
using PrintNest.Infrastructure.Persistence;
using PrintNest.Infrastructure.Workers;
using PrintNest.IntegrationTests.Infrastructure;
using Xunit;

namespace PrintNest.IntegrationTests;

[Collection("Integration")]
public sealed class Phase3IntegrationTests : IAsyncLifetime
{
    private readonly IntegrationContainerFixture _fixture;

    public Phase3IntegrationTests(IntegrationContainerFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Happy_Path_Full_Lifecycle_To_Deleted()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var job = await ApiFlowHelpers.CreatePaidJobWithOtpAsync(client);

        var release = await ApiFlowHelpers.ReleaseByOtpAsync(client, device, job.Otp);
        release.JobId.Should().Be(job.JobId);

        var downloadPath = $"/api/v1/device/printjobs/{job.JobId}/file";
        using var downloadRequest = ApiFlowHelpers.CreateSignedRequest(
            HttpMethod.Get,
            downloadPath,
            device.DeviceId,
            device.SharedSecret,
            release.FileToken);
        using var downloadResponse = await client.SendAsync(downloadRequest);
        downloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var printingRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            $"/api/v1/device/printjobs/{job.JobId}/printing-started",
            device.DeviceId,
            device.SharedSecret,
            new { cupsJobId = "cups-1", printerName = "integration-printer" });
        using var printingResponse = await client.SendAsync(printingRequest);
        printingResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var completedRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            $"/api/v1/device/printjobs/{job.JobId}/completed",
            device.DeviceId,
            device.SharedSecret,
            new { cupsJobId = "cups-1" });
        using var completedResponse = await client.SendAsync(completedRequest);
        completedResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ApiFlowHelpers.GetJobStatusAsync(client, job.JobId)).Should().Be("Completed");

        var cleanupWorker = factory.Services.GetRequiredService<CleanupWorker>();
        await cleanupWorker.RunOnceAsync(CancellationToken.None);

        (await ApiFlowHelpers.GetJobStatusAsync(client, job.JobId)).Should().Be("Deleted");
        (await _fixture.ObjectExistsAsync($"jobs/{job.JobId}.pdf")).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Otp_Is_Single_Use()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var job = await ApiFlowHelpers.CreatePaidJobWithOtpAsync(client);

        var firstRelease = await ApiFlowHelpers.ReleaseByOtpAsync(client, device, job.Otp);
        firstRelease.JobId.Should().Be(job.JobId);

        using var secondReleaseRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/release",
            device.DeviceId,
            device.SharedSecret,
            new { otp = job.Otp, storeId = device.StoreId });
        using var secondReleaseResponse = await client.SendAsync(secondReleaseRequest);

        secondReleaseResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ApiFlowHelpers.GetErrorCodeAsync(secondReleaseResponse)).Should().Be(ErrorCodes.OtpInvalid);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Otp_Expiry_Rejects_Release()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var job = await ApiFlowHelpers.CreatePaidJobWithOtpAsync(client);

        await UpdateJobAsync(factory.Services, job.JobId, j =>
        {
            j.OtpExpiryUtc = DateTime.UtcNow.AddMinutes(-5);
            return Task.CompletedTask;
        });

        using var releaseRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/release",
            device.DeviceId,
            device.SharedSecret,
            new { otp = job.Otp, storeId = device.StoreId });
        using var releaseResponse = await client.SendAsync(releaseRequest);

        releaseResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ApiFlowHelpers.GetErrorCodeAsync(releaseResponse)).Should().Be(ErrorCodes.OtpInvalid);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Double_Release_Allows_One_And_Returns_LockConflict_For_Other()
    {
        using var hook = new DeterministicReleaseConcurrencyHook();
        using var factory = _fixture.CreateFactory(concurrencyHook: hook);
        using var client = factory.CreateClient();

        var deviceA = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var deviceB = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var job = await ApiFlowHelpers.CreatePaidJobWithOtpAsync(client);
        hook.SetTargetJob(job.JobId);

        var requestA = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/release",
            deviceA.DeviceId,
            deviceA.SharedSecret,
            new { otp = job.Otp, storeId = deviceA.StoreId });

        var requestB = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/release",
            deviceB.DeviceId,
            deviceB.SharedSecret,
            new { otp = job.Otp, storeId = deviceB.StoreId });

        var responseTaskA = client.SendAsync(requestA);
        var responseTaskB = client.SendAsync(requestB);
        await Task.WhenAll(responseTaskA, responseTaskB);

        using var responseA = await responseTaskA;
        using var responseB = await responseTaskB;

        var responses = new[] { responseA, responseB };
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        (await ApiFlowHelpers.GetErrorCodeAsync(loser)).Should().Be(ErrorCodes.LockConflict);
        hook.ParticipantCount.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task File_Token_Is_Single_Use()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var job = await ApiFlowHelpers.CreatePaidJobWithOtpAsync(client);
        var release = await ApiFlowHelpers.ReleaseByOtpAsync(client, device, job.Otp);

        var path = $"/api/v1/device/printjobs/{job.JobId}/file";

        using var firstDownloadRequest = ApiFlowHelpers.CreateSignedRequest(
            HttpMethod.Get, path, device.DeviceId, device.SharedSecret, release.FileToken);
        using var firstDownloadResponse = await client.SendAsync(firstDownloadRequest);
        firstDownloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var secondDownloadRequest = ApiFlowHelpers.CreateSignedRequest(
            HttpMethod.Get, path, device.DeviceId, device.SharedSecret, release.FileToken);
        using var secondDownloadResponse = await client.SendAsync(secondDownloadRequest);
        secondDownloadResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiFlowHelpers.GetErrorCodeAsync(secondDownloadResponse)).Should().Be(ErrorCodes.TokenAlreadyUsed);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task File_Token_Expiry_Is_Enforced()
    {
        using var factory = _fixture.CreateFactory(jwtFileTokenTtlSeconds: 1);
        using var client = factory.CreateClient();

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var job = await ApiFlowHelpers.CreatePaidJobWithOtpAsync(client);
        var release = await ApiFlowHelpers.ReleaseByOtpAsync(client, device, job.Otp);

        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        using var downloadRequest = ApiFlowHelpers.CreateSignedRequest(
            HttpMethod.Get,
            $"/api/v1/device/printjobs/{job.JobId}/file",
            device.DeviceId,
            device.SharedSecret,
            release.FileToken);
        using var downloadResponse = await client.SendAsync(downloadRequest);

        downloadResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiFlowHelpers.GetErrorCodeAsync(downloadResponse)).Should().Be(ErrorCodes.TokenExpired);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Hmac_Replay_Attack_Is_Rejected()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var staleTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 601;

        using var request = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/heartbeat",
            device.DeviceId,
            device.SharedSecret,
            new { storeId = device.StoreId, capabilitiesJson = "{}" },
            unixTimestamp: staleTimestamp);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiFlowHelpers.GetErrorCodeAsync(response)).Should().Be(ErrorCodes.DeviceUnauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Hmac_Wrong_Signature_Is_Rejected()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);

        using var request = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/heartbeat",
            device.DeviceId,
            device.SharedSecret,
            new { storeId = device.StoreId, capabilitiesJson = "{}" },
            forcedSignature: "deadbeef");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiFlowHelpers.GetErrorCodeAsync(response)).Should().Be(ErrorCodes.DeviceUnauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Rate_Limit_Triggers_On_Seventh_Failed_Attempt()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);

        for (var i = 0; i < 6; i++)
        {
            using var request = ApiFlowHelpers.CreateSignedJsonRequest(
                HttpMethod.Post,
                "/api/v1/device/release",
                device.DeviceId,
                device.SharedSecret,
                new { otp = "000000", storeId = device.StoreId });
            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ApiFlowHelpers.GetErrorCodeAsync(response)).Should().Be(ErrorCodes.OtpInvalid);
        }

        using var limitedRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/release",
            device.DeviceId,
            device.SharedSecret,
            new { otp = "000000", storeId = device.StoreId });
        using var limitedResponse = await client.SendAsync(limitedRequest);

        limitedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ApiFlowHelpers.GetErrorCodeAsync(limitedResponse)).Should().Be(ErrorCodes.OtpRateLimited);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExpiryWorker_Expires_Old_Quoted_Job()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var job = await ApiFlowHelpers.CreatePaidJobWithOtpAsync(client);

        await UpdateJobAsync(factory.Services, job.JobId, j =>
        {
            j.Status = JobStatus.Quoted;
            return Task.CompletedTask;
        });
        await SetJobTimestampsAsync(factory.Services, job.JobId, DateTime.UtcNow.AddHours(-25), DateTime.UtcNow.AddHours(-25));

        var expiryWorker = factory.Services.GetRequiredService<ExpiryWorker>();
        await expiryWorker.RunOnceAsync(CancellationToken.None);

        var status = await GetJobStatusFromDbAsync(factory.Services, job.JobId);
        status.Should().Be(JobStatus.Expired);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CleanupWorker_Deletes_File_And_Transitions_To_Deleted()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var job = await ApiFlowHelpers.CreatePaidJobWithOtpAsync(client);
        var release = await ApiFlowHelpers.ReleaseByOtpAsync(client, device, job.Otp);

        using var downloadRequest = ApiFlowHelpers.CreateSignedRequest(
            HttpMethod.Get,
            $"/api/v1/device/printjobs/{job.JobId}/file",
            device.DeviceId,
            device.SharedSecret,
            release.FileToken);
        using var downloadResponse = await client.SendAsync(downloadRequest);
        downloadResponse.EnsureSuccessStatusCode();

        using var printingRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            $"/api/v1/device/printjobs/{job.JobId}/printing-started",
            device.DeviceId,
            device.SharedSecret,
            new { cupsJobId = "cups-cleanup", printerName = "integration-printer" });
        using var printingResponse = await client.SendAsync(printingRequest);
        printingResponse.EnsureSuccessStatusCode();

        using var completedRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            $"/api/v1/device/printjobs/{job.JobId}/completed",
            device.DeviceId,
            device.SharedSecret,
            new { cupsJobId = "cups-cleanup" });
        using var completedResponse = await client.SendAsync(completedRequest);
        completedResponse.EnsureSuccessStatusCode();

        var cleanupWorker = factory.Services.GetRequiredService<CleanupWorker>();
        await cleanupWorker.RunOnceAsync(CancellationToken.None);

        var status = await GetJobStatusFromDbAsync(factory.Services, job.JobId);
        status.Should().Be(JobStatus.Deleted);
        (await _fixture.ObjectExistsAsync($"jobs/{job.JobId}.pdf")).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Invalid_State_Transition_Is_Rejected()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var jobId = await ApiFlowHelpers.CreateDraftJobAsync(client);

        using var quoteResponse = await client.PostAsJsonAsync($"/api/v1/public/printjobs/{jobId}/quote", new
        {
            copies = 1,
            color = "BW"
        });

        quoteResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiFlowHelpers.GetErrorCodeAsync(quoteResponse)).Should().Be(ErrorCodes.JobStateInvalid);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PrePayment_Abandonment_Expires_And_Cleans_Up()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var jobId = await ApiFlowHelpers.CreateDraftJobAsync(client);

        await SetJobTimestampsAsync(factory.Services, jobId, DateTime.UtcNow.AddHours(-25), DateTime.UtcNow.AddHours(-25));

        var expiryWorker = factory.Services.GetRequiredService<ExpiryWorker>();
        await expiryWorker.RunOnceAsync(CancellationToken.None);

        var cleanupWorker = factory.Services.GetRequiredService<CleanupWorker>();
        await cleanupWorker.RunOnceAsync(CancellationToken.None);

        var status = await GetJobStatusFromDbAsync(factory.Services, jobId);
        status.Should().Be(JobStatus.Deleted);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Device_Cannot_Update_Job_Owned_By_Other_Device()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var deviceA = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var deviceB = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var job = await ApiFlowHelpers.CreatePaidJobWithOtpAsync(client);
        await ApiFlowHelpers.ReleaseByOtpAsync(client, deviceA, job.Otp);

        using var completedByWrongDevice = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            $"/api/v1/device/printjobs/{job.JobId}/completed",
            deviceB.DeviceId,
            deviceB.SharedSecret,
            new { cupsJobId = "cups-unauthorized" });
        using var response = await client.SendAsync(completedByWrongDevice);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApiFlowHelpers.GetErrorCodeAsync(response)).Should().Be(ErrorCodes.DeviceUnauthorized);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Admin_Key_Validation_Works_For_Missing_Wrong_And_Correct()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        using var missingKeyResponse = await client.GetAsync("/api/v1/admin/stores");
        missingKeyResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiFlowHelpers.GetErrorCodeAsync(missingKeyResponse)).Should().Be(ErrorCodes.AdminUnauthorized);

        using var wrongKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/stores");
        wrongKeyRequest.Headers.Add("X-Admin-Key", "wrong-key");
        using var wrongKeyResponse = await client.SendAsync(wrongKeyRequest);
        wrongKeyResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiFlowHelpers.GetErrorCodeAsync(wrongKeyResponse)).Should().Be(ErrorCodes.AdminUnauthorized);

        using var correctKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/stores");
        correctKeyRequest.Headers.Add("X-Admin-Key", _fixture.AdminApiKey);
        using var correctKeyResponse = await client.SendAsync(correctKeyRequest);
        correctKeyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task UpdateJobAsync(
        IServiceProvider services,
        Guid jobId,
        Func<PrintNest.Domain.Entities.PrintJob, Task> mutation)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PrintJobs.FirstAsync(j => j.JobId == jobId);
        await mutation(job);
        await db.SaveChangesAsync();
    }

    private static async Task<JobStatus> GetJobStatusFromDbAsync(IServiceProvider services, Guid jobId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PrintJobs.AsNoTracking().FirstAsync(j => j.JobId == jobId);
        return job.Status;
    }

    private static async Task SetJobTimestampsAsync(
        IServiceProvider services,
        Guid jobId,
        DateTime createdAtUtc,
        DateTime updatedAtUtc)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE print_jobs SET created_at_utc = {createdAtUtc}, updated_at_utc = {updatedAtUtc} WHERE job_id = {jobId}");
    }
}
