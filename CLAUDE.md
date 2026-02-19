# PrintNest — AI Context File

This file gives any AI coding assistant (Claude, Codex, Gemini, etc.) everything
needed to understand and extend this codebase without reading every file.

---

## What This Is

A privacy-first, release-based print network backend.

Users upload PDFs from their phone, pay, and get a 6-digit OTP.
At a store kiosk (Raspberry Pi + touchscreen), they enter the OTP.
The file downloads to the Pi, prints via CUPS, then is permanently deleted.

No file ever leaves the user's control. No file is stored after printing.

---

## Tech Stack

- .NET 8 Web API
- PostgreSQL (via EF Core + Npgsql)
- MinIO (S3-compatible local storage)
- JWT (file tokens, HS256)
- HMAC-SHA256 (device authentication)
- Argon2id (OTP hashing)
- Docker Compose (local dev)

---

## Project Structure

Single .csproj, layered by folder:

```
Domain/           ← Business rules. No dependencies on other layers.
  Entities/       ← PrintJob, Device, Store, AuditEvent, UsedFileToken
  Enums/          ← JobStatus, AuditEventType
  StateMachine/   ← JobStateMachine.cs — ALL state transitions live here
  Errors/         ← DomainException, ErrorCodes (all API error codes)

Application/      ← Use cases. Depends on Domain + Infrastructure interfaces.
  Commands/       ← One command class per use case
  Interfaces/     ← IStorageService, ITokenService, IDeviceAuthService, IOtpService, IAuditService

Infrastructure/   ← External systems. Implements Application interfaces.
  Persistence/    ← AppDbContext (EF Core), AuditService
  Auth/           ← HmacDeviceAuthService, JwtTokenService, OtpService
  Storage/        ← MinioStorageService
  Workers/        ← ExpiryWorker, CleanupWorker (IHostedService)

Api/              ← HTTP layer. Thin controllers, middleware only.
  Controllers/
    Public/       ← Customer endpoints (no auth)
    Device/       ← Device endpoints (HMAC auth via DeviceAuthMiddleware)
    Admin/        ← Admin endpoints (API key via AdminAuthMiddleware)
  Middleware/     ← ErrorHandlingMiddleware, DeviceAuthMiddleware, AdminAuthMiddleware
```

---

## The State Machine (Most Important File)

`Domain/StateMachine/JobStateMachine.cs`

**NEVER set PrintJob.Status directly.** Always call:
```csharp
JobStateMachine.Transition(job, JobStatus.TargetState, actor: "user|device|worker");
```

Valid transitions:
```
Draft → Uploaded              (user: finalize upload)
Uploaded → Quoted             (user: request quote)
Quoted → Paid                 (user: mock pay)
Paid → Released               (device: OTP validated)
Released → Downloading        (device: file download started)
Downloading → Printing        (device: CUPS job submitted)
Printing → Completed          (device: CUPS success)
Printing → Failed             (device: CUPS failure)
Completed → Deleted           (worker: MinIO delete succeeded)
Failed → Deleted              (worker: MinIO delete succeeded)
Expired → Deleted             (worker: MinIO delete succeeded)
Draft → Expired               (worker: abandoned before upload, 24h)
Uploaded → Expired            (worker: uploaded but never paid, 24h)
Quoted → Expired              (worker: quoted but never paid, 24h)
Paid → Expired                (worker: 7-day lifetime exceeded)
Released → Expired            (worker: stuck > 10 min)
Downloading → Failed          (worker: stuck > 10 min watchdog)
Printing → Failed             (worker: stuck > 10 min watchdog)
```

Any other transition throws `DomainException(ErrorCodes.JobStateInvalid)`.

---

## Error Format (Always)

```json
{ "error": { "code": "ERROR_CODE", "message": "Human readable" } }
```

All codes in `Domain/Errors/ErrorCodes.cs`. Never invent new error code strings inline.

---

## API Endpoints

### Public (no auth) — /api/v1/public/
| Method | Path | Purpose |
|--------|------|---------|
| POST | /printjobs | Create job + get presigned upload URL |
| POST | /printjobs/{id}/finalize | Confirm file uploaded |
| POST | /printjobs/{id}/quote | Set options + get price |
| POST | /printjobs/{id}/pay-mock | Mock payment → Paid |
| POST | /printjobs/{id}/otp/generate | Generate/regenerate OTP |
| GET | /printjobs/{id} | Get job status |
| GET | /stores | Get active stores for map |

### Device (HMAC auth) — /api/v1/device/
| Method | Path | Purpose |
|--------|------|---------|
| POST | /heartbeat | Update LastHeartbeatUtc |
| POST | /release | Enter OTP → get file token |
| GET | /printjobs/{id}/file | Download file (Bearer token) |
| POST | /printjobs/{id}/printing-started | CUPS job submitted |
| POST | /printjobs/{id}/completed | Print success |
| POST | /printjobs/{id}/failed | Print failure |

### Admin (X-Admin-Key header) — /api/v1/admin/
| Method | Path | Purpose |
|--------|------|---------|
| POST | /devices | Register new device |
| PATCH | /devices/{id}/deactivate | Deactivate device |
| GET | /devices | List all devices |
| POST | /stores | Create store |
| GET | /stores | List all stores |

---

## Device Authentication

Every device request must include:
```
X-Device-Id: dev_storeid_random8
X-Timestamp: 1708956123          (Unix seconds)
X-Signature: <hex HMAC-SHA256>
```

Signature = HMACSHA256(SharedSecret, `{timestamp}\n{METHOD}\n{path}\n{bodyHash}`)

- bodyHash = lowercase hex SHA256 of raw body bytes
- Empty body bodyHash = `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`
- Timestamp drift > 5 minutes → rejected

---

## Key Business Rules

1. **OTP**: 6-digit numeric, Argon2id hashed, 6h expiry, single-use, rate-limited 6 attempts/min/device
2. **File token**: JWT HS256, 120s TTL, device-bound, single-use (JTI in UsedFileTokens table)
3. **Double-release prevention**: EF Core optimistic concurrency on Status column
4. **File deletion**: Cleanup worker handles it. If MinIO fails → retry next run (status check guards safety)
5. **Stuck jobs**: ExpiryWorker watchdog — Released > 10 min → Expired; Downloading/Printing > 10 min → Failed
6. **Abandoned jobs**: Draft/Uploaded/Quoted older than 24h → Expired (files in MinIO are then deleted)
7. **OTP never logged**: Search codebase for "OtpHash" — never appears in any log call

---

## Adding a New Feature (Checklist)

1. Does it need a new state? → Add to `Domain/Enums/JobStatus.cs` + transition in `JobStateMachine.cs`
2. Does it talk to an external system? → Add interface in `Application/Interfaces/`, implement in `Infrastructure/`
3. Is it a user action? → Add command in `Application/Commands/`
4. Does it need an audit trail? → Call `IAuditService.RecordAsync()` in the command, before `SaveChangesAsync()`
5. Does it need an API endpoint? → Add to the appropriate controller in `Api/Controllers/`
6. Register any new services in `Program.cs`
7. Add the migration: `dotnet ef migrations add YourMigrationName`

---

## Running Locally

```bash
cp infra/.env.example infra/.env
# Edit infra/.env with real values

docker-compose up
# API: http://localhost:5000
# MinIO console: http://localhost:9001
# Swagger: http://localhost:5000/swagger
```

---

## Testing the Full Flow

**Public + Admin endpoints** → open `printnest.http` in VS Code (REST Client extension)

**Device flow** (requires HMAC signing — use the simulator):
```bash
# 1. Register a device (run once)
ADMIN_API_KEY=your-key bash tools/provision-device.sh dev_store1_01 store_id

# 2. Run public flow (Steps 3–8 in printnest.http) to get an OTP, then:
bash tools/simulate-device.sh dev_store1_01 "<base64-secret>" <otp-code>
# Downloads file to /tmp/printnest-<jobId>.pdf and runs full flow to Completed
```

---

## Adding EF Migrations

```bash
dotnet ef migrations add InitialSchema --output-dir Infrastructure/Persistence/Migrations
dotnet ef database update
```

---

## Security Rules (Never Violate)

- Never log OTP plaintext or hash
- Never log file content or object keys in user-visible errors
- Never reveal job existence in OTP failure responses (always "Invalid code.")
- SharedSecret never returned in list endpoints
- All device auth failures return the same generic 401
- Admin key uses constant-time comparison
