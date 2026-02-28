using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintNest.Domain.Entities;
using PrintNest.Domain.Errors;
using PrintNest.Infrastructure.Persistence;
using System.Security.Cryptography;

namespace PrintNest.Api.Controllers.Admin;

/// <summary>
/// Admin-only endpoints. Protected by AdminAuthMiddleware (X-Admin-Key header).
///
/// Base route: /api/v1/admin
///
/// Provides:
///   - Device registration (called by provisioning script)
///   - Device listing + health telemetry + alerts
///   - Store management
///   - Device deactivation
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminController(AppDbContext db) => _db = db;

    /// <summary>
    /// Register a new device. Called by the provisioning script (tools/provision-device.sh).
    /// POST /api/v1/admin/devices
    ///
    /// Returns the generated SharedSecret - store it securely, it won't be shown again.
    /// </summary>
    [HttpPost("devices")]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceId) || !req.DeviceId.StartsWith("dev_"))
        {
            throw new DomainException(
                ErrorCodes.ValidationError,
                "DeviceId must start with 'dev_'. Example: dev_store1_abc12345",
                httpStatus: 422
            );
        }

        var existing = await _db.Devices.AnyAsync(d => d.DeviceId == req.DeviceId);
        if (existing)
        {
            throw new DomainException(
                ErrorCodes.ValidationError,
                $"Device '{req.DeviceId}' is already registered.",
                httpStatus: 409
            );
        }

        var secretBytes = RandomNumberGenerator.GetBytes(32); // 256-bit
        var sharedSecret = Convert.ToBase64String(secretBytes);

        var device = new Device
        {
            DeviceId = req.DeviceId,
            StoreId = req.StoreId,
            SharedSecret = sharedSecret,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _db.Devices.Add(device);
        _db.AuditEvents.Add(new AuditEvent
        {
            JobId = Guid.Empty,
            Type = Domain.Enums.AuditEventType.DeviceRegistered,
            MetaJson = $"{{\"deviceId\":\"{req.DeviceId}\",\"storeId\":\"{req.StoreId}\"}}",
            CreatedAtUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        return Ok(new
        {
            deviceId = device.DeviceId,
            storeId = device.StoreId,
            sharedSecret,
            createdAtUtc = device.CreatedAtUtc
        });
    }

    /// <summary>
    /// Deactivate a device. It will be rejected by auth middleware from this point on.
    /// PATCH /api/v1/admin/devices/{deviceId}/deactivate
    /// </summary>
    [HttpPatch("devices/{deviceId}/deactivate")]
    public async Task<IActionResult> DeactivateDevice(string deviceId)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId)
            ?? throw new DomainException(ErrorCodes.ValidationError, "Device not found.", httpStatus: 404);

        device.IsActive = false;
        device.UpdatedAtUtc = DateTime.UtcNow;

        _db.AuditEvents.Add(new AuditEvent
        {
            JobId = Guid.Empty,
            Type = Domain.Enums.AuditEventType.DeviceDeactivated,
            MetaJson = $"{{\"deviceId\":\"{deviceId}\"}}",
            CreatedAtUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
        return Ok(new { deviceId, isActive = false });
    }

    /// <summary>
    /// List all devices with heartbeat + normalized printer health and active alerts.
    /// GET /api/v1/admin/devices
    /// </summary>
    [HttpGet("devices")]
    public async Task<IActionResult> ListDevices()
    {
        var now = DateTime.UtcNow;
        var devices = await _db.Devices
            .AsNoTracking()
            .OrderBy(d => d.CreatedAtUtc)
            .ToListAsync();

        var result = devices.Select(d => new
        {
            d.DeviceId,
            d.StoreId,
            d.IsActive,
            d.LastHeartbeatUtc,
            d.CreatedAtUtc,
            isOnline = d.LastHeartbeatUtc != null && d.LastHeartbeatUtc > now.AddMinutes(-2),
            printerHealth = new
            {
                d.PrinterModel,
                connectionState = d.PrinterConnectionState ?? "UNKNOWN",
                operationalState = d.PrinterOperationalState ?? "UNKNOWN",
                d.PrinterPaperOut,
                d.PrinterDoorOpen,
                d.PrinterCartridgeMissing,
                inkState = d.PrinterInkState ?? "UNKNOWN",
                d.PrinterStatusUpdatedAtUtc
            },
            alerts = BuildAlerts(d, now)
            // Never return SharedSecret in list endpoint
        });

        return Ok(result);
    }

    /// <summary>
    /// List active printer alerts across devices for store/staff monitoring UI.
    /// GET /api/v1/admin/devices/alerts
    /// </summary>
    [HttpGet("devices/alerts")]
    public async Task<IActionResult> ListDeviceAlerts()
    {
        var now = DateTime.UtcNow;
        var devices = await _db.Devices
            .AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.StoreId)
            .ThenBy(d => d.DeviceId)
            .ToListAsync();

        var alerts = devices
            .SelectMany(d => BuildAlerts(d, now).Select(a => new
            {
                d.DeviceId,
                d.StoreId,
                alertCode = a.Code,
                message = a.Message,
                severity = a.Severity,
                observedAtUtc = now
            }))
            .ToList();

        return Ok(alerts);
    }

    /// <summary>
    /// Create a new store location.
    /// POST /api/v1/admin/stores
    /// </summary>
    [HttpPost("stores")]
    public async Task<IActionResult> CreateStore([FromBody] CreateStoreRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.StoreId))
            throw new DomainException(ErrorCodes.ValidationError, "StoreId is required.", httpStatus: 422);

        var existing = await _db.Stores.AnyAsync(s => s.StoreId == req.StoreId);
        if (existing)
        {
            throw new DomainException(
                ErrorCodes.ValidationError,
                $"Store '{req.StoreId}' already exists.",
                httpStatus: 409
            );
        }

        var store = new Store
        {
            StoreId = req.StoreId,
            Name = req.Name,
            Address = req.Address,
            Latitude = req.Latitude,
            Longitude = req.Longitude,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _db.Stores.Add(store);
        _db.AuditEvents.Add(new AuditEvent
        {
            JobId = Guid.Empty,
            Type = Domain.Enums.AuditEventType.StoreCreated,
            MetaJson = $"{{\"storeId\":\"{req.StoreId}\",\"name\":\"{req.Name}\"}}",
            CreatedAtUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
        return Ok(store);
    }

    /// <summary>
    /// List all stores.
    /// GET /api/v1/admin/stores
    /// </summary>
    [HttpGet("stores")]
    public async Task<IActionResult> ListStores()
    {
        var stores = await _db.Stores
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync();

        return Ok(stores);
    }

    private sealed record DeviceAlert(string Code, string Message, string Severity);

    private static IReadOnlyList<DeviceAlert> BuildAlerts(Device device, DateTime nowUtc)
    {
        var alerts = new List<DeviceAlert>();
        var isDeviceOnline = device.LastHeartbeatUtc != null && device.LastHeartbeatUtc > nowUtc.AddMinutes(-2);

        if (!isDeviceOnline)
            alerts.Add(new DeviceAlert("DEVICE_OFFLINE", "Device heartbeat is stale or missing.", "CRITICAL"));

        if (string.Equals(device.PrinterConnectionState, "OFFLINE", StringComparison.OrdinalIgnoreCase))
            alerts.Add(new DeviceAlert("PRINTER_OFFLINE", "Printer is offline.", "CRITICAL"));

        if (string.Equals(device.PrinterOperationalState, "ERROR", StringComparison.OrdinalIgnoreCase))
            alerts.Add(new DeviceAlert("PRINTER_ERROR", "Printer reported an error state.", "CRITICAL"));

        if (device.PrinterPaperOut is true)
            alerts.Add(new DeviceAlert("PAPER_OUT", "Printer is out of paper.", "CRITICAL"));

        if (device.PrinterDoorOpen is true)
            alerts.Add(new DeviceAlert("DOOR_OPEN", "Printer door/cover is open.", "CRITICAL"));

        if (device.PrinterCartridgeMissing is true)
            alerts.Add(new DeviceAlert("CARTRIDGE_MISSING", "Printer cartridge is missing.", "CRITICAL"));

        if (device.PrinterStatusUpdatedAtUtc != null && device.PrinterStatusUpdatedAtUtc < nowUtc.AddMinutes(-2))
            alerts.Add(new DeviceAlert("PRINTER_STATUS_STALE", "Printer telemetry is stale.", "WARNING"));

        if (string.Equals(device.PrinterInkState, "LOW", StringComparison.OrdinalIgnoreCase))
            alerts.Add(new DeviceAlert("INK_LOW", "Ink level is low.", "WARNING"));
        else if (string.Equals(device.PrinterInkState, "VERY_LOW", StringComparison.OrdinalIgnoreCase))
            alerts.Add(new DeviceAlert("INK_VERY_LOW", "Ink level is very low.", "WARNING"));
        else if (string.Equals(device.PrinterInkState, "EMPTY", StringComparison.OrdinalIgnoreCase))
            alerts.Add(new DeviceAlert("INK_EMPTY", "Ink is empty.", "CRITICAL"));

        return alerts;
    }
}

public sealed record RegisterDeviceRequest(string DeviceId, string? StoreId);
public sealed record CreateStoreRequest(
    string StoreId, string Name, string Address, double Latitude, double Longitude);
