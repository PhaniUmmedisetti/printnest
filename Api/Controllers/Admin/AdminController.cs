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
                d.PrinterStatusUpdatedAtUtc,
                inkPrediction = BuildInkPrediction(d, now)
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
                isBlocking = a.IsBlocking,
                firstObservedAtUtc = a.FirstObservedAtUtc,
                escalatesAtUtc = a.EscalatesAtUtc,
                isEscalated = a.IsEscalated,
                inkPrediction = a.InkPrediction,
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

    private sealed record InkPrediction(
        DateTime EstimatedEmptyWindowStartUtc,
        DateTime EstimatedEmptyWindowEndUtc,
        int RemainingMinutesLower,
        int RemainingMinutesUpper,
        string Confidence,
        int BaselineSamples);

    private sealed record DeviceAlert(
        string Code,
        string Message,
        string Severity,
        bool IsBlocking,
        DateTime? FirstObservedAtUtc,
        DateTime? EscalatesAtUtc,
        bool IsEscalated,
        InkPrediction? InkPrediction);

    private static IReadOnlyList<DeviceAlert> BuildAlerts(Device device, DateTime nowUtc)
    {
        var alerts = new List<DeviceAlert>();
        var isDeviceOnline = device.LastHeartbeatUtc != null && device.LastHeartbeatUtc > nowUtc.AddMinutes(-2);
        var inkPrediction = BuildInkPrediction(device, nowUtc);

        if (!isDeviceOnline)
        {
            var firstObserved = device.LastHeartbeatUtc?.AddMinutes(2);
            alerts.Add(CreateAlert(
                code: "DEVICE_OFFLINE",
                message: "Device heartbeat is stale or missing.",
                severity: "CRITICAL",
                firstObservedAtUtc: firstObserved,
                nowUtc: nowUtc));
        }

        if (string.Equals(device.PrinterConnectionState, "OFFLINE", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(CreateAlert(
                code: "PRINTER_OFFLINE",
                message: "Printer is offline.",
                severity: "CRITICAL",
                firstObservedAtUtc: device.PrinterOfflineSinceUtc,
                nowUtc: nowUtc));
        }

        if (string.Equals(device.PrinterOperationalState, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(CreateAlert(
                code: "PRINTER_ERROR",
                message: "Printer reported an error state.",
                severity: "CRITICAL",
                firstObservedAtUtc: device.PrinterErrorSinceUtc,
                nowUtc: nowUtc));
        }

        if (device.PrinterPaperOut is true)
        {
            alerts.Add(CreateAlert(
                code: "PAPER_OUT",
                message: "Printer is out of paper.",
                severity: "BLOCKING",
                firstObservedAtUtc: device.PrinterPaperOutSinceUtc,
                nowUtc: nowUtc));
        }

        if (device.PrinterDoorOpen is true)
        {
            alerts.Add(CreateAlert(
                code: "DOOR_OPEN",
                message: "Printer door/cover is open.",
                severity: "BLOCKING",
                firstObservedAtUtc: device.PrinterDoorOpenSinceUtc,
                nowUtc: nowUtc));
        }

        if (device.PrinterCartridgeMissing is true)
        {
            alerts.Add(CreateAlert(
                code: "CARTRIDGE_MISSING",
                message: "Printer cartridge is missing.",
                severity: "BLOCKING",
                firstObservedAtUtc: device.PrinterCartridgeMissingSinceUtc,
                nowUtc: nowUtc));
        }

        if (device.PrinterStatusUpdatedAtUtc != null && device.PrinterStatusUpdatedAtUtc < nowUtc.AddMinutes(-2))
        {
            alerts.Add(CreateAlert(
                code: "PRINTER_STATUS_STALE",
                message: "Printer telemetry is stale.",
                severity: "WARNING",
                firstObservedAtUtc: device.PrinterStatusUpdatedAtUtc.Value.AddMinutes(2),
                nowUtc: nowUtc));
        }

        if (string.Equals(device.PrinterInkState, "LOW", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(CreateAlert(
                code: "INK_LOW",
                message: "Ink level is low.",
                severity: "WARNING",
                firstObservedAtUtc: device.PrinterInkStateChangedAtUtc,
                nowUtc: nowUtc,
                inkPrediction: inkPrediction));
        }
        else if (string.Equals(device.PrinterInkState, "VERY_LOW", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(CreateAlert(
                code: "INK_VERY_LOW",
                message: "Ink level is very low.",
                severity: "CRITICAL",
                firstObservedAtUtc: device.PrinterInkStateChangedAtUtc,
                nowUtc: nowUtc,
                inkPrediction: inkPrediction));
        }
        else if (string.Equals(device.PrinterInkState, "EMPTY", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(CreateAlert(
                code: "INK_EMPTY",
                message: "Ink is empty.",
                severity: "BLOCKING",
                firstObservedAtUtc: device.PrinterInkStateChangedAtUtc,
                nowUtc: nowUtc));
        }

        return alerts;
    }

    private static DeviceAlert CreateAlert(
        string code,
        string message,
        string severity,
        DateTime? firstObservedAtUtc,
        DateTime nowUtc,
        InkPrediction? inkPrediction = null)
    {
        var escalatesAtUtc = ComputeEscalatesAt(firstObservedAtUtc, severity);
        var isEscalated = escalatesAtUtc != null && nowUtc >= escalatesAtUtc.Value;

        return new DeviceAlert(
            Code: code,
            Message: message,
            Severity: severity,
            IsBlocking: string.Equals(severity, "BLOCKING", StringComparison.OrdinalIgnoreCase),
            FirstObservedAtUtc: firstObservedAtUtc,
            EscalatesAtUtc: escalatesAtUtc,
            IsEscalated: isEscalated,
            InkPrediction: inkPrediction);
    }

    private static DateTime? ComputeEscalatesAt(DateTime? firstObservedAtUtc, string severity)
    {
        if (firstObservedAtUtc is null)
            return null;

        return severity.ToUpperInvariant() switch
        {
            "WARNING" => firstObservedAtUtc.Value.AddMinutes(10),
            "CRITICAL" => firstObservedAtUtc.Value.AddMinutes(5),
            "BLOCKING" => firstObservedAtUtc,
            _ => null
        };
    }

    private static InkPrediction? BuildInkPrediction(Device device, DateTime nowUtc)
    {
        if (!string.Equals(device.PrinterInkState, "LOW", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(device.PrinterInkState, "VERY_LOW", StringComparison.OrdinalIgnoreCase))
            return null;

        var lowSince = device.PrinterInkLowSinceUtc ?? device.PrinterInkStateChangedAtUtc;
        if (lowSince is null)
            return null;

        var hasBaseline = device.PrinterLowToEmptyAvgMinutes is > 0;
        var baselineMinutes = hasBaseline
            ? device.PrinterLowToEmptyAvgMinutes!.Value
            : 180d; // default fallback before enough field data exists

        if (string.Equals(device.PrinterInkState, "VERY_LOW", StringComparison.OrdinalIgnoreCase))
            baselineMinutes *= 0.35d;

        var confidence = ConfidenceFromSamples(device.PrinterLowToEmptySamples);
        var variance = confidence switch
        {
            "HIGH" => 0.15d,
            "MEDIUM" => 0.30d,
            _ => 0.45d
        };

        var lowEndMinutes = Math.Max(1, baselineMinutes * (1d - variance));
        var highEndMinutes = Math.Max(lowEndMinutes, baselineMinutes * (1d + variance));

        var etaStart = lowSince.Value.AddMinutes(lowEndMinutes);
        var etaEnd = lowSince.Value.AddMinutes(highEndMinutes);

        var remainingLower = (int)Math.Max(0, Math.Ceiling((etaStart - nowUtc).TotalMinutes));
        var remainingUpper = (int)Math.Max(0, Math.Ceiling((etaEnd - nowUtc).TotalMinutes));

        return new InkPrediction(
            EstimatedEmptyWindowStartUtc: etaStart,
            EstimatedEmptyWindowEndUtc: etaEnd,
            RemainingMinutesLower: remainingLower,
            RemainingMinutesUpper: remainingUpper,
            Confidence: confidence,
            BaselineSamples: device.PrinterLowToEmptySamples);
    }

    private static string ConfidenceFromSamples(int samples)
    {
        if (samples >= 5) return "HIGH";
        if (samples >= 2) return "MEDIUM";
        return "LOW";
    }
}

public sealed record RegisterDeviceRequest(string DeviceId, string? StoreId);
public sealed record CreateStoreRequest(
    string StoreId, string Name, string Address, double Latitude, double Longitude);
