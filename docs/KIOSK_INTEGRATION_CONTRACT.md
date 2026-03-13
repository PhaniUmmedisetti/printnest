# PrintNest — Kiosk Integration Contract

This document is the single source of truth for how `printnest-kiosk` must talk
to the `printnest` backend. Read this before writing any device-side or UI code.

---

## What Changed (and Why)

The backend now stores the failure reason on the job itself and returns it through
the public status API. Before this change, the device sent failure details to
`POST /failed` but they were only written to internal audit logs — the kiosk UI
had no way to know *why* a job failed. Every poll just returned `status: "Failed"`
with no context.

**New field on `GET /api/v1/public/printjobs/{jobId}`:**

```json
{
  "jobId": "...",
  "status": "Failed",
  "canReuseOtp": true,
  "failure": {
    "code": "PAPER_OUT",
    "message": "Printer out of paper"
  }
}
```

When the job is not in `Failed` status, `failure` is `null`. When it is `Failed`,
`failure.code` and `failure.message` describe why.

---

## The Complete Print Flow

This is the full sequence. The Pi agent owns every step after the customer
enters the OTP.

```
Customer enters OTP on kiosk UI
        ↓
[1] Pi agent → POST /api/v1/device/release        { otp, storeId }
        ↓  receives fileToken (valid 120s)
[2] Pi agent → GET  /api/v1/device/printjobs/{id}/file   (Bearer: fileToken)
        ↓  backend auto-transitions job to Downloading
[3] Pi saves PDF to local temp file
        ↓
[4] Pi submits CUPS print job
        ↓
[5] Pi agent → POST /api/v1/device/printjobs/{id}/printing-started
        ↓
[6] Pi watches CUPS job status until terminal (completed / failed / on-hold)
        ↓
    CUPS success?
      YES → [7a] Pi deletes local temp file
            Pi agent → POST /api/v1/device/printjobs/{id}/completed
      NO  → [7b] Pi deletes local temp file
            Pi agent → POST /api/v1/device/printjobs/{id}/failed   ← see below
```

---

## Pi Agent — What It Must Do

### Step 6: Watch CUPS Until Terminal

Do NOT call `/completed` immediately after submitting the CUPS job. You must poll
or listen for the CUPS job to reach a terminal state:

| CUPS state | Meaning | Action |
|---|---|---|
| `completed` | Paper came out | Call `/completed` |
| `aborted` | CUPS aborted the job | Call `/failed` with appropriate code |
| `canceled` | Job was canceled | Call `/failed` |
| `stopped` / `on-hold` with no recovery | Printer cannot continue | Call `/failed` |

Use `lpstat -l -j <cupsJobId>` or the CUPS API to poll job state. A reasonable
poll interval is 2–3 seconds with a 5-minute timeout. If the timeout fires,
still call `/failed` — the backend watchdog will also eventually catch it, but
it's better to report proactively.

### Step 7b: Calling `/failed` Correctly

```
POST /api/v1/device/printjobs/{jobId}/failed
Authorization: HMAC (device auth header, same as all device endpoints)

{
  "cupsJobId":      "123",
  "failureCode":    "PAPER_OUT",       ← required, see codes below
  "failureMessage": "Printer out of paper. Please refill and retry.",
  "isRetryable":    true               ← see retry rules below
}
```

**`failureCode` values the kiosk should send:**

| Code | When to use |
|---|---|
| `PAPER_OUT` | CUPS job stopped because printer reports paper out |
| `PAPER_JAM` | Printer reports a jam |
| `DOOR_OPEN` | Printer cover/door opened during print |
| `CARTRIDGE_MISSING` | Cartridge was removed mid-print |
| `INK_EMPTY` | Printer ink ran out during print |
| `CUPS_ERROR` | CUPS aborted or reported an error not covered above |
| `DOWNLOAD_FAILED` | Pi could not download the file from the backend |
| `TEMP_FILE_ERROR` | Pi could not write/read the local temp file |

**`isRetryable` rules:**

| Scenario | `isRetryable` |
|---|---|
| Paper out, door open, cartridge missing — physical issue the staff can fix, file is fine | `true` |
| Ink empty | `true` |
| Paper jam — printer may need service, but job file is fine | `true` |
| CUPS repeatedly aborted (not a physical issue) | `false` |
| Download failed or temp file error | `true` (different kiosk can try) |

When `isRetryable: true`, the backend keeps the same OTP alive so the customer
can re-enter it at a working kiosk. When `isRetryable: false`, the OTP is voided.

### Heartbeat — Printer Health Payload

The heartbeat `printerHealth` block must accurately reflect the printer state
at all times. The backend uses these values to block release if the printer
cannot physically accept a job.

```
POST /api/v1/device/heartbeat
{
  "storeId": "store_123",
  "printerHealth": {
    "connectionState":  "ONLINE",    // "ONLINE" | "OFFLINE" | "UNKNOWN"
    "operationalState": "IDLE",      // "IDLE" | "PRINTING" | "ERROR" | "UNKNOWN"
    "paperOut":         false,
    "doorOpen":         false,
    "cartridgeMissing": false,
    "inkState":         "OK",        // "OK" | "LOW" | "VERY_LOW" | "EMPTY" | "UNKNOWN"
    "printerModel":     "HP LaserJet",
    "rawStatusJson":    null         // optional, raw CUPS/IPP status for debugging
  }
}
```

Send heartbeat every 30 seconds while the Pi is running, including during a print
job. The backend watches heartbeat freshness for the staff monitoring alerts.

---

## Kiosk UI — What It Must Display

The UI polls `GET /api/v1/public/printjobs/{jobId}` while the job is in progress.
Here is exactly what to show for each status:

| `status` | `canReuseOtp` | `failure.code` | What to show the customer |
|---|---|---|---|
| `Released` | — | — | "Connecting to printer…" |
| `Downloading` | — | — | "Preparing your document…" |
| `Printing` | — | — | "Printing in progress…" |
| `Completed` | — | — | ✅ **"Your document is ready! Please collect it from the printer."** |
| `Failed` | `true` | `PAPER_OUT` | ❌ "Printer ran out of paper. Ask staff to refill and try again with the same code." |
| `Failed` | `true` | `PAPER_JAM` | ❌ "Printer paper jam. Ask staff for help. You can retry with the same code." |
| `Failed` | `true` | `DOOR_OPEN` | ❌ "Printer cover is open. Close it and try again with the same code." |
| `Failed` | `true` | `INK_EMPTY` | ❌ "Printer ink is empty. Ask staff for help. You can retry with the same code." |
| `Failed` | `true` | `WATCHDOG_TIMEOUT` | ❌ "Print was interrupted. You can retry with the same code at another kiosk." |
| `Failed` | `true` | anything else | ❌ `failure.message` — show the message directly, offer retry |
| `Failed` | `false` | anything | ❌ "Print failed. Please contact staff to restart the process." |
| `Expired` | — | — | "Your session expired. Please start a new print job." |

**Never show "print is ready" unless `status === "Completed"`.**

If `failure` is `null` but `status` is `Failed` (legacy data or watchdog expiry
before this update), fall back to a generic message: "Print failed. Please contact staff."

### Polling Behaviour

- Poll every 3 seconds while status is `Released`, `Downloading`, or `Printing`
- Stop polling once status is `Completed`, `Failed`, `Expired`, or `Deleted`
- Show a spinner / progress indicator while polling
- After 10 minutes with no terminal state, show: "Something is taking longer than
  expected. Please ask staff for help." — but keep polling

---

## What the Backend Blocks at Release Time

If any of the following are true at the moment the customer enters the OTP, the
backend will reject the release with `HTTP 409 PRINTER_NOT_READY`:

- Printer paper out
- Printer cartridge missing
- Printer door open
- Printer ink state is EMPTY

The kiosk should surface this as: **"This printer cannot accept jobs right now.
Please ask staff for help or try another kiosk."**

This check uses the last heartbeat state. If the printer health has not been
reported recently, the backend cannot block on it — so the Pi must keep heartbeats
going at all times.

---

## Device Auth Quick Reference

All `POST /api/v1/device/*` and `GET /api/v1/device/*` requests require HMAC auth.

```
X-Device-Id:        dev_<your-device-id>
X-Timestamp:        <unix-seconds-utc>
X-Signature:        HMAC-SHA256(sharedSecret, "POST\n/api/v1/device/release\n<timestamp>\n<body-sha256>")
```

Timestamp must be within 30 seconds of server time. The backend returns
`DEVICE_UNAUTHORIZED` on failure — never a specific reason.

---

## New API Fields Summary

`GET /api/v1/public/printjobs/{jobId}` response (updated):

```json
{
  "jobId":           "uuid",
  "status":          "Completed | Failed | Printing | ...",
  "priceCents":      500,
  "currency":        "INR",
  "otpExpiresAtUtc": "2026-03-14T10:00:00Z",   // only when status=Paid
  "canReuseOtp":     true,                       // only meaningful when status=Failed
  "failure": {                                   // null unless status=Failed
    "code":    "PAPER_OUT",
    "message": "Printer out of paper"
  },
  "assignedStoreId": "store_123",
  "createdAtUtc":    "2026-03-14T09:00:00Z",
  "updatedAtUtc":    "2026-03-14T09:05:00Z"
}
```

No other endpoints changed. All existing device endpoints (`release`, `file`,
`printing-started`, `completed`, `failed`, `heartbeat`) have the same request
shape as before.
