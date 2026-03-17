using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintNest.Application.Commands;
using PrintNest.Application.Interfaces;
using PrintNest.Domain.Entities;
using PrintNest.Domain.Errors;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Api.Controllers.DeviceApi;

/// <summary>
/// Device-facing endpoints. All requests authenticated via HMAC (DeviceAuthMiddleware).
///
/// Base route: /api/v1/device
///
/// The authenticated device is available via HttpContext.Items["AuthenticatedDevice"].
/// Controllers verify that the device in the request body matches the authenticated device.
/// </summary>
[ApiController]
[Route("api/v1/device")]
public sealed class DeviceController : ControllerBase
{
    private readonly ReleaseJobCommand _releaseJob;
    private readonly MarkDownloadingCommand _markDownloading;
    private readonly MarkPrintingCommand _markPrinting;
    private readonly CompleteJobCommand _completeJob;
    private readonly FailJobCommand _failJob;
    private readonly IStorageService _storage;
    private readonly ITokenService _token;
    private readonly AppDbContext _db;

    public DeviceController(
        ReleaseJobCommand releaseJob,
        MarkDownloadingCommand markDownloading,
        MarkPrintingCommand markPrinting,
        CompleteJobCommand completeJob,
        FailJobCommand failJob,
        IStorageService storage,
        ITokenService token,
        AppDbContext db)
    {
        _releaseJob = releaseJob;
        _markDownloading = markDownloading;
        _markPrinting = markPrinting;
        _completeJob = completeJob;
        _failJob = failJob;
        _storage = storage;
        _token = token;
        _db = db;
    }

    /// <summary>
    /// Device heartbeat — updates LastHeartbeatUtc and capabilities.
    /// POST /api/v1/device/heartbeat
    /// </summary>
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] HeartbeatRequest req)
    {
        var device = GetAuthenticatedDevice();
        var now = DateTime.UtcNow;
        device.LastHeartbeatUtc = now;
        device.UpdatedAtUtc = now;

        if (req.StoreId is not null) device.StoreId = req.StoreId;
        if (req.CapabilitiesJson is not null) device.CapabilitiesJson = req.CapabilitiesJson;
        if (req.PrinterHealth is not null)
        {
            var previousConnectionState = device.PrinterConnectionState;
            var previousOperationalState = device.PrinterOperationalState;
            var previousPaperOut = device.PrinterPaperOut;
            var previousDoorOpen = device.PrinterDoorOpen;
            var previousCartridgeMissing = device.PrinterCartridgeMissing;
            var previousInkState = device.PrinterInkState;

            device.PrinterModel = req.PrinterHealth.PrinterModel?.Trim();
            device.PrinterConnectionState = NormalizePrinterConnectionState(req.PrinterHealth.ConnectionState);
            device.PrinterOperationalState = NormalizePrinterOperationalState(req.PrinterHealth.OperationalState);
            device.PrinterPaperOut = req.PrinterHealth.PaperOut;
            device.PrinterDoorOpen = req.PrinterHealth.DoorOpen;
            device.PrinterCartridgeMissing = req.PrinterHealth.CartridgeMissing;
            device.PrinterInkState = NormalizePrinterInkState(req.PrinterHealth.InkState);
            device.PrinterRawStatusJson = req.PrinterHealth.RawStatusJson;
            device.PrinterStatusUpdatedAtUtc = now;
            UpdateConnectionFlappingWindow(device, previousConnectionState, now);

            device.PrinterOfflineSinceUtc = TrackActiveSince(
                previousConnectionState,
                device.PrinterConnectionState,
                activeState: "OFFLINE",
                currentSince: device.PrinterOfflineSinceUtc,
                nowUtc: now);

            device.PrinterErrorSinceUtc = TrackActiveSince(
                previousOperationalState,
                device.PrinterOperationalState,
                activeState: "ERROR",
                currentSince: device.PrinterErrorSinceUtc,
                nowUtc: now);

            device.PrinterPaperOutSinceUtc = TrackActiveSince(
                previousPaperOut,
                device.PrinterPaperOut,
                currentSince: device.PrinterPaperOutSinceUtc,
                nowUtc: now);

            device.PrinterDoorOpenSinceUtc = TrackActiveSince(
                previousDoorOpen,
                device.PrinterDoorOpen,
                currentSince: device.PrinterDoorOpenSinceUtc,
                nowUtc: now);

            device.PrinterCartridgeMissingSinceUtc = TrackActiveSince(
                previousCartridgeMissing,
                device.PrinterCartridgeMissing,
                currentSince: device.PrinterCartridgeMissingSinceUtc,
                nowUtc: now);

            if (!string.Equals(previousInkState, device.PrinterInkState, StringComparison.OrdinalIgnoreCase))
            {
                device.PrinterInkStateChangedAtUtc = now;
            }

            var currentInkState = device.PrinterInkState ?? "UNKNOWN";
            var wasLowFamily = IsLowFamily(previousInkState);
            var isLowFamily = IsLowFamily(currentInkState);

            if (isLowFamily && !wasLowFamily)
                device.PrinterInkLowSinceUtc = now;

            if (!isLowFamily && !string.Equals(currentInkState, "EMPTY", StringComparison.OrdinalIgnoreCase))
                device.PrinterInkLowSinceUtc = null;

            if (string.Equals(currentInkState, "EMPTY", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(previousInkState, "EMPTY", StringComparison.OrdinalIgnoreCase))
            {
                UpdateLowToEmptyBaseline(device, now);
            }
        }

        // LastHeartbeatUtc on the Device entity already records device liveness.
        // No AuditEvent row — heartbeats are routine telemetry, not security-relevant events.
        // Auditing every heartbeat would add ~28,000 rows/day per 10 devices (one per 30s).
        await _db.SaveChangesAsync();

        return Ok(new { serverTimeUtc = DateTime.UtcNow });
    }

    /// <summary>
    /// Device enters OTP → job released to this device + file token issued.
    /// POST /api/v1/device/release
    /// </summary>
    [HttpPost("release")]
    public async Task<IActionResult> Release([FromBody] ReleaseRequest req)
    {
        var device = GetAuthenticatedDevice();

        var result = await _releaseJob.ExecuteAsync(new ReleaseJobCommand.Input(
            DeviceId: device.DeviceId,
            StoreId: req.StoreId ?? device.StoreId,
            OtpPlaintext: req.Otp
        ));

        return Ok(new
        {
            jobId = result.JobId,
            status = result.Status,
            jobSummary = new
            {
                copies = result.Summary.Copies,
                color = result.Summary.Color,
                priceCents = result.Summary.PriceCents,
                currency = result.Summary.Currency
            },
            fileToken = new
            {
                token = result.FileToken,
                expiresInSeconds = result.FileTokenExpiresInSeconds
            }
        });
    }

    /// <summary>
    /// Device downloads the file. Token-gated + single-use.
    /// GET /api/v1/device/printjobs/{jobId}/file
    /// Authorization: Bearer {fileToken}
    /// </summary>
    [HttpGet("printjobs/{jobId:guid}/file")]
    public async Task<IActionResult> DownloadFile(Guid jobId)
    {
        var device = GetAuthenticatedDevice();

        // ── Validate Bearer token ─────────────────────────────────
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new DomainException(ErrorCodes.TokenInvalid, "File token required.", httpStatus: 401);

        var tokenString = authHeader["Bearer ".Length..].Trim();
        var claims = _token.ValidateFileToken(tokenString);

        // ── Token must match this job and this device ─────────────
        if (claims.JobId != jobId)
            throw new DomainException(ErrorCodes.TokenInvalid, "Token job mismatch.", httpStatus: 401);

        if (!string.Equals(claims.DeviceId, device.DeviceId, StringComparison.Ordinal))
            throw new DomainException(ErrorCodes.DeviceUnauthorized, "Token device mismatch.", httpStatus: 401);

        // ── Check JTI has not been used (single-use) ─────────────
        var jtiUsed = await _db.UsedFileTokens.AnyAsync(t => t.Jti == claims.Jti);
        if (jtiUsed)
            throw new DomainException(ErrorCodes.TokenAlreadyUsed, "File token has already been used.", httpStatus: 401);

        // ── Load job — must be in Released state ──────────────────
        var job = await _db.PrintJobs.FirstOrDefaultAsync(j => j.JobId == jobId)
            ?? throw new DomainException(ErrorCodes.JobNotFound, "Job not found.", httpStatus: 404);

        if (job.AssignedDeviceId != device.DeviceId)
            throw new DomainException(ErrorCodes.DeviceUnauthorized, "Job not assigned to this device.", httpStatus: 403);

        if (job.Status != Domain.Enums.JobStatus.Released)
            throw new DomainException(ErrorCodes.JobStateInvalid, "Job is no longer in a downloadable state.", httpStatus: 409);

        if (string.IsNullOrWhiteSpace(job.ObjectKey))
            throw new DomainException(
                ErrorCodes.StorageError,
                "Job file is unavailable for download.",
                httpStatus: 409
            );

        // ── Consume JTI + transition to Downloading (atomic) ─────
        _db.UsedFileTokens.Add(new UsedFileToken
        {
            Jti = claims.Jti,
            JobId = jobId,
            DeviceId = device.DeviceId,
            UsedAtUtc = DateTime.UtcNow
        });

        await _markDownloading.ExecuteAsync(jobId, device.DeviceId);
        // Note: MarkDownloadingCommand calls SaveChangesAsync — JTI insert is in the same save

        // ── Stream file ───────────────────────────────────────────
        Response.ContentType = "application/pdf";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{jobId}.pdf\"";
        await _storage.StreamFileAsync(job.ObjectKey!, Response.Body, HttpContext.RequestAborted);

        return new EmptyResult();
    }

    /// <summary>
    /// Device reports CUPS job submitted — printing in progress.
    /// POST /api/v1/device/printjobs/{jobId}/printing-started
    /// </summary>
    [HttpPost("printjobs/{jobId:guid}/printing-started")]
    public async Task<IActionResult> PrintingStarted(Guid jobId, [FromBody] PrintingStartedRequest req)
    {
        var device = GetAuthenticatedDevice();

        await _markPrinting.ExecuteAsync(new MarkPrintingCommand.Input(
            jobId, device.DeviceId, req.CupsJobId, req.PrinterName));

        return Ok(new { status = "Printing" });
    }

    /// <summary>
    /// Device reports successful print completion.
    /// POST /api/v1/device/printjobs/{jobId}/completed
    /// </summary>
    [HttpPost("printjobs/{jobId:guid}/completed")]
    public async Task<IActionResult> Completed(Guid jobId, [FromBody] CompletedRequest req)
    {
        var device = GetAuthenticatedDevice();

        await _completeJob.ExecuteAsync(new CompleteJobCommand.Input(
            jobId, device.DeviceId, req.CupsJobId, req.Metrics));

        return Ok(new { status = "Completed" });
    }

    /// <summary>
    /// Device reports print failure.
    /// POST /api/v1/device/printjobs/{jobId}/failed
    /// </summary>
    [HttpPost("printjobs/{jobId:guid}/failed")]
    public async Task<IActionResult> Failed(Guid jobId, [FromBody] FailedRequest req)
    {
        var device = GetAuthenticatedDevice();

        await _failJob.ExecuteAsync(new FailJobCommand.Input(
            jobId,
            device.DeviceId,
            req.CupsJobId,
            req.FailureCode,
            req.FailureMessage,
            req.IsRetryable
        ));

        return Ok(new { status = "Failed" });
    }

    // ── Helper ────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves the device authenticated by DeviceAuthMiddleware.
    /// Throws if called outside of the device middleware pipeline (should never happen).
    /// </summary>
    private Device GetAuthenticatedDevice()
    {
        return HttpContext.Items["AuthenticatedDevice"] as Device
            ?? throw new DomainException(ErrorCodes.DeviceUnauthorized, "Unauthorized.", 401);
    }

    private static DateTime? TrackActiveSince(
        string? previousState,
        string? currentState,
        string activeState,
        DateTime? currentSince,
        DateTime nowUtc)
    {
        var wasActive = string.Equals(previousState, activeState, StringComparison.OrdinalIgnoreCase);
        var isActive = string.Equals(currentState, activeState, StringComparison.OrdinalIgnoreCase);

        if (isActive && !wasActive)
            return nowUtc;

        if (!isActive)
            return null;

        return currentSince ?? nowUtc;
    }

    private static DateTime? TrackActiveSince(
        bool? previousValue,
        bool? currentValue,
        DateTime? currentSince,
        DateTime nowUtc)
    {
        var wasActive = previousValue is true;
        var isActive = currentValue is true;

        if (isActive && !wasActive)
            return nowUtc;

        if (!isActive)
            return null;

        return currentSince ?? nowUtc;
    }

    private static bool IsLowFamily(string? inkState) =>
        inkState?.ToUpperInvariant() is "LOW" or "VERY_LOW";

    private static void UpdateConnectionFlappingWindow(Device device, string? previousConnectionState, DateTime nowUtc)
    {
        var currentState = device.PrinterConnectionState;
        var transitioned = IsConnectionState(previousConnectionState)
            && IsConnectionState(currentState)
            && !string.Equals(previousConnectionState, currentState, StringComparison.OrdinalIgnoreCase);

        if (device.PrinterConnectionFlapWindowStartedAtUtc is null ||
            device.PrinterConnectionFlapWindowStartedAtUtc < nowUtc.AddMinutes(-15))
        {
            device.PrinterConnectionFlapWindowStartedAtUtc = nowUtc;
            device.PrinterConnectionFlapTransitions = transitioned ? 1 : 0;
            return;
        }

        if (transitioned)
            device.PrinterConnectionFlapTransitions += 1;
    }

    private static bool IsConnectionState(string? state) =>
        state?.ToUpperInvariant() is "ONLINE" or "OFFLINE";

    private static void UpdateLowToEmptyBaseline(Device device, DateTime nowUtc)
    {
        if (device.PrinterInkLowSinceUtc is not { } lowSince)
            return;

        var elapsedMinutes = (nowUtc - lowSince).TotalMinutes;
        if (elapsedMinutes <= 0)
            return;

        var samples = device.PrinterLowToEmptySamples;
        var currentAvg = device.PrinterLowToEmptyAvgMinutes;

        var newAvg = samples > 0 && currentAvg is not null
            ? ((currentAvg.Value * samples) + elapsedMinutes) / (samples + 1)
            : elapsedMinutes;

        device.PrinterLowToEmptyAvgMinutes = newAvg;
        device.PrinterLowToEmptySamples = samples + 1;
    }

    private static readonly string[] ConnectionStates   = ["ONLINE", "OFFLINE"];
    private static readonly string[] OperationalStates  = ["IDLE", "PRINTING", "ERROR"];
    private static readonly string[] InkStates          = ["OK", "LOW", "VERY_LOW", "EMPTY"];

    private static string NormalizePrinterConnectionState(string? state)  => NormalizePrinterState(state, ConnectionStates);
    private static string NormalizePrinterOperationalState(string? state) => NormalizePrinterState(state, OperationalStates);
    private static string NormalizePrinterInkState(string? state)         => NormalizePrinterState(state, InkStates);

    private static string NormalizePrinterState(string? state, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(state)) return "UNKNOWN";
        var normalized = state.Trim().ToUpperInvariant();
        return Array.IndexOf(allowed, normalized) >= 0 ? normalized : "UNKNOWN";
    }
}

// ── Request models ────────────────────────────────────────────────────────────

public sealed record HeartbeatRequest(string? StoreId, string? CapabilitiesJson, PrinterHealthRequest? PrinterHealth);
public sealed record PrinterHealthRequest(
    string? PrinterModel,
    string? ConnectionState,
    string? OperationalState,
    bool? PaperOut,
    bool? DoorOpen,
    bool? CartridgeMissing,
    string? InkState,
    string? RawStatusJson);
public sealed record ReleaseRequest(string Otp, string? StoreId);
public sealed record PrintingStartedRequest(string? CupsJobId, string? PrinterName);
public sealed record CompletedRequest(string? CupsJobId, object? Metrics);
public sealed record FailedRequest(string? CupsJobId, string? FailureCode, string? FailureMessage, bool IsRetryable);
