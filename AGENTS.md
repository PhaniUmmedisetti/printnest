# PrintNest AI Operating Guide

This file is the canonical quick-start runbook for any AI coding agent working in this repository.
Use it together with `HANDOFF.md` and `CLAUDE.md`.

## Source Priority
1. `AGENTS.md` (operational workflow)
2. `HANDOFF.md` (latest bookmark + session continuity)
3. `CLAUDE.md` (architecture and domain context)

## Session Start Protocol
1. Read `HANDOFF.md` -> `## CURRENT BOOKMARK`.
2. Report to the user:
   - where work stopped
   - what was completed
   - the immediate next step
3. Confirm environment before code changes:
   - `dotnet --version`
   - `docker info` (if integration tests are needed)

## Core Engineering Rules
1. Never change `PrintJob.Status` directly; always use `JobStateMachine.Transition(...)`.
2. Never log OTP plaintext/hash, shared secrets, or file content.
3. Never invent inline error-code strings; use constants from `Domain/Errors/ErrorCodes.cs`.
4. Device auth failures must remain generic (`DEVICE_UNAUTHORIZED`).
5. OTP release errors must not reveal job existence.

## Bookmark and Commit Policy
If the user asks to bookmark:
1. Update `HANDOFF.md` `## CURRENT BOOKMARK` first.
2. Commit bookmark + session code together in the same commit.
3. Do not split into separate commits unless explicitly requested.

## Phase Status (Current)
- Phase 3: complete and automated via integration tests.
- Phase 4 (backend): printer health telemetry + alerts endpoints implemented.
- Staff UI for printer monitoring is the next major deliverable.

## Current Monitoring Contract (Phase 4 backend)
- Device heartbeat accepts optional `printerHealth`.
- Admin endpoints:
  - `GET /api/v1/admin/devices` (includes `printerHealth` + computed `alerts`)
  - `GET /api/v1/admin/devices/alerts` (flat alert feed for staff dashboard)
- Telemetry is normalized server-side:
  - connection: `ONLINE | OFFLINE | UNKNOWN`
  - operational: `IDLE | PRINTING | ERROR | UNKNOWN`
  - ink: `OK | LOW | VERY_LOW | EMPTY | UNKNOWN`

## Verification Checklist Before Handoff
Run the minimum relevant checks:
1. `dotnet build printnest.sln`
2. `dotnet test tests/PrintNest.IntegrationTests/PrintNest.IntegrationTests.csproj` (for backend behavior changes)

## Definition of Done for Backend Changes
1. Code compiles without warnings.
2. Migration generated when schema changes.
3. Integration tests updated or added for behavior changes.
4. `HANDOFF.md` bookmark reflects exact stop point and next step.

## Commit Convention
Use one focused commit per completed unit of work.
Message format:
- `feat: ...` for new behavior
- `fix: ...` for bug fixes
- `docs: ...` for documentation-only changes
