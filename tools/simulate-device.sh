#!/usr/bin/env bash
# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
# PrintNest â€” Device Simulator
#
# Simulates the complete Raspberry Pi device workflow without hardware:
#   1. Heartbeat        â€” verifies HMAC auth is working
#   2. OTP Release      â€” enters OTP, gets job details + file token
#   3. File Download    â€” downloads PDF from MinIO via the file token
#   4. Printing Started â€” reports CUPS job submitted
#   5. Completed        â€” reports print success
#
# Usage:
#   ./simulate-device.sh <DEVICE_ID> <SHARED_SECRET_BASE64> <OTP_CODE> [API_URL]
#
# Example (after running Steps 1â€“8 in printnest.http):
#   ./simulate-device.sh dev_supermart_01 "abc123base64==" 482917
#
# Environment variables:
#   STORE_ID    (optional) â€” sent with release and heartbeat
#   OUTPUT_DIR  (optional) â€” where to save the downloaded PDF, default /tmp
#
# Requirements: curl, jq, openssl, xxd
# â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

set -euo pipefail

# â”€â”€ Args â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
DEVICE_ID="${1:?Usage: $0 <DEVICE_ID> <SHARED_SECRET_BASE64> <OTP_CODE> [API_URL]}"
DEVICE_SECRET="${2:?Usage: $0 <DEVICE_ID> <SHARED_SECRET_BASE64> <OTP_CODE> [API_URL]}"
OTP="${3:?Usage: $0 <DEVICE_ID> <SHARED_SECRET_BASE64> <OTP_CODE> [API_URL]}"
API_URL="${4:-http://localhost:5000}"

STORE_ID="${STORE_ID:-}"
OUTPUT_DIR="${OUTPUT_DIR:-/tmp}"

# â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
step() { echo ""; echo "â”€â”€ $* â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€"; }
ok()   { echo "  âœ“ $*"; }
fail() { echo "  âœ— $*" >&2; exit 1; }

# Compute HMAC-SHA256 device auth headers.
# Args:   METHOD PATH [BODY]
# Prints: "TIMESTAMP:SIGNATURE" (both lowercase hex)
sign() {
    local method="$1"
    local path="$2"
    local body="${3:-}"
    local ts
    ts=$(date +%s)

    # SHA256 of request body (empty body has a well-known hash)
    local body_hash
    if [ -z "$body" ]; then
        body_hash="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    else
        body_hash=$(printf '%s' "$body" | openssl dgst -sha256 | awk '{print $NF}')
    fi

    # Decode the base64 shared secret to raw hex for openssl HMAC
    local secret_hex
    secret_hex=$(printf '%s' "$DEVICE_SECRET" | base64 -d | xxd -p | tr -d ' \n')

    # Message = timestamp\nMETHOD\npath\nbodyHash  (real newlines, not literal \n)
    local msg
    msg=$(printf '%s\n%s\n%s\n%s' "$ts" "$method" "$path" "$body_hash")

    # HMAC-SHA256 with raw binary key
    local sig
    sig=$(printf '%s' "$msg" | openssl dgst -sha256 -mac HMAC -macopt "hexkey:$secret_hex" | awk '{print $NF}')

    echo "${ts}:${sig}"
}

# Make a signed API call to a device endpoint.
# Args:   METHOD PATH [BODY]
# Prints: response JSON
device_call() {
    local method="$1"
    local path="$2"
    local body="${3:-}"

    local auth
    auth=$(sign "$method" "$path" "$body")
    local ts="${auth%%:*}"
    local sig="${auth##*:}"

    local curl_args=(
        -s
        -X "$method"
        -H "X-Device-Id: $DEVICE_ID"
        -H "X-Timestamp: $ts"
        -H "X-Signature: $sig"
    )

    if [ -n "$body" ]; then
        curl_args+=(-H "Content-Type: application/json" -d "$body")
    fi

    curl "${curl_args[@]}" "$API_URL$path"
}

# â”€â”€ Preflight checks â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
for cmd in curl jq openssl xxd; do
    command -v "$cmd" &>/dev/null || fail "Required tool not found: $cmd. Install with: apt install $cmd"
done

echo ""
echo "PrintNest Device Simulator"
echo "  API URL:  $API_URL"
echo "  Device:   $DEVICE_ID"
echo "  Store:    ${STORE_ID:-not specified}"
echo "  OTP:      $OTP"

# â”€â”€ Step 1: Heartbeat â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
step "Step 1: Heartbeat (auth check)"

if [ -n "$STORE_ID" ]; then
    HB_BODY=$(printf '{"storeId":"%s"}' "$STORE_ID")
else
    HB_BODY='{"storeId":null}'
fi

HB_RESPONSE=$(device_call "POST" "/api/v1/device/heartbeat" "$HB_BODY")

if echo "$HB_RESPONSE" | jq -e '.error' &>/dev/null; then
    fail "Heartbeat failed: $(echo "$HB_RESPONSE" | jq -r '.error.message')"
fi

SERVER_TIME=$(echo "$HB_RESPONSE" | jq -r '.serverTimeUtc')
ok "HMAC auth verified. Server time: $SERVER_TIME"

# â”€â”€ Step 2: OTP Release â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
step "Step 2: OTP Release"

if [ -n "$STORE_ID" ]; then
    RELEASE_BODY=$(printf '{"otp":"%s","storeId":"%s"}' "$OTP" "$STORE_ID")
else
    RELEASE_BODY=$(printf '{"otp":"%s"}' "$OTP")
fi

RELEASE_RESPONSE=$(device_call "POST" "/api/v1/device/release" "$RELEASE_BODY")

if echo "$RELEASE_RESPONSE" | jq -e '.error' &>/dev/null; then
    ERR=$(echo "$RELEASE_RESPONSE" | jq -r '.error.message')
    fail "Release failed: $ERR"
fi

JOB_ID=$(echo "$RELEASE_RESPONSE" | jq -r '.jobId')
FILE_TOKEN=$(echo "$RELEASE_RESPONSE" | jq -r '.fileToken.token')
TOKEN_TTL=$(echo "$RELEASE_RESPONSE" | jq -r '.fileToken.expiresInSeconds')
COPIES=$(echo "$RELEASE_RESPONSE" | jq -r '.jobSummary.copies')
COLOR=$(echo "$RELEASE_RESPONSE" | jq -r '.jobSummary.color')
PRICE_CENTS=$(echo "$RELEASE_RESPONSE" | jq -r '.jobSummary.priceCents')

ok "Job released: $JOB_ID"
ok "Print job: $COPIES copies, $COLOR, â‚¹$(echo "scale=2; $PRICE_CENTS/100" | bc 2>/dev/null || echo "$PRICE_CENTS cents")"
ok "File token valid for: ${TOKEN_TTL}s (use it quickly)"

# â”€â”€ Step 3: Download file â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
step "Step 3: File Download"

DOWNLOAD_PATH="/api/v1/device/printjobs/${JOB_ID}/file"
DL_AUTH=$(sign "GET" "$DOWNLOAD_PATH")
DL_TS="${DL_AUTH%%:*}"
DL_SIG="${DL_AUTH##*:}"

OUTPUT_FILE="$OUTPUT_DIR/printnest-${JOB_ID}.pdf"

HTTP_STATUS=$(curl -s \
    -w "%{http_code}" \
    -o "$OUTPUT_FILE" \
    -X GET "$API_URL$DOWNLOAD_PATH" \
    -H "X-Device-Id: $DEVICE_ID" \
    -H "X-Timestamp: $DL_TS" \
    -H "X-Signature: $DL_SIG" \
    -H "Authorization: Bearer $FILE_TOKEN")

if [ "$HTTP_STATUS" != "200" ]; then
    # Try to read error from the output file (ErrorHandlingMiddleware writes JSON)
    ERR=$(cat "$OUTPUT_FILE" 2>/dev/null | jq -r '.error.message' 2>/dev/null || echo "unknown")
    rm -f "$OUTPUT_FILE"
    fail "File download failed (HTTP $HTTP_STATUS): $ERR"
fi

FILE_SIZE=$(wc -c < "$OUTPUT_FILE" | tr -d ' ')
ok "File saved: $OUTPUT_FILE ($FILE_SIZE bytes)"

# â”€â”€ Step 4: Printing started â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
step "Step 4: Mark Printing Started"

CUPS_JOB_ID="sim-$(date +%s)"
PRINTING_BODY=$(printf '{"cupsJobId":"%s","printerName":"simulated-printer-lp0"}' "$CUPS_JOB_ID")
PRINTING_RESPONSE=$(device_call "POST" "/api/v1/device/printjobs/${JOB_ID}/printing-started" "$PRINTING_BODY")

if echo "$PRINTING_RESPONSE" | jq -e '.error' &>/dev/null; then
    fail "printing-started failed: $(echo "$PRINTING_RESPONSE" | jq -r '.error.message')"
fi

ok "Status: $(echo "$PRINTING_RESPONSE" | jq -r '.status')"

# â”€â”€ Step 5: Completed â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
step "Step 5: Mark Completed"

COMPLETE_BODY=$(printf '{"cupsJobId":"%s"}' "$CUPS_JOB_ID")
COMPLETE_RESPONSE=$(device_call "POST" "/api/v1/device/printjobs/${JOB_ID}/completed" "$COMPLETE_BODY")

if echo "$COMPLETE_RESPONSE" | jq -e '.error' &>/dev/null; then
    fail "completed failed: $(echo "$COMPLETE_RESPONSE" | jq -r '.error.message')"
fi

ok "Status: $(echo "$COMPLETE_RESPONSE" | jq -r '.status')"

# â”€â”€ Summary â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
echo ""
echo "â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”"
echo "  âœ“ Full device flow complete"
echo "  Job:  $JOB_ID"
echo "  File: $OUTPUT_FILE"
echo ""
echo "  The job is now Completed. CleanupWorker will delete the file"
echo "  from MinIO within the next 60 seconds."
echo "â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”â”"
