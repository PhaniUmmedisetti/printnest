# PROJECT_SPEC.md

# Secure Self-Serve Print Network (Privacy-First MVP) — Local/Free Dev Spec

## 0) Goal

Build a privacy-first print workflow:
User uploads PDF → pays (mock) → gets OTP → any kiosk/device releases by OTP → device downloads file (short-lived token) → prints via CUPS → confirms → backend deletes file everywhere.

MVP must be fully runnable for free:

- Local Docker Compose
- Postgres DB
- S3-compatible storage via MinIO
- Backend: .NET API
- Web app: simple frontend
- Pi agent: runnable on any Linux (and Pi later)
- No paid cloud dependencies required for testing

## 1) Non-Goals (MVP)

- Real payments (Razorpay/UPI) — mock only
- QR scan (OTP only)
- Multi-store routing at upload (store assigned only at release)
- Advanced print options (BW + copies only)
- Staff/admin workflows beyond basic monitoring

## 2) Core Principles (Hard Requirements)

- Upload is store-agnostic: no store selection at upload time.
- Print is release-based: nothing prints until OTP is entered at a kiosk.
- Ephemeral file handling:
  - Files encrypted at rest in object storage (MinIO SSE in dev, KMS later).
  - Downloaded to device only at release time.
  - Stored only in `/tmp/printjobs` (prefer tmpfs) and deleted after print attempt.
- OTP:
  - Single use
  - Expires (default 6 hours)
  - Rate-limited attempts
  - Stored hashed in DB (never store plaintext)
- No job browsing on kiosk:
  - device UI shows only minimal summary (copies, BW, price)
  - no preview / thumbnails / filenames in UI logs

## 3) Repository Layout (Monorepo)

/
backend/ # .NET API
web/ # Customer web frontend
pi-agent/ # Device print agent (service + optional local HTTP)
tools/ # test scripts, device simulator
infra/
docker-compose.yml
.env.example
docs/
PROJECT_SPEC.md
AI_BUILD_PROMPT.md

## 4) Local Free Dev Environment (Must Work)

Use docker-compose to run:

- postgres (free)
- minio (free S3-compatible)
- backend API
  Optionally run web and pi-agent locally.

### MinIO

- bucket: printfiles
- access key/secret in .env
- enable server-side encryption in dev if supported; otherwise treat as dev-only
- Use presigned PUT for upload, token-gated GET (via backend streaming endpoint) for device fetch.

## 5) MVP State Machine (Backend)

Statuses:

- Draft → Uploaded → Quoted → Paid → Released → Downloading → Printing → Completed → Deleted
  Terminal:
- Expired
- Failed
- Refunded (manual trigger only in MVP)

Rules:

- AssignedDeviceId is NULL until Released.
- Released binds job to a device (and storeId if present) and consumes OTP.
- File download allowed only after Released and only with short-lived device-bound file token.

Timeout defaults:

- OTP expiry: 6 hours
- file token TTL: 120 seconds
- release lock TTL: 90 seconds
- printing watchdog: 10 minutes

## 6) Data Model (Postgres)

### PrintJobs

- JobId (uuid, pk)
- Status (text / enum)
- ObjectKey (text) # minio object path
- Sha256 (text, nullable)
- OptionsJson (jsonb) # { copies, color: "BW" }
- PriceCents (int)
- Currency (text default "INR")
- OtpHash (text, nullable)
- OtpExpiryUtc (timestamptz, nullable)
- AssignedDeviceId (text, nullable)
- AssignedStoreId (text, nullable)
- ReleaseAttemptCount (int default 0)
- CreatedAtUtc, UpdatedAtUtc
- PrintedAtUtc (nullable)
- DeletedAtUtc (nullable)
  Indexes:
- status
- otp expiry
- assignedDeviceId

### Devices (MVP)

- DeviceId (text pk)
- StoreId (text nullable)
- PublicKey (text) OR SharedSecret (text) # choose one; prefer public key signing
- LastHeartbeatUtc (timestamptz)
- CapabilitiesJson (jsonb)

### AuditEvents

- Id (bigserial pk)
- JobId (uuid)
- Type (text)
- AtUtc (timestamptz)
- MetaJson (jsonb)

## 7) Backend API Contract (MVP)

Base: /api/v1

### Public endpoints

1. Create job
   POST /public/printjobs
   Request: { fileName, fileSizeBytes, contentType }
   Response: { jobId, upload: { method, url, headers, expiresInSeconds } }

2. Finalize upload
   POST /public/printjobs/{jobId}/finalize
   Request: { sha256 }
   Response: { status }

3. Quote
   POST /public/printjobs/{jobId}/quote
   Request: { options: { copies: int, color: "BW" } }
   Response: { status, pricing: { currency, totalAmountCents } }

4. Mock payment confirm (MVP)
   POST /public/printjobs/{jobId}/pay-mock
   Response: { status: "Paid", otp, otpExpiresAtUtc }

5. Get job status
   GET /public/printjobs/{jobId}
   Response: { jobId, status, otpExpiresAtUtc?, assignedDeviceId?, pricing? }

### Device endpoints

Auth: device-signed request (recommended) OR shared secret header.
All device endpoints must enforce:

- known device
- request signature valid
- rate limits

1. Heartbeat
   POST /device/heartbeat
   Request: { deviceId, storeId?, capabilities?, cupsPrinters? }
   Response: { serverTimeUtc }

2. Release by OTP
   POST /device/release
   Request: { deviceId, storeId?, otp }
   Response: {
   jobId,
   status: "Released",
   jobSummary: { copies, color, priceCents, currency },
   fileToken: { token, expiresInSeconds }
   }

3. Download file (token-gated)
   GET /device/printjobs/{jobId}/file
   Header: Authorization: Bearer <fileToken>
   Response: application/pdf stream

4. Printing started
   POST /device/printjobs/{jobId}/printing-started
   Request: { deviceId, cupsJobId, printerName }

5. Completed
   POST /device/printjobs/{jobId}/completed
   Request: { deviceId, cupsJobId, result: "SUCCESS", metrics? }

6. Failed
   POST /device/printjobs/{jobId}/failed
   Request: { deviceId, cupsJobId?, failure: { code, message, isRetryable } }

## 8) Pi Agent (Headless) Requirements

- Runs as a service (systemd unit file included).
- Provides local HTTP endpoint on localhost only:
  POST http://127.0.0.1:7070/local/release { otp }
- On release:
  - calls backend /device/release
  - downloads file via /device/printjobs/{jobId}/file using token
  - saves to /tmp/printjobs/{jobId}.pdf (0600)
  - prints via CUPS (lp command is acceptable)
  - monitors job status (lpstat) until success/fail or timeout
  - calls backend completed/failed
  - deletes local file always
- Must be robust:
  - retries transient download errors
  - never leaves files behind
  - safe on restart (cleans stale /tmp/printjobs)

## 9) Web Frontend (MVP)

Pages:

- Upload page:
  - select PDF
  - upload via presigned URL
  - choose copies (BW only)
  - quote + pay mock
  - show OTP + expiry + link to status
- Status page:
  - shows current status: Paid/Released/Printing/Deleted/etc
  - no document preview

## 10) Testing Without Pi Display (Required)

Provide a "device simulator" script in tools/:

- reads otp + deviceId from args
- calls /device/release
- downloads file
- optionally "fake print" (sleep) then call completed
  This allows full end-to-end validation on a laptop.

Also provide integration tests:

- OTP wrong attempts rate limited
- OTP single-use
- Cannot download file before release
- After completion, object removed from MinIO and job status becomes Deleted

## 11) Security Rules (MVP must follow)

- Never log OTP or PDF bytes.
- OTP stored hashed (Argon2id preferred; bcrypt acceptable in MVP).
- File token short-lived and device-bound; one-time use is ideal.
- CORS locked to web origin in dev; production configurable.
- Rate limiting for release attempts.
- Validate PDF contentType and enforce max size (e.g., 20MB MVP).
- Use HTTPS in production later; local HTTP acceptable.

## 12) Acceptance Criteria (Definition of Done)

- docker-compose up → system runs locally
- User can upload PDF, pay mock, get OTP
- Device simulator can release OTP, download file, complete job
- Backend deletes file from MinIO after completion
- Job ends in Deleted
- No sensitive logs contain OTP or file content
- Failure paths do not leave job stuck forever (timeouts move to Failed/Expired)

## 13) Configuration (env)

Provide infra/.env.example with:

- POSTGRES connection
- MINIO endpoint, bucket, access key, secret
- JWT signing key for backend
- device auth mode (public key / shared secret)
- OTP expiry hours, token TTL seconds, rate limit params

## 14) Notes for Later (Not MVP)

- Real payments (Razorpay)
- QR support
- Multi-store scale + monitoring
- Refund automation
- Kiosk touchscreen UI (Pi full-screen) — can be built after agent works