# PrintNest — Complete AI Handoff Document

This document captures every decision, every line of logic, every business rule, and every
implementation detail of the PrintNest backend. Any AI coding assistant reading this should
be able to pick up the project in full fidelity without reading any other file first.

---

## !! AI ASSISTANT — READ THIS FIRST !!

### Session Bookmark Protocol

**On every session start:**
1. Read the `## CURRENT BOOKMARK` section at the bottom of this file.
2. Tell the user exactly where work was left off, what was completed, and what is next.
3. Do this before asking any questions or writing any code.

**When the user says any of these (or similar):**
> "stop", "bookmark this", "that's it for today", "calling it a day",
> "save progress", "end session", "pause here", "note this down"

→ Immediately update the `## CURRENT BOOKMARK` section at the bottom of this file with:
- **Date** (today's date)
- **Completed this session** — bullet list of everything finished
- **Stopped at** — the exact task/file/step where work paused (be specific: file name, function, test name, etc.)
- **Next step** — the single next action to take when resuming
- **Pending decisions** — anything the user needs to decide before work can continue
- **Context notes** — any non-obvious state (e.g. "migration not yet run", "env var missing", "test failing for known reason")

Update the bookmark section in-place — overwrite the previous bookmark. Only the latest matters.

---

## 1. What Is This Product

**PrintNest** is a privacy-first print kiosk network.

**User flow:**
1. Customer opens the PrintNest mobile app
2. Selects a nearby store on a map (OpenStreetMap, stores fetched from API)
3. Uploads a PDF (max 20MB)
4. Configures options: copies (1–100), color (currently B&W only; Color = "Coming Soon" / disabled)
5. Receives a mock price: ₹2 per copy, minimum ₹5
6. Completes mock payment (real payment is a future phase — currently a no-op)
7. Taps "Generate OTP" → receives a 6-digit numeric code, valid for 6 hours
8. Walks to the store kiosk (Raspberry Pi 4 + 7" touchscreen + USB printer)
9. Types the 6-digit OTP on the kiosk
10. Kiosk downloads the file, submits it to CUPS for printing
11. File is permanently deleted from storage once printing is confirmed

**Core privacy guarantee:** The file only exists in MinIO storage between upload and the moment
the cleanup worker runs after printing. After Completed → Deleted, the file is gone forever.
The API server never stores the file on disk — all file transit is streamed directly.

**The system is live only in India (INR currency). MVP targets store locations in Hyderabad.**

---

## 2. Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 8 Web API (C#) |
| Database | PostgreSQL 16 via EF Core 8 + Npgsql |
| File storage | MinIO (S3-compatible, self-hosted) |
| OTP hashing | Argon2id (Isopoh.Cryptography.Argon2) |
| File token | JWT HS256 (System.IdentityModel.Tokens.Jwt) |
| Device auth | HMAC-SHA256 with custom signed headers |
| Containerization | Docker Compose (postgres + minio + minio-init + api) |
| API docs | Swagger (Swashbuckle, dev only) |

---

## 3. Architecture — Single Project, Layered by Folder

The entire backend is **one .csproj** (`printnest.csproj`). No microservices, no separate class libraries.
Layers are enforced by folder convention, not by project references.

```
Domain/           ← Pure business rules. Zero dependencies on other layers.
  Entities/       ← PrintJob, Device, Store, AuditEvent, UsedFileToken
  Enums/          ← JobStatus, AuditEventType
  StateMachine/   ← JobStateMachine.cs — THE ONLY place status changes happen
  Errors/         ← DomainException, ErrorCodes

Application/      ← Use cases. Depends on Domain + Infrastructure interfaces only.
  Commands/       ← One sealed class per use case
  Interfaces/     ← IStorageService, ITokenService, IDeviceAuthService, IOtpService, IAuditService

Infrastructure/   ← Concrete external system wrappers.
  Persistence/    ← AppDbContext (EF Core), AuditService
  Auth/           ← HmacDeviceAuthService, JwtTokenService, OtpService
  Storage/        ← MinioStorageService
  Workers/        ← ExpiryWorker, CleanupWorker (IHostedService)

Api/              ← HTTP boundary. Thin controllers, middleware only.
  Controllers/
    Public/       ← Customer endpoints (no auth)
    Device/       ← Device endpoints (HMAC auth via DeviceAuthMiddleware)
    Admin/        ← Admin endpoints (X-Admin-Key via AdminAuthMiddleware)
  Middleware/     ← ErrorHandlingMiddleware, DeviceAuthMiddleware, AdminAuthMiddleware

tools/            ← Bash scripts for operators
  provision-device.sh   ← Registers a Pi with the API, writes .env
  simulate-device.sh    ← Simulates full Pi device flow for testing

Program.cs        ← DI registration, middleware pipeline, startup migration
docker-compose.yml
Dockerfile        ← Multi-stage build → non-root aspnet:8.0 runtime
infra/.env.example
printnest.http    ← VS Code REST Client test file (all public + admin flows)
CLAUDE.md         ← AI context file (always read this first)
```

---

## 4. Domain Entities — Every Field

### PrintJob
```csharp
Guid     JobId            // PK, gen_random_uuid() default
JobStatus Status          // IsConcurrencyToken() — EF includes this in UPDATE WHERE clause
string?  ObjectKey        // "jobs/{jobId}.pdf" — set at CreateJob time, never changes
string?  Sha256           // SHA256 hex of the uploaded file (set at FinalizeUpload)
string?  OptionsJson      // JSONB: {"copies":2,"color":"BW"} — set at Quote
int      PriceCents       // calculated at Quote (copies × 200, min 500)
string   Currency         // always "INR"

// OTP fields — NEVER logged, NEVER returned in API responses
string?  OtpHash          // Argon2id hash — nulled when Released or Expired
DateTime? OtpExpiryUtc   // 6h from generation — nulled when Released or Expired
int      OtpAttempts      // always 0 in MVP (per-job locking not yet active)
DateTime? OtpLastAttemptUtc
DateTime? OtpLockedUntilUtc  // reserved for future per-job locking feature

// Assignment — set atomically at Release
string?  AssignedDeviceId  // device that claimed this job via OTP
string?  AssignedStoreId   // store where printing happens
DateTime? ReleaseLockUtc  // when the OTP was consumed

// Lifecycle
DateTime? PrintedAtUtc    // set when Printing → Completed
DateTime? DeletedAtUtc    // set when any terminal → Deleted
bool     DeletePending    // true if MinIO delete failed, retry next CleanupWorker run

DateTime CreatedAtUtc
DateTime UpdatedAtUtc
```

### Device
```csharp
string   DeviceId         // PK, format: "dev_{anything}" enforced by validation
string?  StoreId          // which store this Pi lives at
string   SharedSecret     // plaintext base64 (32 raw bytes) — stored plaintext for HMAC
DateTime? LastHeartbeatUtc
string?  CapabilitiesJson // JSONB: Pi can report printer capabilities
bool     IsActive         // false = device rejected by auth middleware
DateTime CreatedAtUtc
DateTime UpdatedAtUtc
```

### Store
```csharp
string   StoreId
string   Name
string?  Address
double?  Latitude         // WGS84 decimal degrees
double?  Longitude
bool     IsActive
DateTime CreatedAtUtc
DateTime UpdatedAtUtc
```

### AuditEvent (append-only log)
```csharp
long            Id          // bigserial — bigint auto-increment
Guid            JobId       // Guid.Empty for device/admin events not tied to a job
AuditEventType  Type        // stored as string
string?         MetaJson    // JSONB — arbitrary context, never contains OTP/secret
DateTime        CreatedAtUtc
```

### UsedFileToken (single-use JTI tracking)
```csharp
string   Jti         // PK — compact GUID without dashes (Guid.NewGuid().ToString("N"))
Guid     JobId
string   DeviceId
DateTime UsedAtUtc
```

---

## 5. Job Status Enum

```
Draft       — job created, presigned upload URL issued, file not yet in MinIO
Uploaded    — file confirmed in MinIO, options not yet set
Quoted      — options set, price calculated, awaiting payment
Paid        — mock payment complete, OTP can now be generated
Released    — OTP validated by device, file token issued, device identified
Downloading — device is downloading the file (file token consumed)
Printing    — CUPS job submitted, printing in progress
Completed   — CUPS reports success
Failed      — CUPS reports failure, or watchdog timeout
Expired     — job timed out (see transition table)
Deleted     — file deleted from MinIO (terminal state, never changes again)
```

---

## 6. The State Machine — Most Critical File

**File:** `Domain/StateMachine/JobStateMachine.cs`

**ABSOLUTE RULE:** Never write `job.Status = someValue` directly anywhere in the codebase.
Always use:
```csharp
JobStateMachine.Transition(job, JobStatus.TargetState, actor: "user|device|worker");
```

If the transition is not in `AllowedTransitions`, it throws `DomainException(JobStateInvalid, 409)`.

### Complete Allowed Transitions Table

```
Draft     → Uploaded      actor: "user"   — FinalizeUploadCommand, after MinIO verify
Uploaded  → Quoted        actor: "user"   — QuoteJobCommand
Quoted    → Paid          actor: "user"   — PayJobCommand (mock)
Paid      → Released      actor: "device" — ReleaseJobCommand (OTP match)
Released  → Downloading   actor: "device" — MarkDownloadingCommand (auto, on file request)
Downloading → Printing    actor: "device" — MarkPrintingCommand
Printing  → Completed     actor: "device" — CompleteJobCommand
Printing  → Failed        actor: "device" — FailJobCommand

Completed → Deleted       actor: "worker" — CleanupWorker (file deleted from MinIO)
Failed    → Deleted       actor: "worker" — CleanupWorker
Expired   → Deleted       actor: "worker" — CleanupWorker

Draft     → Expired       actor: "worker" — ExpiryWorker (24h abandonment)
Uploaded  → Expired       actor: "worker" — ExpiryWorker (24h abandonment)
Quoted    → Expired       actor: "worker" — ExpiryWorker (24h abandonment)
Paid      → Expired       actor: "worker" — ExpiryWorker (7-day job lifetime)
Released  → Expired       actor: "worker" — ExpiryWorker (stuck > 10 min, no download)
Downloading → Failed      actor: "worker" — ExpiryWorker watchdog (stuck > 10 min)
Printing  → Failed        actor: "worker" — ExpiryWorker watchdog (stuck > 10 min)
```

### ApplyGuards (pre-transition validation)
- → Released: job.OtpHash must not be null; OtpExpiryUtc must be in future; OtpLockedUntilUtc must be null or past
- → Uploaded: job.ObjectKey must not be empty
- → Quoted: job.OptionsJson must not be empty

### ApplyEffects (auto-set fields on transition)
- → Released: set ReleaseLockUtc = now; null OtpHash, OtpExpiryUtc, OtpAttempts, OtpLockedUntilUtc
- → Expired: null OtpHash, OtpExpiryUtc, OtpLockedUntilUtc (privacy cleanup)
- → Completed: set PrintedAtUtc = now
- → Deleted: set DeletedAtUtc = now

---

## 7. API Endpoints — Complete Specification

### Error Format (always, no exceptions)
```json
{ "error": { "code": "ERROR_CODE", "message": "Human readable message" } }
```
All error codes are constants in `Domain/Errors/ErrorCodes.cs`. Never inline new code strings.

### Public Endpoints — `/api/v1/public/` — No Auth

#### POST /api/v1/public/printjobs — Create Job
Request:
```json
{ "fileName": "document.pdf", "fileSizeBytes": 204800, "contentType": "application/pdf" }
```
Response:
```json
{
  "jobId": "uuid",
  "upload": {
    "method": "PUT",
    "url": "https://minio-presigned-url",
    "headers": { "contentType": "application/pdf" },
    "expiresInSeconds": 900
  }
}
```
- Validates contentType = "application/pdf", fileSizeBytes 1–20971520 (20MB)
- ObjectKey pre-computed: `jobs/{jobId}.pdf`
- Presigned PUT URL issued by MinIO SDK with 15 minute TTL
- MinIO enforces Content-Type: application/pdf on the PUT request
- Job created in Draft state

#### POST /api/v1/public/printjobs/{jobId}/finalize — Confirm Upload
Request:
```json
{ "sha256": "optional-hex-hash" }
```
Response: `{ "status": "Uploaded" }`
- Calls MinIO HeadObject to verify the file actually exists
- Stores sha256 if provided
- Transitions Draft → Uploaded

#### POST /api/v1/public/printjobs/{jobId}/quote — Set Options + Get Price
Request:
```json
{ "copies": 2, "color": "BW" }
```
Response:
```json
{ "status": "Quoted", "pricing": { "currency": "INR", "totalAmountCents": 400 } }
```
- Valid copies: 1–100
- Valid color: "BW" only (Color rejected with validation error: "Color printing is coming soon")
- Price formula: copies × 200 paise, minimum 500 paise (₹5)
- Stores OptionsJson as JSONB: `{"copies":2,"color":"BW"}`
- Transitions Uploaded → Quoted

#### POST /api/v1/public/printjobs/{jobId}/pay-mock — Mock Payment
Request: `{}`
Response: `{ "status": "Paid", "priceCents": 400, "currency": "INR" }`
- No real payment logic — MVP placeholder
- Transitions Quoted → Paid

#### POST /api/v1/public/printjobs/{jobId}/otp/generate — Generate OTP
Request: `{}`
Response:
```json
{ "otp": "482917", "expiresAtUtc": "2026-02-26T14:00:00Z" }
```
- Generates cryptographically secure 6-digit code: `RandomNumberGenerator.GetInt32(100000, 1000000)`
- Range 100000–999999 (always 6 digits, no leading zeros)
- Hashes with Argon2id (64MB memory, 3 iterations, parallelism 1, 32-byte hash)
- OTP valid for 6 hours from generation
- Calling this again generates a new OTP and invalidates the previous one (OtpAttempts reset to 0)
- Job must be in Paid state (does NOT transition state — stays Paid)

#### GET /api/v1/public/printjobs/{jobId} — Status Check
Response:
```json
{
  "jobId": "uuid",
  "status": "Paid",
  "priceCents": 400,
  "currency": "INR",
  "otpExpiresAtUtc": "2026-02-26T14:00:00Z",
  "assignedStoreId": "store_supermart_01",
  "createdAtUtc": "...",
  "updatedAtUtc": "..."
}
```
- Never returns: OtpHash, ObjectKey, AssignedDeviceId, Sha256
- Safe to poll; no side effects

#### GET /api/v1/public/stores — Active Stores for Map
Response: `[{ "storeId", "name", "address", "latitude", "longitude" }]`
- Only active stores (IsActive = true) — filter is server-side; `isActive` is NOT in the response
- Projection is explicit (5 fields only) — the full Store entity is never returned on this endpoint
- Used by customer app to render the map and let user select a store

---

### Device Endpoints — `/api/v1/device/` — HMAC Auth

**Every request must include:**
```
X-Device-Id: dev_storeid_random8
X-Timestamp: 1708956123          (Unix seconds)
X-Signature: <hex HMAC-SHA256>
```

**Signature algorithm:**
```
message = "{timestamp}\n{METHOD}\n{path}\n{bodyHash}"
bodyHash = lowercase hex SHA256 of raw body bytes
emptyBodyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
signature = HMACSHA256(base64decode(SharedSecret), UTF8(message))
```
- Timestamp drift > 300 seconds → rejected
- All auth failures return the same 401 (never reveals which check failed)
- `DeviceAuthMiddleware` calls `Request.EnableBuffering()`, reads body bytes, rewinds stream,
  then sets `HttpContext.Items["AuthenticatedDevice"] = device`

#### POST /api/v1/device/heartbeat — Device Alive Signal
Request: `{ "storeId": "store_supermart_01", "capabilitiesJson": "{...}" }`
Response: `{ "serverTimeUtc": "..." }`
- Updates `LastHeartbeatUtc` and `UpdatedAtUtc` on the Device entity
- If `storeId` or `capabilitiesJson` are non-null, updates them
- No AuditEvent generated (would create 28,000 rows/day per 10 devices — removed by design)
- Admin "IsOnline" check: `LastHeartbeatUtc > DateTime.UtcNow.AddMinutes(-2)`

#### POST /api/v1/device/release — OTP Validation → Job Release
Request: `{ "otp": "482917", "storeId": "store_supermart_01" }`
Response:
```json
{
  "jobId": "uuid",
  "status": "Released",
  "jobSummary": { "copies": 2, "color": "BW", "priceCents": 400, "currency": "INR" },
  "fileToken": { "token": "eyJ...", "expiresInSeconds": 120 }
}
```

**Full ReleaseJobCommand logic (most security-critical):**
1. Rate limit: count `OtpAttemptFailed` events with `"deviceId":"this_device"` in last 60s. If ≥6 → 429
2. Load all Paid jobs where `OtpHash != null AND OtpExpiryUtc > now AND (OtpLockedUntilUtc IS NULL OR < now)`
3. Iterate candidates: `_otp.Verify(plaintext, storedHash)` — Argon2id, ~100–300ms each
4. If no match → RecordAsync(Guid.Empty, OtpAttemptFailed, {deviceId, reason}) → SaveChanges → throw 400 "Invalid code."
5. If match:
   - `job.AssignedDeviceId = input.DeviceId`
   - `job.AssignedStoreId = input.StoreId`
   - `JobStateMachine.Transition(job, Released)` — this nulls OtpHash/OtpExpiryUtc via ApplyEffects
   - `_token.IssueFileToken(job.JobId, deviceId)` — 120s JWT
   - Audit: OtpConsumed + JobReleased
   - `SaveChangesAsync()` — if DbUpdateConcurrencyException → throw 409 "Invalid code." (LOCK_CONFLICT)
6. All failure messages are "Invalid code." — never reveals job existence

**Privacy invariants in ReleaseJobCommand:**
- Does not reveal whether the job exists
- Does not reveal whether the OTP was wrong vs. expired vs. locked
- Uses field-anchored JSON match for rate limit: `"deviceId":"dev_xxx"` not just `dev_xxx`

#### GET /api/v1/device/printjobs/{jobId}/file — Download File
Headers: Authorization: Bearer {fileToken}  (plus HMAC headers)
Response: PDF binary stream (Content-Type: application/pdf)
- Validates Bearer token (JWT signature, expiry, issuer/audience)
- Checks token.JobId == path jobId
- Checks token.DeviceId == authenticated device
- Checks JTI not in UsedFileTokens (single-use)
- Loads job, checks AssignedDeviceId == device
- **Checks job.Status == Released** (explicit status guard, not just relying on MarkDownloading)
- Adds UsedFileToken to change tracker (not yet saved)
- Calls MarkDownloadingCommand — which calls SaveChangesAsync (atomically saves JTI + status transition)
- Streams file from MinIO directly to HTTP response body (no disk on API server)

#### POST /api/v1/device/printjobs/{jobId}/printing-started
Request: `{ "cupsJobId": "12345", "printerName": "lp0" }`
Response: `{ "status": "Printing" }`
- Enforces DeviceOwnershipGuard (AssignedDeviceId must match)
- Transitions Downloading → Printing

#### POST /api/v1/device/printjobs/{jobId}/completed
Request: `{ "cupsJobId": "12345" }`
Response: `{ "status": "Completed" }`
- Enforces DeviceOwnershipGuard
- Transitions Printing → Completed
- CleanupWorker will delete file from MinIO and move to Deleted

#### POST /api/v1/device/printjobs/{jobId}/failed
Request: `{ "cupsJobId": "12345", "failureCode": "PAPER_JAM", "failureMessage": "...", "isRetryable": false }`
Response: `{ "status": "Failed" }`
- Enforces DeviceOwnershipGuard
- Transitions Printing → Failed
- CleanupWorker will delete file and move to Deleted

---

### Admin Endpoints — `/api/v1/admin/` — X-Admin-Key Header

- Key must be set in env as `ADMIN_API_KEY`, minimum 32 characters
- App throws `InvalidOperationException` on startup if key is missing or too short
- Comparison uses `CryptographicOperations.FixedTimeEquals` (constant-time for same-length inputs)
- All failures return 401 ADMIN_UNAUTHORIZED regardless of what failed

#### POST /api/v1/admin/devices — Register Device
Request: `{ "deviceId": "dev_store1_abc12345", "storeId": "store_supermart_01" }`
Response:
```json
{
  "deviceId": "dev_store1_abc12345",
  "storeId": "store_supermart_01",
  "sharedSecret": "base64string==",
  "createdAtUtc": "..."
}
```
- DeviceId must start with "dev_" (enforced)
- SharedSecret: 32 random bytes → Convert.ToBase64String (standard base64, NOT URL-safe)
- SharedSecret returned ONCE — caller (provision-device.sh) writes it to Pi .env immediately
- Duplicate DeviceId → 409

#### PATCH /api/v1/admin/devices/{deviceId}/deactivate
Response: `{ "deviceId": "...", "isActive": false }`
- Sets IsActive = false → device rejected by HmacDeviceAuthService

#### GET /api/v1/admin/devices
Returns all devices. Never returns SharedSecret. Includes computed `isOnline` field.

#### POST /api/v1/admin/stores
Request: `{ "storeId": "store_supermart_01", "name": "SuperMart Hitech City", "address": "...", "latitude": 17.4486, "longitude": 78.3908 }`

#### GET /api/v1/admin/stores
Returns all stores including inactive ones.

---

## 8. Security Mechanisms — Implementation Details

### Device Authentication (HMAC-SHA256)
File: `Infrastructure/Auth/HmacDeviceAuthService.cs`

```csharp
// Signature verification:
var bodyHash = bodyBytes.Length == 0 ? EmptyBodyHash
    : Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();

var message = $"{timestamp}\n{httpMethod.ToUpperInvariant()}\n{path}\n{bodyHash}";
var secretBytes = Convert.FromBase64String(device.SharedSecret);
using var hmac = new HMACSHA256(secretBytes);
var expectedSig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();

CryptographicOperations.FixedTimeEquals(
    Encoding.UTF8.GetBytes(expectedSig),
    Encoding.UTF8.GetBytes(signature.ToLowerInvariant()))
```

`Reject()` is a `[DoesNotReturn]` method that always throws the same 401. Never reveals which check failed.

### OTP Hashing (Argon2id)
File: `Infrastructure/Auth/OtpService.cs`

- Parameters: MemoryCost = 65536 (64MB), TimeCost = 3, Lanes/Threads = 1, HashLength = 32
- Uses `Argon2Type.HybridAddressing` = Argon2id
- Verification: `Argon2.Verify(storedHash, plaintext)` — returns bool, ~100–300ms
- Generation: `Argon2Config` → `new Argon2(config)` → `argon2.Hash()` → `config.EncodeString(hash.Buffer)`

### File Token (JWT HS256)
File: `Infrastructure/Auth/JwtTokenService.cs`

- Algorithm: HMAC-SHA256
- Issuer: "printnest", Audience: "printnest-device"
- Claims: `sub` = jobId.ToString(), `did` = deviceId, `jti` = Guid.ToString("N"), `iat` = unix seconds
- TTL: 120 seconds (configurable via `Jwt:FileTokenTtlSeconds`)
- ClockSkew = TimeSpan.Zero (strict — no grace period on 120s tokens)
- Single-use enforced via `UsedFileTokens` table — JTI inserted atomically with state transition to Downloading

### Signing Key Requirements
- JWT key: minimum 32 chars (`Jwt:SigningKey` config)
- Admin key: minimum 32 chars (`AdminApiKey` config)
- Both throw `InvalidOperationException` on startup if too short

### Double-Release Prevention
- `Status` column has `IsConcurrencyToken()` in EF Core config
- EF Core includes `WHERE status = 'Paid'` in the UPDATE query
- Two simultaneous releases: one succeeds (status → 'Released'), the other's UPDATE finds no rows → DbUpdateConcurrencyException → 409 "Invalid code."

### Privacy Rules (Never Violate)
- Never log OTP plaintext or OtpHash — search for "OtpHash" confirms it never appears in log calls
- Never reveal job existence in OTP failure responses ("Invalid code." for all failures)
- Never return OtpHash, ObjectKey, AssignedDeviceId, Sha256 in public-facing GET status
- SharedSecret never returned in list endpoints (only returned once at registration)
- All device auth failures return the same generic 401 (Reject() method)

---

## 9. Background Workers

### ExpiryWorker
File: `Infrastructure/Workers/ExpiryWorker.cs`
- Startup delay: 15 seconds (lets DB migrations complete)
- Runs every 60 seconds
- All cases use per-job try-catch — one failure doesn't stop the batch
- Single `SaveChangesAsync()` at the end covers all successfully transitioned jobs

**Case 1: Paid jobs older than 7 days → Expired**
```
WHERE Status = 'Paid' AND CreatedAtUtc < now - 7days
```

**Case 2: Released jobs stuck > 10 min → Expired**
```
WHERE Status = 'Released' AND UpdatedAtUtc < now - 10min
```
(Device claimed the job via OTP but never called the file download endpoint)

**Case 3: Downloading/Printing stuck > 10 min → Failed**
```
WHERE Status IN ('Downloading','Printing') AND UpdatedAtUtc < now - 10min
```

**Case 4: Pre-payment abandoned jobs → Expired (NEW, added in code review)**
```
WHERE Status IN ('Draft','Uploaded','Quoted') AND CreatedAtUtc < now - 24h
```
This prevents file accumulation in MinIO for users who uploaded then abandoned.

### CleanupWorker
File: `Infrastructure/Workers/CleanupWorker.cs`
- Startup delay: 30 seconds (offset from ExpiryWorker)
- Runs every 60 seconds

**Case 1: Delete files for terminal jobs**
```
WHERE DeletedAtUtc IS NULL AND ObjectKey IS NOT NULL
AND Status IN ('Completed','Failed','Expired')
```
Note: Only terminal statuses are in the query. `DeletePending` is NOT used as a standalone OR
clause (was a bug — if DeletePending were ever true on an active job, it would delete the file
for an in-flight print). Status check is the authoritative guard.

For each job:
- Call `storage.DeleteFileAsync(job.ObjectKey)` — MinIO delete (404 is idempotent)
- On success: Transition(job, Deleted) — sets DeletedAtUtc; DeletePending = false; add FileDeleted audit
- On failure: job.DeletePending = true; add FileDeleteFailed audit; continue to next job

**Case 2: JTI cleanup**
```
DELETE FROM used_file_tokens WHERE UsedAtUtc < now - 24h
```
File tokens expire in 120s, so JTI rows older than 24h are pure dead weight.

---

## 10. Infrastructure Details

### MinIO (S3-Compatible Storage)
File: `Infrastructure/Storage/MinioStorageService.cs`

- AWS SDK v3, endpoint from config (`Storage:Endpoint` e.g. `http://minio:9000`)
- `ForcePathStyle = true` (required for MinIO, not AWS)
- Bucket name: configurable via `Storage:BucketName` (default "printfiles")
- Object key format: `jobs/{jobId}.pdf`

Operations:
- `GeneratePresignedUploadUrlAsync(key, ttlSeconds)` → PUT URL, ContentType = "application/pdf"
- `VerifyObjectExistsAsync(key)` → HeadObject, throws ValidationError if 404
- `StreamFileAsync(key, responseBody, ct)` → GetObject, copies stream to response body (no disk)
- `DeleteFileAsync(key)` → DeleteObject, ignores 404 (idempotent)

### AppDbContext
File: `Infrastructure/Persistence/AppDbContext.cs`

- All column names: snake_case (configured via Fluent API, no data annotations on entities)
- All JSON fields: `HasColumnType("jsonb")` — real Postgres JSONB for efficient querying
- Status: `HasConversion<string>()` + `IsConcurrencyToken()` (string storage, not int enum)
- AuditEvent.Id: `UseIdentityAlwaysColumn()` (bigserial, no sequence gaps)

Key indexes:
- `ix_print_jobs_status` — workers filter by status constantly
- `ix_print_jobs_otp_expiry` — expiry queries
- `ix_print_jobs_assigned_device` — device owns which jobs
- `ix_print_jobs_delete_pending` — (exists, not currently used in query due to bug fix)
- `ix_print_jobs_created_at` — expiry worker cutoff queries
- `ix_audit_events_job_id` — audit log lookup by job
- `ix_used_file_tokens_used_at` — JTI cleanup cutoff

### AuditService
File: `Infrastructure/Persistence/AuditService.cs`

- Synchronous: just calls `_db.AuditEvents.Add(...)` (no SaveChanges)
- Relies on caller's `SaveChangesAsync()` for transactional correctness
- This means audit events are always within the same DB transaction as the state change

### Middleware Pipeline
Program.cs order (matters):
```
1. ErrorHandlingMiddleware    — catches ALL exceptions, converts to JSON error format
2. Swagger (dev only)
3. CORS
4. UseWhen → DeviceAuthMiddleware   (for /api/v1/device/* only)
5. UseWhen → AdminAuthMiddleware    (for /api/v1/admin/* only)
6. MapControllers
```

**Critical: `UseWhen` not `MapWhen`.**
`MapWhen` creates a parallel branch — requests processed there never reach `MapControllers`.
`UseWhen` applies middleware conditionally then REJOINS the main pipeline. This was a fixed bug.

### DeviceAuthMiddleware
File: `Api/Middleware/DeviceAuthMiddleware.cs`

- Calls `Request.EnableBuffering()` to allow body to be read twice (once here, once by controller)
- Reads raw body bytes, rewinds stream position to 0
- Calls `HmacDeviceAuthService.AuthenticateAsync(...)`
- Stores result in `HttpContext.Items["AuthenticatedDevice"]`

---

## 11. Error Codes (Complete List)
File: `Domain/Errors/ErrorCodes.cs`

```
OTP_INVALID       — wrong OTP or no matching job
OTP_EXPIRED       — OTP existed but past expiry (currently returns OTP_INVALID to attacker)
OTP_RATE_LIMITED  — >6 attempts/min from this device → 429
OTP_LOCKED        — future per-job locking (field reserved in schema)
JOB_STATE_INVALID — attempted invalid state transition → 409
JOB_NOT_FOUND     — job UUID does not exist → 404
DEVICE_UNAUTHORIZED — HMAC auth failed → 401
TOKEN_INVALID     — JWT malformed or wrong device → 401
TOKEN_EXPIRED     — JWT past 120s TTL → 401
TOKEN_ALREADY_USED — JTI already in UsedFileTokens → 401
STORAGE_ERROR     — MinIO operation failed
LOCK_CONFLICT     — optimistic concurrency (double-release) → 409
VALIDATION_ERROR  — invalid input (file type, size, etc.) → 422
ADMIN_UNAUTHORIZED — wrong or missing X-Admin-Key → 401
```

**Note:** `INTERNAL_ERROR` is used as an inline string in `ErrorHandlingMiddleware` for unhandled
exceptions that don't map to a `DomainException`. This code is **NOT** a constant in `ErrorCodes.cs`
— it is the only error code defined outside that file. In production it returns a generic message;
in Development mode it includes `ex.Message`.

---

## 12. Migrations

Two EF Core migrations in `Infrastructure/Persistence/Migrations/`:

1. `20260219183149_InitialSchema` — full schema creation
2. `20260219183644_AddConcurrencyToken` — adds xmin or status as concurrency token

Migrations run automatically on startup via `db.Database.Migrate()` in Program.cs.

To add a new migration:
```bash
dotnet ef migrations add YourMigrationName --output-dir Infrastructure/Persistence/Migrations
dotnet ef database update
```

---

## 13. Docker Compose
File: `docker-compose.yml`

Services:
- `postgres:16-alpine` — data volume persisted, healthcheck on pg_isready
- `minio:latest` — data volume persisted, healthcheck on /minio/health/live
- `minio-init` — runs `mc mb` to create the bucket on first start
- `api` — built from Dockerfile, depends_on postgres + minio with condition: service_healthy

Environment variables via `infra/.env` (copy from `infra/.env.example`):
```
POSTGRES_DB / POSTGRES_USER / POSTGRES_PASSWORD
MINIO_ROOT_USER / MINIO_ROOT_PASSWORD / MINIO_BUCKET
JWT_SIGNING_KEY         (≥32 chars)
JWT_FILE_TOKEN_TTL_SECONDS  (default 120)
ADMIN_API_KEY           (≥32 chars)
CORS_ALLOWED_ORIGINS    (comma-separated)
STORAGE_ENDPOINT        (http://minio:9000 for compose)
STORAGE_USE_HTTPS       (false for local)
```

**Critical: docker-compose env var naming uses double-underscore (`__`) for nested ASP.NET config.**
ASP.NET Core uses `:` as the config hierarchy separator (`Jwt:SigningKey`). In docker-compose.yml
environment blocks, `:` is not valid — use `__` instead:

| ASP.NET config key | docker-compose env var |
|--------------------|------------------------|
| `Jwt:SigningKey` | `Jwt__SigningKey` |
| `Jwt:FileTokenTtlSeconds` | `Jwt__FileTokenTtlSeconds` |
| `Storage:Endpoint` | `Storage__Endpoint` |
| `Storage:AccessKey` | `Storage__AccessKey` |
| `Storage:SecretKey` | `Storage__SecretKey` |
| `Storage:BucketName` | `Storage__BucketName` |
| `Storage:UseHttps` | `Storage__UseHttps` |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins` |

Flat keys (`ADMIN_API_KEY`, `ASPNETCORE_URLS`, `ASPNETCORE_ENVIRONMENT`, and the Postgres/MinIO
keys) have no nesting so no double-underscore is needed.

`ASPNETCORE_URLS=http://+:8080` is set inside the container. Port mapping: 5000 (host) → 8080 (container).

API listens on port 5000 externally (maps to 8080 inside container).

---

## 14. Tools Scripts

### tools/provision-device.sh
Usage: `ADMIN_API_KEY=your-key ./provision-device.sh dev_store1_abc12345 [store_id]`

- Validates DeviceId starts with "dev_"
- Calls `POST /api/v1/admin/devices` with X-Admin-Key
- Parses response with jq, extracts sharedSecret
- Writes `{DEVICE_ID}.env` file with credentials (chmod 600)
- ENV_FILE contents: PRINTNEST_API_URL, PRINTNEST_DEVICE_ID, PRINTNEST_SHARED_SECRET, PRINTNEST_STORE_ID
- Warns operator the secret is shown ONCE

### tools/simulate-device.sh
Usage: `./simulate-device.sh <DEVICE_ID> <SHARED_SECRET_BASE64> <OTP_CODE> [API_URL]`

- Requires: curl, jq, openssl, xxd
- `sign()` function computes HMAC-SHA256: decodes base64 secret → hex via xxd, builds message
  with `printf '%s\n%s\n%s\n%s' ts method path bodyHash`, pipes to
  `openssl dgst -sha256 -mac HMAC -macopt "hexkey:$secret_hex"`
- `device_call()` function wraps curl with X-Device-Id, X-Timestamp, X-Signature headers
- Steps: Heartbeat → Release → file download → printing-started → completed
- Downloads file to `$OUTPUT_DIR/printnest-{jobId}.pdf` (default /tmp)

---

## 15. Bugs Found and Fixed (Do Not Re-Introduce)

1. **`MapWhen` vs `UseWhen`** — MapWhen creates a parallel branch; device/admin requests
   would 404. Fixed to UseWhen. Program.cs now uses UseWhen for both device and admin middleware.

2. **Missing `IsConcurrencyToken()` on Status** — EF Core would not include Status in WHERE
   clause of UPDATE, so two simultaneous releases would both succeed. Fixed in AppDbContext.

3. **`OtpAttempts++` on success path** — was incrementing on match before we removed it.
   Fixed — ApplyEffects on Released already resets OtpAttempts to 0.

4. **Pre-payment jobs never expired** — Draft/Uploaded/Quoted jobs never reached Expired/Deleted,
   leaving files in MinIO forever. Fixed: added three transitions to AllowedTransitions, added
   Case 4 in ExpiryWorker (24h cutoff).

5. **DownloadFile missing status check** — comment said "must be in Released state" but only
   AssignedDeviceId was checked. Fixed: explicit `job.Status != Released → throw 409`.

6. **CleanupWorker DeletePending OR clause** — `|| j.DeletePending` without status guard could
   pick up active jobs if DeletePending were somehow true, deleting their MinIO file. Fixed:
   removed the standalone DeletePending clause; terminal status check is the authoritative guard.

7. **AdminAuthMiddleware misleading comment** — claimed FixedTimeEquals works "even on wrong-length
   keys" which is false. Fixed the comment. The code behavior is acceptable for this use case.

8. **ReleaseJobCommand MetaJson.Contains imprecise** — `dev_a` would match inside `dev_abc123`.
   Fixed: uses anchored JSON fragment `"deviceId":"dev_x"` for the rate limit query.

9. **OtpHash not cleared on Expired transition** — minor privacy gap. Fixed: ApplyEffects
   for Expired now clears OtpHash, OtpExpiryUtc, OtpLockedUntilUtc.

10. **Heartbeat flooding audit_events** — every heartbeat was creating an AuditEvent row.
    Fixed: removed AuditEvent from heartbeat handler. LastHeartbeatUtc on Device is sufficient.

---

## 16. Known Limitations (Accepted for MVP, Not Bugs)

- **Argon2 is synchronous** — blocks thread pool ~200ms per verify. Acceptable for MVP.
  For production: wrap in `Task.Run` in OtpService.Verify.

- **O(N) Argon2 scan in release** — all Paid jobs with active OTPs are loaded and iterated.
  For 50 simultaneous jobs: 50 × 200ms = 10s per release attempt. Acceptable for MVP.
  Future fix: add a fast-path index (e.g., first 3 OTP digits stored separately).

- **AdminKey byte-length leakable via timing** — FixedTimeEquals returns false immediately
  for different-length spans. An attacker can binary-search key length. Acceptable: key has
  ≥32 chars entropy, admin endpoint should not be public.

- **Per-job OTP locking unused** — OtpLockedUntilUtc and OtpAttempts fields exist in schema
  but are never written (because we can't identify which job to lock on failure — device doesn't
  know the jobId, only the OTP). Per-device-per-minute rate limiting is the active protection.

- **Argon2 stuckInStatus audit bug** — in ExpiryWorker, job.Status is read after the transition,
  so MetaJson["stuckInStatus"] always shows the post-transition value ("Expired"/"Failed") not
  the original. Minor audit log inaccuracy only, no functional impact.

- **Mock payment** — payment is a no-op. Real payment integration (Razorpay etc.) is a future phase.

- **Color printing** — disabled in QuoteJobCommand (returns validation error). Color = "Coming Soon".

---

## 17. Git History (Context for Commit Conventions)

```
1e12604 feat: Phase 2 — device provisioning + simulator + complete API test file
0d264af fix: code review — plug lifecycle gaps and tighten security checks
37d2151 Fix 3 bugs found in Phase 1 audit
b8c54c1 Phase 1: full backend foundation
1197fa7 Initial commit: .NET 8 skeleton + project spec
```

Repository: https://github.com/PhaniUmmedisetti/printnest (private)
Branch: main

---

## 18. What Has Been Completed

**Phase 1: Full backend foundation**
- All domain entities, enums, error codes, state machine
- All 8 application commands (CreateJob, FinalizeUpload, QuoteJob, PayJob, GenerateOtp,
  ReleaseJob, MarkDownloading, MarkPrinting, CompleteJob, FailJob)
- All infrastructure implementations (MinIO, Postgres/EF Core, Argon2id, JWT, HMAC)
- Two background workers (ExpiryWorker, CleanupWorker)
- Three middleware (ErrorHandling, DeviceAuth, AdminAuth)
- All API controllers (Public, Device, Admin)
- Docker Compose setup
- EF Core migrations

**Phase 1 Audit (3 bugs fixed)**
- MapWhen → UseWhen
- IsConcurrencyToken added to Status
- OtpAttempts++ removed from success path

**Code Review (7 issues fixed)**
- Pre-payment job lifecycle gap (Draft/Uploaded/Quoted now expire)
- DownloadFile status check added
- CleanupWorker DeletePending clause tightened
- AdminAuthMiddleware comment corrected
- Rate limit MetaJson match anchored
- Heartbeat audit events removed
- OtpHash cleared on Expired transition

**Phase 2: Testing + Tooling**
- `tools/provision-device.sh` — device registration CLI
- `tools/simulate-device.sh` — full device flow simulation (HMAC-signed bash)
- `printnest.http` — complete VS Code REST Client test file
- CLAUDE.md updated with all transitions and testing guide

---

## 19. What Comes Next — Phase 3

**Phase 3: Integration Tests (xUnit, WebApplicationFactory)**

The `public partial class Program { }` in Program.cs exists specifically for this.

Test scenarios to cover:
1. **Happy path**: full job lifecycle Draft→Uploaded→Quoted→Paid→Released→Downloading→Printing→Completed→Deleted
2. **OTP single-use**: after release, same OTP rejected
3. **OTP expiry**: set OtpExpiryUtc in past, verify release fails
4. **Double-release prevention**: two concurrent release requests for same OTP — only one succeeds, other gets LOCK_CONFLICT
5. **File token single-use**: second download with same token rejected (TOKEN_ALREADY_USED)
6. **File token expiry**: use 1-second TTL token, wait, verify rejection (TOKEN_EXPIRED)
7. **HMAC replay attack**: reuse X-Timestamp > 5min in past → 401
8. **HMAC wrong signature**: correct timestamp, wrong signature → 401
9. **Rate limiting**: 6 failed OTP attempts in 60s → 7th returns 429
10. **ExpiryWorker**: set CreatedAtUtc to 25h ago on Quoted job, trigger worker, verify Expired
11. **CleanupWorker**: transition job to Completed manually, trigger worker, verify Deleted + MinIO file gone
12. **State machine rejection**: attempt invalid transition (e.g. Draft→Completed) → JOB_STATE_INVALID
13. **Pre-payment abandonment**: Draft job 25h old → ExpiryWorker → Expired → CleanupWorker → Deleted
14. **Device ownership**: device A cannot mark completed a job owned by device B
15. **Admin key validation**: wrong/missing key → 401; correct key → 200

**Infrastructure for tests:**
- Use `WebApplicationFactory<Program>` with `TestServer`
- Override AppDbContext with a real Postgres test database (or Testcontainers)
- Override MinioStorageService with a real MinIO test instance (or mock)
- Manually trigger workers by resolving from DI scope and calling `RunAsync`

---

## 20. Running the System Locally

```bash
# 1. Set up environment
cp infra/.env.example infra/.env
# Edit infra/.env: set strong ADMIN_API_KEY and JWT_SIGNING_KEY (both ≥32 chars)

# 2. Start everything
docker-compose up

# 3. Verify running
curl http://localhost:5000/health

# 4. Open printnest.http in VS Code and run Steps 1a–8

# 5. Simulate a device
ADMIN_API_KEY=your-key bash tools/provision-device.sh dev_store1_abc12345 store_supermart_01
# Copy sharedSecret from output
bash tools/simulate-device.sh dev_store1_abc12345 "sharedSecret==" 482917
```

Swagger UI: http://localhost:5000/swagger
MinIO Console: http://localhost:9001 (MINIO_ROOT_USER / MINIO_ROOT_PASSWORD)

---

## 21. Adding a New Feature (Checklist)

1. New state? → `Domain/Enums/JobStatus.cs` + `AllowedTransitions` in `JobStateMachine.cs` + `ApplyEffects` if needed
2. New external system? → Interface in `Application/Interfaces/` + implementation in `Infrastructure/`
3. New use case? → Sealed command class in `Application/Commands/`
4. Needs audit? → Call `IAuditService.RecordAsync(...)` BEFORE `SaveChangesAsync()` — it's in the same transaction
5. New HTTP endpoint? → Controller action in appropriate `Api/Controllers/` subfolder
6. Register in `Program.cs` → `builder.Services.AddScoped<YourThing>()`
7. Schema change? → `dotnet ef migrations add YourMigrationName`
8. New error code? → Add constant to `Domain/Errors/ErrorCodes.cs`

---

## 22. Pricing Logic (QuoteJobCommand)

```csharp
const int PricePerCopyPaise = 200;   // ₹2.00
const int MinimumPricePaise  = 500;  // ₹5.00

var priceCents = Math.Max(input.Options.Copies * PricePerCopyPaise, MinimumPricePaise);
```

Currency is always INR. "Cents" in field names means paise (1/100 of a rupee).

---

## 23. Configuration Keys

| Config Key | Env Var | Description |
|-----------|---------|-------------|
| `ConnectionStrings:Postgres` | built from POSTGRES_* | Npgsql connection string |
| `Jwt:SigningKey` | JWT_SIGNING_KEY | HS256 signing key (≥32 chars) |
| `Jwt:FileTokenTtlSeconds` | JWT_FILE_TOKEN_TTL_SECONDS | File token TTL (default 120) |
| `AdminApiKey` | ADMIN_API_KEY | Admin endpoint key (≥32 chars) |
| `Cors:AllowedOrigins` | CORS_ALLOWED_ORIGINS | Comma-separated origins |
| `Storage:Endpoint` | STORAGE_ENDPOINT | MinIO URL |
| `Storage:AccessKey` | MINIO_ROOT_USER | MinIO access key |
| `Storage:SecretKey` | MINIO_ROOT_PASSWORD | MinIO secret key |
| `Storage:BucketName` | MINIO_BUCKET | MinIO bucket (default "printfiles") |
| `Storage:UseHttps` | STORAGE_USE_HTTPS | false for local MinIO |

---

## CURRENT BOOKMARK

**Date:** 2026-02-26

**Completed this session:**
- Created `infra/.env` from template and generated local secrets for Postgres/MinIO/JWT/Admin key
- Fixed Docker build reliability: `Dockerfile` now restores/publishes `printnest.csproj` explicitly
- Added `.dockerignore` to exclude `bin/`, `obj/`, `.git/`, editor folders, and `infra/.env`
- Started local stack with `docker compose --env-file infra/.env up -d --build`
- Verified runtime health: API up on `http://localhost:5000`, Postgres healthy on `5432`, MinIO healthy on `9000/9001`
- Ran smoke test against API (`POST /api/v1/public/printjobs`) and confirmed DB write in `print_jobs`
- Committed Docker setup fixes: `43380f0` (`fix docker setup`)

**Stopped at:** Runtime is up and validated. Project state is clean with no uncommitted changes.

**Next step:** Run `printnest.http` end-to-end (public + device/admin flow) and confirm each transition, then start Phase 3 integration tests (Section 19) beginning with the happy-path lifecycle test.

**Pending decisions:** None blocking. Optional decision: use real MinIO in integration tests via Testcontainers vs mocking `IStorageService`.

**Context notes:**
- Keep `infra/.env` local only; never commit secrets
- Docker command that works in this repo: `docker compose --env-file infra/.env up -d --build`
- If compose warns about obsolete `version` key, it is non-blocking for local run
- `public partial class Program { }` exists for `WebApplicationFactory` tests
- `print_jobs` currently contains at least one smoke-test record from local API validation
