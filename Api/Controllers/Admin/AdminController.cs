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
///   - Store management
///   - Device deactivation
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminController(AppDbContext db) => _db = db;

    // ── Devices ───────────────────────────────────────────────────

    /// <summary>
    /// Register a new device. Called by the provisioning script (tools/provision-device.sh).
    /// POST /api/v1/admin/devices
    ///
    /// Returns the generated SharedSecret — store it securely, it won't be shown again.
    /// </summary>
    [HttpPost("devices")]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest req)
    {
        // Validate device ID format: dev_{anything}
        if (string.IsNullOrWhiteSpace(req.DeviceId) || !req.DeviceId.StartsWith("dev_"))
            throw new DomainException(
                ErrorCodes.ValidationError,
                "DeviceId must start with 'dev_'. Example: dev_store1_abc12345",
                httpStatus: 422
            );

        var existing = await _db.Devices.AnyAsync(d => d.DeviceId == req.DeviceId);
        if (existing)
            throw new DomainException(
                ErrorCodes.ValidationError,
                $"Device '{req.DeviceId}' is already registered.",
                httpStatus: 409
            );

        // Generate a cryptographically secure shared secret
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
            sharedSecret,  // returned ONCE — write to Pi .env immediately
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
    /// List all devices with their status.
    /// GET /api/v1/admin/devices
    /// </summary>
    [HttpGet("devices")]
    public async Task<IActionResult> ListDevices()
    {
        var devices = await _db.Devices
            .AsNoTracking()
            .OrderBy(d => d.CreatedAtUtc)
            .Select(d => new
            {
                d.DeviceId,
                d.StoreId,
                d.IsActive,
                d.LastHeartbeatUtc,
                d.CreatedAtUtc,
                IsOnline = d.LastHeartbeatUtc != null &&
                           d.LastHeartbeatUtc > DateTime.UtcNow.AddMinutes(-2)
                // Never return SharedSecret in list endpoint
            })
            .ToListAsync();

        return Ok(devices);
    }

    // ── Stores ────────────────────────────────────────────────────

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
            throw new DomainException(
                ErrorCodes.ValidationError,
                $"Store '{req.StoreId}' already exists.",
                httpStatus: 409
            );

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
    /// List all active stores. Used by the customer map view.
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
}

// ── Request models ────────────────────────────────────────────────────────────

public sealed record RegisterDeviceRequest(string DeviceId, string? StoreId);
public sealed record CreateStoreRequest(
    string StoreId, string Name, string Address, double Latitude, double Longitude);
