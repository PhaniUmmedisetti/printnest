# PrintNest Testing Guide (for new developers)

This file explains how to run the app locally, what each API test step does, what inputs are needed, and what output to expect.

## 1) What you need

- Docker Desktop running
- VS Code with REST Client extension (`ms-vscode.vscode-restclient`)
- `printnest.http` opened in VS Code
- A local PDF file path for upload tests
- Bash for device simulation (WSL or Git Bash)
- In Bash: `curl`, `jq`, `openssl`, `xxd`

## 2) Start the app

From repo root:

```powershell
docker compose --env-file infra/.env up -d --build
```

Health check:

```powershell
curl.exe -sS http://localhost:5000/health
```

Expected:

```json
{"status":"ok","timestamp":"..."}
```

## 3) Inputs you must fill in `printnest.http`

At the top of `printnest.http`, set:

- `@staffUsername`
  - Source: `infra/.env` -> `STAFF_AUTH_BOOTSTRAP_USERNAME`
- `@staffPassword`
  - Source: `infra/.env` -> `STAFF_AUTH_BOOTSTRAP_PASSWORD`
- `@staffAccessToken`
  - Copy from Step 1 response (`accessToken`)
- `@samplePdfPath`
  - Example: `C:/Users/<you>/Downloads/file.pdf`
- `@sharedSecret`
  - Copy from Step 4 response (`register device`)
- `@jobId`
  - Copy from Step 6 response (`create job`)
- `@uploadUrl`
  - Copy from Step 6 response (`upload.url`)
  - Replace `https://minio:9000` with `http://localhost:9000` for local host upload
- `@otp`
  - Copy from Step 11 response (`generate otp`)

## 4) Run order and expected output

Use `printnest.http` top to bottom.

### Step 0 - Health
- Request: `GET /health`
- Expect: `200`, body has `status: "ok"`

### Step 1 - Staff login
- Request: `POST /api/v1/staff/auth/login` with username/password
- Expect: `200` with `accessToken`

### Step 2 - Create store
- Request: `POST /api/v1/admin/stores`
- Expect: `200` and store object
- Note: if already created, you may get `409`

### Step 3 - List stores
- Request: `GET /api/v1/admin/stores`
- Expect: `200` and your store in list

### Step 4 - Register device
- Request: `POST /api/v1/admin/devices`
- Expect: `200` with `sharedSecret` (shown once)
- Action: copy `sharedSecret` into `@sharedSecret`

### Step 5 - List devices
- Request: `GET /api/v1/admin/devices`
- Expect: `200` and your device in list

### Step 6 - Create print job
- Request: `POST /api/v1/public/printjobs`
- Expect: `200` with:
  - `jobId`
  - `upload.url` (presigned PUT URL)
- Action:
  - set `@jobId`
  - set `@uploadUrl` from `upload.url`, replacing host/protocol as noted above

### Step 7 - Upload PDF to MinIO
- Request: `PUT {{uploadUrl}}` with `< {{samplePdfPath}}`
- Expect: `200`, usually empty body

### Step 8 - Finalize upload
- Request: `POST /api/v1/public/printjobs/{{jobId}}/finalize`
- Expect: `200`, `{ "status": "Uploaded" }`

### Step 9 - Quote
- Request: `POST /api/v1/public/printjobs/{{jobId}}/quote`
- Expect: `200`, status `Quoted`, pricing in INR

### Step 10 - Mock pay
- Request: `POST /api/v1/public/printjobs/{{jobId}}/pay-mock`
- Expect: `200`, status `Paid`

### Step 11 - Generate OTP
- Request: `POST /api/v1/public/printjobs/{{jobId}}/otp/generate`
- Expect: `200` with 6-digit `otp`
- Action: copy to `@otp`

### Step 12 - Job status
- Request: `GET /api/v1/public/printjobs/{{jobId}}`
- Typical expected before device flow: `Paid`

### Step 13 - Public stores list
- Request: `GET /api/v1/public/stores`
- Expect: `200` and active stores only

### Step 14 - Device simulation
Run from repo root:

```powershell
bash tools/simulate-device.sh dev_supermart_abc12345 "<sharedSecret>" "<otp>" http://localhost:5000
```

Expected sequence:
- Heartbeat success
- OTP release success
- File download success
- Printing started success
- Completed success
- Final line: `Full device flow complete`

After this, cleanup worker should move job from `Completed` to `Deleted` within about 60 seconds.

## 5) Common issues and fixes

- `getaddrinfo ENOTFOUND minio`
  - Cause: host machine cannot resolve Docker service name
  - Fix: use `http://localhost:9000` in `@uploadUrl`

- `STORAGE_ERROR File not found in storage` on finalize
  - Cause: uploaded one job's file, finalized another job ID
  - Fix: ensure `@jobId` and `@uploadUrl` come from the same Step 6 response

- Admin endpoints return `401`
  - Cause: missing/expired/invalid bearer token
  - Fix: re-run Step 1 and update `@staffAccessToken`

- Device simulator says `jq` missing
  - Fix (Ubuntu WSL): `sudo apt-get update && sudo apt-get install -y jq`

## 6) Printer monitoring expectations (HP DeskJet 2338)

Current MVP hardware is HP DeskJet 2338 over USB via Raspberry Pi + CUPS.

- Reliable to monitor: online/offline, idle/printing, paper-out, door-open, cartridge-missing, general error
- Status accuracy is typically ~98-100%
- Ink monitoring is only state-based (`OK`, `Low`, `Very Low`, `Empty`), not percentage-based
- Ink warning quality: `Low` is approximate (~70-85%), `Empty` is strong (~95-100%)
- Do not build app logic around exact ink percentages for this printer class
- Refilled cartridges and host-based counter behavior can make ink status inaccurate

## 7) Safe ways to play around

- Change `copies` in Step 9 and check pricing
- Call Step 11 multiple times and verify OTP changes
- Try status checks at each stage (Step 12)
- Register another device and test ownership/security behavior

## 8) Phase 3 integration tests (xUnit + Testcontainers)

These tests run the full 15-scenario Phase 3 matrix using:
- `WebApplicationFactory<Program>`
- Postgres Testcontainer
- MinIO Testcontainer

Prerequisite:
- Docker Desktop running before test execution

Run only integration tests:

```powershell
dotnet test tests/PrintNest.IntegrationTests/PrintNest.IntegrationTests.csproj
```

Notes:
- Containers are shared for the test run and DB/bucket are reset before each test.
- Background workers are not run as timed hosted services in tests; tests invoke one-shot worker methods for deterministic assertions.
- If Docker is unavailable, the integration suite will fail to start containers.
