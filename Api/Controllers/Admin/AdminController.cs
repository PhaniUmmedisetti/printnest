using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintNest.Api.Middleware;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Entities;
using PrintNest.Domain.Enums;
using PrintNest.Domain.Errors;
using PrintNest.Infrastructure.Persistence;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;

namespace PrintNest.Api.Controllers.Admin;

/// <summary>
/// Admin/staff endpoints. Protected by StaffAuthMiddleware (JWT bearer).
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
    private static readonly JobStatus[] ActiveQueueStatuses = [JobStatus.Released, JobStatus.Downloading, JobStatus.Printing];
    private readonly AppDbContext _db;
    private readonly IStaffPasswordService _passwords;

    public AdminController(AppDbContext db, IStaffPasswordService passwords)
    {
        _db = db;
        _passwords = passwords;
    }

    /// <summary>
    /// Register a new device. Called by the provisioning script (tools/provision-device.sh).
    /// POST /api/v1/admin/devices
    ///
    /// Returns the generated SharedSecret - store it securely, it won't be shown again.
    /// </summary>
    [HttpPost("devices")]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest req)
    {
        EnsureSuperAdmin(GetAuthenticatedStaff());

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
            MetaJson = JsonSerializer.Serialize(new { deviceId = req.DeviceId, storeId = req.StoreId ?? string.Empty }),
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
        EnsureSuperAdmin(GetAuthenticatedStaff());

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId)
            ?? throw new DomainException(ErrorCodes.ValidationError, "Device not found.", httpStatus: 404);

        device.IsActive = false;
        device.UpdatedAtUtc = DateTime.UtcNow;

        _db.AuditEvents.Add(new AuditEvent
        {
            JobId = Guid.Empty,
            Type = Domain.Enums.AuditEventType.DeviceDeactivated,
            MetaJson = JsonSerializer.Serialize(new { deviceId }),
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
        var staff = GetAuthenticatedStaff();
        var now = DateTime.UtcNow;
        var devices = await ApplyStoreScope(_db.Devices.AsNoTracking(), staff)
            .OrderBy(d => d.CreatedAtUtc)
            .ToListAsync();
        var jobsByDevice = await LoadJobsByDeviceAsync(devices);

        var result = devices.Select(d => new
        {
            d.DeviceId,
            d.StoreId,
            d.IsActive,
            d.LastHeartbeatUtc,
            d.CreatedAtUtc,
            isOnline = IsDeviceOnline(d, now),
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
            alerts = BuildAlerts(d, now, jobsByDevice[d.DeviceId])
                .Select(a => ToAlertResponse(d, a, now))
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
        var snapshot = await LoadOpsSnapshotAsync(GetAuthenticatedStaff(), activeOnly: true);
        return Ok(snapshot.Alerts);
    }

    [HttpGet("ops/summary")]
    public async Task<IActionResult> GetOpsSummary()
    {
        var snapshot = await LoadOpsSnapshotAsync(GetAuthenticatedStaff(), activeOnly: true);
        var alerts = snapshot.Alerts;
        var devices = snapshot.Devices;

        var totals = new
        {
            kiosks = devices.Count,
            online = devices.Count(d => IsDeviceOnline(d, snapshot.GeneratedAtUtc)),
            offline = devices.Count(d => !IsDeviceOnline(d, snapshot.GeneratedAtUtc)),
            incidents = alerts.Count,
            blocking = alerts.Count(a => a.Severity == "BLOCKING"),
            critical = alerts.Count(a => a.Severity == "CRITICAL"),
            warning = alerts.Count(a => a.Severity == "WARNING"),
            queueBacklog = alerts.Count(a => a.AlertCode == "JOB_QUEUE_BACKLOG"),
            failureTrend = alerts.Count(a => a.AlertCode == "PRINT_FAILURE_TREND"),
            flapping = alerts.Count(a => a.AlertCode == "CONNECTION_FLAPPING")
        };

        var stores = devices
            .GroupBy(d => d.StoreId ?? "unassigned")
            .OrderBy(g => g.Key)
            .Select(group =>
            {
                var storeAlerts = alerts.Where(a => string.Equals(a.StoreId ?? "unassigned", group.Key, StringComparison.Ordinal)).ToList();
                return new
                {
                    storeId = group.Key,
                    deviceCount = group.Count(),
                    onlineDevices = group.Count(d => IsDeviceOnline(d, snapshot.GeneratedAtUtc)),
                    incidentCount = storeAlerts.Count,
                    blocking = storeAlerts.Count(a => a.Severity == "BLOCKING"),
                    critical = storeAlerts.Count(a => a.Severity == "CRITICAL"),
                    warning = storeAlerts.Count(a => a.Severity == "WARNING"),
                    topAlertCodes = storeAlerts
                        .GroupBy(a => a.AlertCode)
                        .OrderByDescending(a => a.Count())
                        .ThenBy(a => a.Key)
                        .Take(3)
                        .Select(a => a.Key)
                };
            });

        return Ok(new
        {
            generatedAtUtc = snapshot.GeneratedAtUtc,
            totals,
            stores
        });
    }

    /// <summary>
    /// Create a new store location.
    /// POST /api/v1/admin/stores
    /// </summary>
    [HttpPost("stores")]
    public async Task<IActionResult> CreateStore([FromBody] CreateStoreRequest req)
    {
        EnsureSuperAdmin(GetAuthenticatedStaff());

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
            MetaJson = JsonSerializer.Serialize(new { storeId = req.StoreId, name = req.Name }),
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
        var staff = GetAuthenticatedStaff();
        var stores = await ApplyStoreScope(_db.Stores.AsNoTracking(), staff)
            .OrderBy(s => s.Name)
            .ToListAsync();

        return Ok(stores);
    }

    /// <summary>
    /// Create staff user accounts (super admin only).
    /// POST /api/v1/admin/staff-users
    /// </summary>
    [HttpPost("staff-users")]
    public async Task<IActionResult> CreateStaffUser([FromBody] CreateStaffUserRequest req)
    {
        EnsureSuperAdmin(GetAuthenticatedStaff());

        if (string.IsNullOrWhiteSpace(req.Username) ||
            string.IsNullOrWhiteSpace(req.DisplayName) ||
            string.IsNullOrWhiteSpace(req.Password))
        {
            throw new DomainException(ErrorCodes.ValidationError, "Username, displayName, and password are required.", 422);
        }

        if (req.Password.Length < 10)
            throw new DomainException(ErrorCodes.ValidationError, "Password must be at least 10 characters.", 422);

        var role = req.Role.Trim().ToUpperInvariant();
        if (!StaffRoles.IsValid(role))
            throw new DomainException(ErrorCodes.ValidationError, "Role must be SUPER_ADMIN or STORE_MANAGER.", 422);

        if (role == StaffRoles.StoreManager && string.IsNullOrWhiteSpace(req.StoreId))
            throw new DomainException(ErrorCodes.ValidationError, "Store manager must have a storeId.", 422);

        if (!string.IsNullOrWhiteSpace(req.StoreId))
        {
            var storeExists = await _db.Stores.AnyAsync(s => s.StoreId == req.StoreId);
            if (!storeExists)
                throw new DomainException(ErrorCodes.ValidationError, "Store does not exist.", 422);
        }

        var normalizedUsername = req.Username.Trim().ToLowerInvariant();
        var exists = await _db.StaffUsers.AnyAsync(x => x.Username.ToLower() == normalizedUsername);
        if (exists)
            throw new DomainException(ErrorCodes.ValidationError, "Username is already in use.", 409);

        var user = new StaffUser
        {
            StaffUserId = Guid.NewGuid(),
            Username = req.Username.Trim(),
            DisplayName = req.DisplayName.Trim(),
            PasswordHash = _passwords.Hash(req.Password),
            Role = role,
            StoreId = role == StaffRoles.SuperAdmin ? null : req.StoreId,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _db.StaffUsers.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            user.StaffUserId,
            user.Username,
            user.DisplayName,
            user.Role,
            user.StoreId,
            user.IsActive
        });
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
        InkPrediction? InkPrediction,
        string Category,
        bool IsDerived);

    private sealed record OpsAlertResponse(
        string DeviceId,
        string? StoreId,
        string AlertCode,
        string Message,
        string Severity,
        bool IsBlocking,
        DateTime? FirstObservedAtUtc,
        DateTime? EscalatesAtUtc,
        bool IsEscalated,
        InkPrediction? InkPrediction,
        string Category,
        bool IsDerived,
        DateTime ObservedAtUtc);

    private sealed record OpsSnapshot(DateTime GeneratedAtUtc, List<Device> Devices, List<OpsAlertResponse> Alerts);

    private static IReadOnlyList<DeviceAlert> BuildAlerts(Device device, DateTime nowUtc, IEnumerable<PrintJob> jobs)
    {
        var alerts = new List<DeviceAlert>();
        var isDeviceOnline = IsDeviceOnline(device, nowUtc);
        var inkPrediction = BuildInkPrediction(device, nowUtc);

        if (!isDeviceOnline)
        {
            var firstObserved = device.LastHeartbeatUtc?.AddMinutes(2);
            alerts.Add(CreateAlert(
                code: "DEVICE_OFFLINE",
                message: "Device heartbeat is stale or missing.",
                severity: "CRITICAL",
                firstObservedAtUtc: firstObserved,
                nowUtc: nowUtc,
                category: "connectivity"));
        }

        if (!isDeviceOnline && device.LastHeartbeatUtc is null && device.CreatedAtUtc <= nowUtc.AddMinutes(-5))
        {
            alerts.Add(CreateAlert(
                code: "NO_TELEMETRY_EVER",
                message: "Device was registered but has never sent heartbeat telemetry.",
                severity: "CRITICAL",
                firstObservedAtUtc: device.CreatedAtUtc,
                nowUtc: nowUtc,
                category: "watchdog",
                isDerived: true));
        }

        if (isDeviceOnline && device.PrinterStatusUpdatedAtUtc is null && device.LastHeartbeatUtc is not null)
        {
            alerts.Add(CreateAlert(
                code: "PRINTER_TELEMETRY_MISSING",
                message: "Device heartbeat is active but printer telemetry payload is missing.",
                severity: "WARNING",
                firstObservedAtUtc: device.LastHeartbeatUtc,
                nowUtc: nowUtc,
                category: "watchdog",
                isDerived: true));
        }

        if (string.Equals(device.PrinterConnectionState, "OFFLINE", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(CreateAlert(
                code: "PRINTER_OFFLINE",
                message: "Printer is offline.",
                severity: "CRITICAL",
                firstObservedAtUtc: device.PrinterOfflineSinceUtc,
                nowUtc: nowUtc,
                category: "connectivity"));
        }

        if (device.PrinterConnectionFlapWindowStartedAtUtc is not null &&
            device.PrinterConnectionFlapWindowStartedAtUtc >= nowUtc.AddMinutes(-15) &&
            device.PrinterConnectionFlapTransitions >= 4)
        {
            alerts.Add(CreateAlert(
                code: "CONNECTION_FLAPPING",
                message: $"Printer connection changed state {device.PrinterConnectionFlapTransitions} times in the last 15 minutes.",
                severity: "CRITICAL",
                firstObservedAtUtc: device.PrinterConnectionFlapWindowStartedAtUtc,
                nowUtc: nowUtc,
                category: "stability",
                isDerived: true));
        }

        if (string.Equals(device.PrinterOperationalState, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(CreateAlert(
                code: "PRINTER_ERROR",
                message: "Printer reported an error state.",
                severity: "CRITICAL",
                firstObservedAtUtc: device.PrinterErrorSinceUtc,
                nowUtc: nowUtc,
                category: "printer"));
        }

        if (device.PrinterPaperOut is true)
        {
            alerts.Add(CreateAlert(
                code: "PAPER_OUT",
                message: "Printer is out of paper.",
                severity: "BLOCKING",
                firstObservedAtUtc: device.PrinterPaperOutSinceUtc,
                nowUtc: nowUtc,
                category: "consumable"));
        }

        if (device.PrinterDoorOpen is true)
        {
            alerts.Add(CreateAlert(
                code: "DOOR_OPEN",
                message: "Printer door or cover is open.",
                severity: "BLOCKING",
                firstObservedAtUtc: device.PrinterDoorOpenSinceUtc,
                nowUtc: nowUtc,
                category: "printer"));
        }

        if (device.PrinterCartridgeMissing is true)
        {
            alerts.Add(CreateAlert(
                code: "CARTRIDGE_MISSING",
                message: "Printer cartridge is missing.",
                severity: "BLOCKING",
                firstObservedAtUtc: device.PrinterCartridgeMissingSinceUtc,
                nowUtc: nowUtc,
                category: "consumable"));
        }

        if (device.PrinterStatusUpdatedAtUtc != null && device.PrinterStatusUpdatedAtUtc < nowUtc.AddMinutes(-2))
        {
            alerts.Add(CreateAlert(
                code: "PRINTER_STATUS_STALE",
                message: "Printer telemetry is stale.",
                severity: "WARNING",
                firstObservedAtUtc: device.PrinterStatusUpdatedAtUtc.Value.AddMinutes(2),
                nowUtc: nowUtc,
                category: "watchdog"));
        }

        if (string.Equals(device.PrinterInkState, "LOW", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(CreateAlert(
                code: "INK_LOW",
                message: "Ink level is low.",
                severity: "WARNING",
                firstObservedAtUtc: device.PrinterInkStateChangedAtUtc,
                nowUtc: nowUtc,
                category: "consumable",
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
                category: "consumable",
                inkPrediction: inkPrediction));
        }
        else if (string.Equals(device.PrinterInkState, "EMPTY", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(CreateAlert(
                code: "INK_EMPTY",
                message: "Ink is empty.",
                severity: "BLOCKING",
                firstObservedAtUtc: device.PrinterInkStateChangedAtUtc,
                nowUtc: nowUtc,
                category: "consumable"));
        }

        if (inkPrediction is not null &&
            string.Equals(inkPrediction.Confidence, "LOW", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(device.PrinterInkState, "LOW", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(device.PrinterInkState, "VERY_LOW", StringComparison.OrdinalIgnoreCase)))
        {
            alerts.Add(CreateAlert(
                code: "INK_PREDICTION_LOW_CONFIDENCE",
                message: "Ink ETA is available but confidence is low due to limited history.",
                severity: "WARNING",
                firstObservedAtUtc: device.PrinterInkStateChangedAtUtc,
                nowUtc: nowUtc,
                category: "prediction",
                inkPrediction: inkPrediction,
                isDerived: true));
        }

        var activeQueue = jobs
            .Where(j => ActiveQueueStatuses.Contains(j.Status))
            .OrderBy(AlertAnchorUtc)
            .ToList();

        if (activeQueue.Count >= 3)
        {
            var firstObserved = AlertAnchorUtc(activeQueue[0]);
            var oldestMinutes = (nowUtc - firstObserved).TotalMinutes;
            var severity = activeQueue.Count >= 5 || oldestMinutes >= 20 ? "CRITICAL" : "WARNING";
            alerts.Add(CreateAlert(
                code: "JOB_QUEUE_BACKLOG",
                message: $"{activeQueue.Count} jobs are queued or still in progress on this kiosk.",
                severity: severity,
                firstObservedAtUtc: firstObserved,
                nowUtc: nowUtc,
                category: "queue",
                isDerived: true));
        }

        var recentFailures = jobs
            .Where(j => j.Status == JobStatus.Failed && j.UpdatedAtUtc >= nowUtc.AddMinutes(-15))
            .OrderBy(j => j.UpdatedAtUtc)
            .ToList();

        if (recentFailures.Count >= 3)
        {
            alerts.Add(CreateAlert(
                code: "PRINT_FAILURE_TREND",
                message: $"{recentFailures.Count} print jobs failed on this kiosk in the last 15 minutes.",
                severity: "CRITICAL",
                firstObservedAtUtc: recentFailures[0].UpdatedAtUtc,
                nowUtc: nowUtc,
                category: "trend",
                isDerived: true));
        }

        return alerts
            .GroupBy(a => a.Code, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => SeverityRank(x.Severity)).ThenBy(x => x.FirstObservedAtUtc).First())
            .OrderByDescending(a => SeverityRank(a.Severity))
            .ThenBy(a => a.FirstObservedAtUtc ?? nowUtc)
            .ToList();
    }

    private static DeviceAlert CreateAlert(
        string code,
        string message,
        string severity,
        DateTime? firstObservedAtUtc,
        DateTime nowUtc,
        string category,
        InkPrediction? inkPrediction = null,
        bool isDerived = false)
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
            InkPrediction: inkPrediction,
            Category: category,
            IsDerived: isDerived);
    }

    private static OpsAlertResponse ToAlertResponse(Device device, DeviceAlert alert, DateTime observedAtUtc)
    {
        return new OpsAlertResponse(
            DeviceId: device.DeviceId,
            StoreId: device.StoreId,
            AlertCode: alert.Code,
            Message: alert.Message,
            Severity: alert.Severity,
            IsBlocking: alert.IsBlocking,
            FirstObservedAtUtc: alert.FirstObservedAtUtc,
            EscalatesAtUtc: alert.EscalatesAtUtc,
            IsEscalated: alert.IsEscalated,
            InkPrediction: alert.InkPrediction,
            Category: alert.Category,
            IsDerived: alert.IsDerived,
            ObservedAtUtc: observedAtUtc);
    }

    private static DateTime AlertAnchorUtc(PrintJob job)
    {
        return job.ReleaseLockUtc ?? job.UpdatedAtUtc;
    }

    private static bool IsDeviceOnline(Device device, DateTime nowUtc)
    {
        return device.LastHeartbeatUtc != null && device.LastHeartbeatUtc > nowUtc.AddMinutes(-2);
    }

    private static int SeverityRank(string severity)
    {
        return severity.ToUpperInvariant() switch
        {
            "BLOCKING" => 3,
            "CRITICAL" => 2,
            "WARNING" => 1,
            _ => 0
        };
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

    private async Task<OpsSnapshot> LoadOpsSnapshotAsync(AuthenticatedStaffContext staff, bool activeOnly)
    {
        var now = DateTime.UtcNow;
        var deviceQuery = ApplyStoreScope(_db.Devices.AsNoTracking(), staff);
        if (activeOnly)
            deviceQuery = deviceQuery.Where(d => d.IsActive);

        var devices = await deviceQuery
            .OrderBy(d => d.StoreId)
            .ThenBy(d => d.DeviceId)
            .ToListAsync();

        var jobsByDevice = await LoadJobsByDeviceAsync(devices);

        var alerts = devices
            .SelectMany(d => BuildAlerts(d, now, jobsByDevice[d.DeviceId])
                .Select(a => ToAlertResponse(d, a, now)))
            .ToList();

        return new OpsSnapshot(now, devices, alerts);
    }

    private async Task<ILookup<string, PrintJob>> LoadJobsByDeviceAsync(IReadOnlyCollection<Device> devices)
    {
        var deviceIds = devices.Select(d => d.DeviceId).ToHashSet(StringComparer.Ordinal);
        if (deviceIds.Count == 0)
            return Array.Empty<PrintJob>().ToLookup(j => j.AssignedDeviceId!, StringComparer.Ordinal);

        var jobs = await _db.PrintJobs.AsNoTracking()
            .Where(j => j.AssignedDeviceId != null && deviceIds.Contains(j.AssignedDeviceId))
            .ToListAsync();

        return jobs.ToLookup(j => j.AssignedDeviceId!, StringComparer.Ordinal);
    }

    private AuthenticatedStaffContext GetAuthenticatedStaff()
    {
        return HttpContext.Items["AuthenticatedStaff"] as AuthenticatedStaffContext
            ?? throw new DomainException(ErrorCodes.AdminUnauthorized, "Unauthorized.", 401);
    }

    private static void EnsureSuperAdmin(AuthenticatedStaffContext staff)
    {
        if (!string.Equals(staff.Role, StaffRoles.SuperAdmin, StringComparison.Ordinal))
            throw new DomainException(ErrorCodes.AdminForbidden, "Forbidden.", 403);
    }

    private static IQueryable<Device> ApplyStoreScope(IQueryable<Device> query, AuthenticatedStaffContext staff)
        => ApplyStoreScopeCore(query, staff, e => e.StoreId);

    private static IQueryable<Store> ApplyStoreScope(IQueryable<Store> query, AuthenticatedStaffContext staff)
        => ApplyStoreScopeCore(query, staff, e => e.StoreId);

    private static IQueryable<T> ApplyStoreScopeCore<T>(
        IQueryable<T> query,
        AuthenticatedStaffContext staff,
        Expression<Func<T, string?>> storeIdSelector)
    {
        if (string.Equals(staff.Role, StaffRoles.SuperAdmin, StringComparison.Ordinal))
            return query;

        if (string.IsNullOrWhiteSpace(staff.StoreId))
            return query.Where(_ => false);

        var param = storeIdSelector.Parameters[0];
        var body = Expression.Equal(storeIdSelector.Body, Expression.Constant(staff.StoreId, typeof(string)));
        return query.Where(Expression.Lambda<Func<T, bool>>(body, param));
    }
}

public sealed record RegisterDeviceRequest(string DeviceId, string? StoreId);
public sealed record CreateStoreRequest(
    string StoreId, string Name, string Address, double Latitude, double Longitude);
public sealed record CreateStaffUserRequest(
    string Username,
    string DisplayName,
    string Password,
    string Role,
    string? StoreId);
