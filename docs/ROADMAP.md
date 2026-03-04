# PrintNest Roadmap

## Purpose

This document freezes the post-Phase-4 plan for the product using the current repo split:

- `printnest` - backend only, source of truth
- `printnest-kiosk` - kiosk frontend + Pi local agent
- `printnest-staff-pwa` - staff dashboard UI
- `printnest-customer-pwa` - future customer app

## Current State

Completed:

- Phase 1 - backend foundation
- Phase 2 - tooling and simulator
- Phase 3 - integration tests
- Phase 4 - staff auth, printer telemetry, monitoring alerts, staff dashboard baseline

Current backend truth:

- jobs, OTP, auth, device APIs, alert logic, and lifecycle rules stay in `printnest`
- kiosk and staff are clients of this backend
- kiosk must not become a second source of truth

## Repo Ownership

### `printnest`

Owns:

- business rules
- database schema and migrations
- job lifecycle and cleanup
- OTP generation and release
- device authentication
- file token issuance and validation
- staff authentication and monitoring APIs
- public/customer-facing APIs

Does not own:

- kiosk UI
- Pi-local printer orchestration
- staff UI frontend
- customer UI frontend

### `printnest-kiosk`

Owns:

- kiosk touch UI
- Pi-local agent/service
- CUPS integration
- local temp-file handling
- Pi deployment and service scripts

Does not own:

- business job state
- OTP truth
- payment truth
- monitoring truth

### `printnest-staff-pwa`

Owns:

- staff login UX
- alert feed UX
- device/store monitoring UX

### `printnest-customer-pwa`

Owns:

- upload flow
- payment flow
- OTP display
- job status tracking UX

## Phase 5 - Kiosk Integration And Deployment Baseline

### Goal

Get the real store-side print flow running end to end against the existing backend:

`customer upload -> payment/mock payment -> OTP -> kiosk OTP entry -> Pi print -> backend completion`

### Primary Outcome

The kiosk repo works against the backend repo using the backend's existing device contract, and the Pi can print a released job reliably.

### Scope

- keep `printnest` as the only source of truth
- adapt `printnest-kiosk` to backend device APIs
- keep OTP as 6-digit numeric
- keep current privacy rule: no filename or preview on kiosk
- keep PDF-only backend contract for the first stable end-to-end flow
- use mock payment until customer app + real payment work is started

### Backend Requirements

- keep existing device endpoints stable:
  - `POST /api/v1/device/heartbeat`
  - `POST /api/v1/device/release`
  - `GET /api/v1/device/printjobs/{jobId}/file`
  - `POST /api/v1/device/printjobs/{jobId}/printing-started`
  - `POST /api/v1/device/printjobs/{jobId}/completed`
  - `POST /api/v1/device/printjobs/{jobId}/failed`
- document the expected payloads clearly for kiosk integration
- only add backend changes if the kiosk integration exposes a real contract gap
- do not add kiosk-local logic to this repo

### Kiosk Requirements

- kiosk UI accepts 6-digit OTP
- Pi agent signs backend requests with HMAC device auth
- Pi agent downloads the PDF through the backend file endpoint
- Pi agent reports printing started, completed, and failed states back to backend
- Pi agent sends heartbeat telemetry in the backend's normalized health format
- Pi always deletes local temp files after print success or failure

### Operational Requirements

- one repeatable local setup guide for Pi
- one provisioning flow for device registration and secret handoff
- one smoke test for real kiosk flow against the backend

### Definition Of Done

- a job can be created and paid via backend/public tooling
- a valid OTP can be entered on the kiosk
- Pi prints successfully through CUPS
- backend job reaches `Completed`, then cleanup reaches `Deleted`
- printer telemetry appears in staff monitoring
- kiosk repo has a working deployment/runbook for Raspberry Pi

## Phase 6 - Customer App And Release Readiness

### Goal

Add the customer-facing app and close the remaining release gaps so the whole user journey works without operator/test tooling.

### Primary Outcome

The full production-shaped flow exists:

`customer app upload -> payment -> OTP -> kiosk print -> staff monitoring`

### Scope

- build `printnest-customer-pwa`
- support customer upload, quote, payment, OTP display, and status polling
- decide whether payment remains mock for pilot or moves to real gateway integration
- harden cross-repo deployment and environment handling
- freeze first-release operating model for staff and kiosk support

### Customer App Requirements

- upload PDF
- finalize upload
- quote copies/color
- payment step
- OTP generation and expiry display
- job status page

### Backend Requirements

- keep public API stable and well documented for customer app consumption
- add payment integration only when the payment decision is finalized
- add only the minimum extra endpoints/fields needed for customer UX

### Operational Requirements

- backend, kiosk, staff, and customer repos each have clear deploy steps
- environment variables and base URLs are documented per repo
- first-release incident/support workflow is written down
- go-live checklist exists across repos

### Definition Of Done

- customer can complete the full flow without backend test tooling
- kiosk print path remains stable against the production customer flow
- staff can monitor live device health and active alerts
- repo ownership and deployment boundaries are documented and repeatable

## Open Decisions

- whether `DOOR_OPEN` remains blocking or is downgraded to critical
- whether Phase 4 staff monitoring stays polling-only first or moves to SignalR/WebSocket
- whether first customer release uses mock payment or real payment integration
- exact external repo/path to standardize for `printnest-kiosk`

## Immediate Next Actions

1. Freeze this roadmap in the backend repo.
2. Standardize the kiosk repo location and ownership handoff.
3. Integrate the kiosk repo against the backend device APIs.
4. Prove one full print flow before starting customer app work.
