# PrintNest - AI Context

This file gives AI coding agents enough context to continue work safely.
Read this with `AGENTS.md` and `HANDOFF.md`.

## Source Priority
1. `AGENTS.md` (operating rules and workflow)
2. `HANDOFF.md` (current bookmark and session continuity)
3. `CLAUDE.md` (architecture and domain reference)

## Product in Plain Terms
- Customer uploads a PDF from mobile.
- Customer pays and gets a 6-digit OTP.
- At store kiosk, customer enters OTP.
- Kiosk downloads file, prints it, then cleanup deletes file from storage.

Privacy goal: files are temporary and removed after completion/cleanup.

## Tech Stack
- .NET 8 Web API
- PostgreSQL (EF Core + Npgsql)
- MinIO (S3-compatible object storage)
- JWT HS256 (file download token)
- HMAC-SHA256 (device authentication)
- Argon2id (OTP hashing)
- Docker Compose for local services

## Architecture
Single project (`printnest.csproj`) with folder-layer boundaries.

- `Domain/`
  - Entities (`PrintJob`, `Device`, `Store`, `AuditEvent`, `UsedFileToken`)
  - Enums (`JobStatus`, `AuditEventType`)
  - State machine (`JobStateMachine.cs`)
  - Errors (`DomainException`, `ErrorCodes`)
- `Application/`
  - Commands (use-case orchestration)
  - Interfaces for infrastructure dependencies
- `Infrastructure/`
  - EF Core persistence (`AppDbContext`)
  - Auth services (OTP, JWT, HMAC)
  - Storage service (MinIO)
  - Workers (`ExpiryWorker`, `CleanupWorker`)
- `Api/`
  - Controllers (Public, Device, Admin)
  - Middleware (error handling + auth)

## Critical Rule
Never set `PrintJob.Status` directly.
Always use:

```csharp
JobStateMachine.Transition(job, JobStatus.TargetState, actor: "user|device|worker");
```

## Core Job States
`Draft -> Uploaded -> Quoted -> Paid -> Released -> Downloading -> Printing -> Completed -> Deleted`

Other guarded paths exist for `Failed` and `Expired` and are enforced in `JobStateMachine`.

## API Surface (Current)
### Public (`/api/v1/public`)
- `POST /printjobs`
- `POST /printjobs/{id}/finalize`
- `POST /printjobs/{id}/quote`
- `POST /printjobs/{id}/pay-mock`
- `POST /printjobs/{id}/otp/generate`
- `GET /printjobs/{id}`
- `GET /stores`

### Device (`/api/v1/device`)
- `POST /heartbeat` (includes optional `printerHealth` payload)
- `POST /release`
- `GET /printjobs/{id}/file`
- `POST /printjobs/{id}/printing-started`
- `POST /printjobs/{id}/completed`
- `POST /printjobs/{id}/failed`

### Admin (`/api/v1/admin`)
- `POST /devices`
- `PATCH /devices/{id}/deactivate`
- `GET /devices` (includes computed printer alerts)
- `GET /devices/alerts` (flat alert feed)
- `POST /stores`
- `GET /stores`

## Security and Behavior Rules
- OTP: 6-digit numeric, Argon2id-hashed, expires in 6 hours, single-use.
- OTP release is rate limited (per device) and should not leak job existence.
- File token: short-lived JWT, single-use enforced by `UsedFileTokens`.
- Device auth must stay generic on failure (`DEVICE_UNAUTHORIZED`).
- Never log OTP plaintext/hash, shared secrets, or file contents.
- Use constants from `Domain/Errors/ErrorCodes.cs` for error codes.

## Phase 3 Testing Baseline
Integration project: `tests/PrintNest.IntegrationTests`

- Uses `WebApplicationFactory<Program>`.
- Uses Testcontainers Postgres + MinIO.
- Uses deterministic worker hooks (`RunOnceAsync`) and release concurrency hook.
- Covers 15 handoff scenarios (happy path, OTP/token semantics, replay/signature checks, workers, invalid transitions, ownership, admin auth).

Run:
```powershell
dotnet test tests\PrintNest.IntegrationTests\PrintNest.IntegrationTests.csproj
```

## Phase 4 Backend Baseline
Implemented backend support for printer health telemetry and staff alert feeds.

- Device heartbeat accepts normalized health input.
- Device list endpoint returns health summary and computed alerts.
- Alerts endpoint returns active alert feed for staff UI.

Normalized values:
- Connection: `ONLINE | OFFLINE | UNKNOWN`
- Operational: `IDLE | PRINTING | ERROR | UNKNOWN`
- Ink: `OK | LOW | VERY_LOW | EMPTY | UNKNOWN`

## Local Commands
```powershell
dotnet build printnest.sln
dotnet test tests\PrintNest.IntegrationTests\PrintNest.IntegrationTests.csproj
```

## AI Agent Checklist Before Handoff
1. Build succeeds.
2. Relevant tests pass.
3. Migrations added for schema changes.
4. Bookmark updated in `HANDOFF.md` if user asked to bookmark.
5. If user asked bookmark + commit, include bookmark and code in same commit.
