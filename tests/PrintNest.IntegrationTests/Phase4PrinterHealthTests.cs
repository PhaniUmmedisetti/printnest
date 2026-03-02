using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PrintNest.Domain.Entities;
using PrintNest.Domain.Enums;
using PrintNest.IntegrationTests.Infrastructure;
using PrintNest.Infrastructure.Persistence;
using Xunit;

namespace PrintNest.IntegrationTests;

[Collection("Integration")]
public sealed class Phase4PrinterHealthTests : IAsyncLifetime
{
    private readonly IntegrationContainerFixture _fixture;

    public Phase4PrinterHealthTests(IntegrationContainerFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Heartbeat_Persists_PrinterHealth_For_Admin_Devices_View()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        var admin = await ApiFlowHelpers.LoginAsStaffAsync(client, _fixture.StaffBootstrapUsername, _fixture.StaffBootstrapPassword);

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);

        using var heartbeatRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/heartbeat",
            device.DeviceId,
            device.SharedSecret,
            new
            {
                storeId = device.StoreId,
                capabilitiesJson = """{"cupsPrinters":["HP_DeskJet_2338"]}""",
                printerHealth = new
                {
                    printerModel = "HP DeskJet 2338",
                    connectionState = "online",
                    operationalState = "idle",
                    paperOut = false,
                    doorOpen = false,
                    cartridgeMissing = false,
                    inkState = "low",
                    rawStatusJson = """{"cups":"idle"}"""
                }
            });
        using var heartbeatResponse = await client.SendAsync(heartbeatRequest);
        heartbeatResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var devicesRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/devices");
        devicesRequest.Headers.Authorization = new("Bearer", admin.AccessToken);
        using var devicesResponse = await client.SendAsync(devicesRequest);
        devicesResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await devicesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var deviceNode = payload.EnumerateArray().First(d => d.GetProperty("deviceId").GetString() == device.DeviceId);
        var health = deviceNode.GetProperty("printerHealth");
        health.GetProperty("printerModel").GetString().Should().Be("HP DeskJet 2338");
        health.GetProperty("connectionState").GetString().Should().Be("ONLINE");
        health.GetProperty("operationalState").GetString().Should().Be("IDLE");
        health.GetProperty("inkState").GetString().Should().Be("LOW");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Device_Alerts_Endpoint_Returns_Printer_Alerts()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        var admin = await ApiFlowHelpers.LoginAsStaffAsync(client, _fixture.StaffBootstrapUsername, _fixture.StaffBootstrapPassword);

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);

        using var heartbeatRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/heartbeat",
            device.DeviceId,
            device.SharedSecret,
            new
            {
                storeId = device.StoreId,
                capabilitiesJson = "{}",
                printerHealth = new
                {
                    printerModel = "HP DeskJet 2338",
                    connectionState = "offline",
                    operationalState = "error",
                    paperOut = true,
                    doorOpen = true,
                    cartridgeMissing = false,
                    inkState = "empty",
                    rawStatusJson = "{}"
                }
            });
        using var heartbeatResponse = await client.SendAsync(heartbeatRequest);
        heartbeatResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var alertsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/devices/alerts");
        alertsRequest.Headers.Authorization = new("Bearer", admin.AccessToken);
        using var alertsResponse = await client.SendAsync(alertsRequest);
        alertsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await alertsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var deviceAlerts = payload.EnumerateArray()
            .Where(a => a.GetProperty("deviceId").GetString() == device.DeviceId)
            .Select(a => a.GetProperty("alertCode").GetString())
            .ToList();

        deviceAlerts.Should().Contain("PRINTER_OFFLINE");
        deviceAlerts.Should().Contain("PRINTER_ERROR");
        deviceAlerts.Should().Contain("PAPER_OUT");
        deviceAlerts.Should().Contain("DOOR_OPEN");
        deviceAlerts.Should().Contain("INK_EMPTY");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Release_Is_Blocked_When_Printer_Has_Blocking_Consumable_Alert()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        var admin = await ApiFlowHelpers.LoginAsStaffAsync(client, _fixture.StaffBootstrapUsername, _fixture.StaffBootstrapPassword);

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);
        var job = await ApiFlowHelpers.CreatePaidJobWithOtpAsync(client);

        using var heartbeatRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/heartbeat",
            device.DeviceId,
            device.SharedSecret,
            new
            {
                storeId = device.StoreId,
                capabilitiesJson = "{}",
                printerHealth = new
                {
                    connectionState = "online",
                    operationalState = "idle",
                    paperOut = false,
                    doorOpen = false,
                    cartridgeMissing = false,
                    inkState = "empty"
                }
            });
        using var heartbeatResponse = await client.SendAsync(heartbeatRequest);
        heartbeatResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var releaseRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/release",
            device.DeviceId,
            device.SharedSecret,
            new { otp = job.Otp, storeId = device.StoreId });
        using var releaseResponse = await client.SendAsync(releaseRequest);
        releaseResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiFlowHelpers.GetErrorCodeAsync(releaseResponse)).Should().Be("PRINTER_NOT_READY");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Device_Alerts_Endpoint_Contains_Escalation_And_InkPrediction_Metadata()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        var admin = await ApiFlowHelpers.LoginAsStaffAsync(client, _fixture.StaffBootstrapUsername, _fixture.StaffBootstrapPassword);

        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);

        using var heartbeatRequest = ApiFlowHelpers.CreateSignedJsonRequest(
            HttpMethod.Post,
            "/api/v1/device/heartbeat",
            device.DeviceId,
            device.SharedSecret,
            new
            {
                storeId = device.StoreId,
                capabilitiesJson = "{}",
                printerHealth = new
                {
                    connectionState = "online",
                    operationalState = "idle",
                    paperOut = false,
                    doorOpen = false,
                    cartridgeMissing = false,
                    inkState = "low"
                }
            });
        using var heartbeatResponse = await client.SendAsync(heartbeatRequest);
        heartbeatResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var alertsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/devices/alerts");
        alertsRequest.Headers.Authorization = new("Bearer", admin.AccessToken);
        using var alertsResponse = await client.SendAsync(alertsRequest);
        alertsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await alertsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inkLowAlert = payload.EnumerateArray()
            .First(a =>
                a.GetProperty("deviceId").GetString() == device.DeviceId &&
                a.GetProperty("alertCode").GetString() == "INK_LOW");

        inkLowAlert.GetProperty("severity").GetString().Should().Be("WARNING");
        inkLowAlert.GetProperty("isBlocking").GetBoolean().Should().BeFalse();
        inkLowAlert.TryGetProperty("escalatesAtUtc", out var escalatesAtUtc).Should().BeTrue();
        escalatesAtUtc.ValueKind.Should().Be(JsonValueKind.String);
        inkLowAlert.TryGetProperty("inkPrediction", out var inkPrediction).Should().BeTrue();
        inkPrediction.ValueKind.Should().Be(JsonValueKind.Object);
        inkPrediction.GetProperty("remainingMinutesUpper").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Store_Manager_Only_Sees_Own_Store_Devices()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();

        var superAdmin = await ApiFlowHelpers.LoginAsStaffAsync(
            client,
            _fixture.StaffBootstrapUsername,
            _fixture.StaffBootstrapPassword);

        var storeA = $"store_a_{Guid.NewGuid():N}";
        var storeB = $"store_b_{Guid.NewGuid():N}";
        var deviceA = $"dev_a_{Guid.NewGuid():N}";
        var deviceB = $"dev_b_{Guid.NewGuid():N}";

        using (var createStoreA = ApiFlowHelpers.CreateStaffRequest(HttpMethod.Post, "/api/v1/admin/stores", superAdmin.AccessToken, new
        {
            storeId = storeA,
            name = "Store A",
            address = "A Street",
            latitude = 17.4010,
            longitude = 78.4020
        }))
        using (var createStoreAResponse = await client.SendAsync(createStoreA))
        {
            createStoreAResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var createStoreB = ApiFlowHelpers.CreateStaffRequest(HttpMethod.Post, "/api/v1/admin/stores", superAdmin.AccessToken, new
        {
            storeId = storeB,
            name = "Store B",
            address = "B Street",
            latitude = 17.5010,
            longitude = 78.5020
        }))
        using (var createStoreBResponse = await client.SendAsync(createStoreB))
        {
            createStoreBResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var registerA = ApiFlowHelpers.CreateStaffRequest(HttpMethod.Post, "/api/v1/admin/devices", superAdmin.AccessToken, new
        {
            deviceId = deviceA,
            storeId = storeA
        }))
        using (var registerAResponse = await client.SendAsync(registerA))
        {
            registerAResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var registerB = ApiFlowHelpers.CreateStaffRequest(HttpMethod.Post, "/api/v1/admin/devices", superAdmin.AccessToken, new
        {
            deviceId = deviceB,
            storeId = storeB
        }))
        using (var registerBResponse = await client.SendAsync(registerB))
        {
            registerBResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var managerUsername = $"manager_{Guid.NewGuid():N}";
        var managerPassword = "ManagerPass-12345";

        using (var createManager = ApiFlowHelpers.CreateStaffRequest(HttpMethod.Post, "/api/v1/admin/staff-users", superAdmin.AccessToken, new
        {
            username = managerUsername,
            displayName = "Manager A",
            password = managerPassword,
            role = "STORE_MANAGER",
            storeId = storeA
        }))
        using (var createManagerResponse = await client.SendAsync(createManager))
        {
            createManagerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var manager = await ApiFlowHelpers.LoginAsStaffAsync(client, managerUsername, managerPassword);

        using var managerDevicesRequest = ApiFlowHelpers.CreateStaffRequest(
            HttpMethod.Get,
            "/api/v1/admin/devices",
            manager.AccessToken);
        using var managerDevicesResponse = await client.SendAsync(managerDevicesRequest);
        managerDevicesResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await managerDevicesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var storeIds = payload.EnumerateArray()
            .Select(x => x.GetProperty("storeId").GetString())
            .Distinct()
            .ToList();

        storeIds.Should().ContainSingle().Which.Should().Be(storeA);
        payload.EnumerateArray().Any(x => x.GetProperty("deviceId").GetString() == deviceB).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ops_Summary_Returns_Queue_Backlog_And_Failure_Trend_Derived_Alerts()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        var admin = await ApiFlowHelpers.LoginAsStaffAsync(client, _fixture.StaffBootstrapUsername, _fixture.StaffBootstrapPassword);
        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;

            db.PrintJobs.AddRange(
                Enumerable.Range(0, 3).Select(i => new PrintJob
                {
                    JobId = Guid.NewGuid(),
                    Status = JobStatus.Printing,
                    AssignedDeviceId = device.DeviceId,
                    AssignedStoreId = device.StoreId,
                    ReleaseLockUtc = now.AddMinutes(-(12 + i)),
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now.AddMinutes(-(11 + i))
                }));

            db.PrintJobs.AddRange(
                Enumerable.Range(0, 3).Select(i => new PrintJob
                {
                    JobId = Guid.NewGuid(),
                    Status = JobStatus.Failed,
                    AssignedDeviceId = device.DeviceId,
                    AssignedStoreId = device.StoreId,
                    ReleaseLockUtc = now.AddMinutes(-(20 + i)),
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now.AddMinutes(-(5 + i))
                }));

            await db.SaveChangesAsync();
        }

        using var summaryRequest = ApiFlowHelpers.CreateStaffRequest(HttpMethod.Get, "/api/v1/admin/ops/summary", admin.AccessToken);
        using var summaryResponse = await client.SendAsync(summaryRequest);
        summaryResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("totals").GetProperty("queueBacklog").GetInt32().Should().BeGreaterThan(0);
        payload.GetProperty("totals").GetProperty("failureTrend").GetInt32().Should().BeGreaterThan(0);
        payload.GetProperty("stores").EnumerateArray().Any(x => x.GetProperty("storeId").GetString() == device.StoreId).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Device_Alerts_Endpoint_Returns_Connection_Flapping_Derived_Alert()
    {
        using var factory = _fixture.CreateFactory();
        using var client = factory.CreateClient();
        var admin = await ApiFlowHelpers.LoginAsStaffAsync(client, _fixture.StaffBootstrapUsername, _fixture.StaffBootstrapPassword);
        var device = await ApiFlowHelpers.RegisterStoreAndDeviceAsync(client, _fixture.AdminApiKey);

        foreach (var state in new[] { "online", "offline", "online", "offline", "online" })
        {
            using var heartbeatRequest = ApiFlowHelpers.CreateSignedJsonRequest(
                HttpMethod.Post,
                "/api/v1/device/heartbeat",
                device.DeviceId,
                device.SharedSecret,
                new
                {
                    storeId = device.StoreId,
                    capabilitiesJson = "{}",
                    printerHealth = new
                    {
                        connectionState = state,
                        operationalState = "idle",
                        paperOut = false,
                        doorOpen = false,
                        cartridgeMissing = false,
                        inkState = "ok"
                    }
                });
            using var heartbeatResponse = await client.SendAsync(heartbeatRequest);
            heartbeatResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var alertsRequest = ApiFlowHelpers.CreateStaffRequest(HttpMethod.Get, "/api/v1/admin/devices/alerts", admin.AccessToken);
        using var alertsResponse = await client.SendAsync(alertsRequest);
        alertsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await alertsResponse.Content.ReadFromJsonAsync<JsonElement>();
        payload.EnumerateArray().Any(a =>
            a.GetProperty("deviceId").GetString() == device.DeviceId &&
            a.GetProperty("alertCode").GetString() == "CONNECTION_FLAPPING").Should().BeTrue();
    }
}
