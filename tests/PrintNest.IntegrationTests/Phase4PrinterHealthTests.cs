using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using PrintNest.IntegrationTests.Infrastructure;
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
        devicesRequest.Headers.Add("X-Admin-Key", _fixture.AdminApiKey);
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
        alertsRequest.Headers.Add("X-Admin-Key", _fixture.AdminApiKey);
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
}
