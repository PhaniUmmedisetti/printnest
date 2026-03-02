#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# PrintNest — Device Provisioning Script
#
# Registers a new Raspberry Pi device with the PrintNest API and writes
# its credentials to a .env file ready to be copied to the Pi.
#
# Usage:
#   STAFF_USERNAME=<username> STAFF_PASSWORD=<password> ./provision-device.sh <DEVICE_ID> [STORE_ID]
#
#   DEVICE_ID must start with "dev_". Example: dev_store1_abc12345
#   STORE_ID  optional — associates the device with a store at registration time
#
# Environment variables:
#   STAFF_USERNAME (required) — staff/super-admin username
#   STAFF_PASSWORD (required) — staff/super-admin password
#   API_URL        (optional) — defaults to http://localhost:5000
#   OUTPUT_DIR     (optional) — where to write the .env file, defaults to .
#
# Example:
#   STAFF_USERNAME=admin STAFF_PASSWORD=your-password ./provision-device.sh dev_hyd_supermart_01 store_supermart_01
#
# Output:
#   Creates <DEVICE_ID>.env in OUTPUT_DIR with the device credentials.
#   Copy this file to the Pi at /home/pi/printnest/.env
#
# Requirements: curl, jq
# ─────────────────────────────────────────────────────────────────────────────

set -euo pipefail

# ── Config ────────────────────────────────────────────────────────────────────
API_URL="${API_URL:-http://localhost:5000}"
STAFF_USERNAME="${STAFF_USERNAME:?Error: STAFF_USERNAME environment variable is required}"
STAFF_PASSWORD="${STAFF_PASSWORD:?Error: STAFF_PASSWORD environment variable is required}"
DEVICE_ID="${1:?Usage: $0 <DEVICE_ID> [STORE_ID]}"
STORE_ID="${2:-}"
OUTPUT_DIR="${OUTPUT_DIR:-.}"

# ── Preflight ─────────────────────────────────────────────────────────────────
if ! [[ "$DEVICE_ID" =~ ^dev_ ]]; then
    echo "Error: DEVICE_ID must start with 'dev_'" >&2
    echo "  Example: dev_supermart_hyd_01" >&2
    exit 1
fi

for cmd in curl jq; do
    if ! command -v "$cmd" &>/dev/null; then
        echo "Error: '$cmd' is required. Install with: apt install $cmd" >&2
        exit 1
    fi
done

# ── Register device ───────────────────────────────────────────────────────────
echo "→ Registering device '$DEVICE_ID'..."

LOGIN_PAYLOAD=$(printf '{"username":"%s","password":"%s"}' "$STAFF_USERNAME" "$STAFF_PASSWORD")

LOGIN_BODY=$(curl -s -w '\n%{http_code}' -X POST "$API_URL/api/v1/staff/auth/login" \
    -H "Content-Type: application/json" \
    -d "$LOGIN_PAYLOAD")

LOGIN_STATUS=$(echo "$LOGIN_BODY" | tail -n1)
LOGIN_RESPONSE=$(echo "$LOGIN_BODY" | head -n-1)

if [ "$LOGIN_STATUS" != "200" ]; then
    echo "Error: Login failed (HTTP $LOGIN_STATUS)" >&2
    echo "$LOGIN_RESPONSE" | jq -r '.error.message' 2>/dev/null || echo "$LOGIN_RESPONSE" >&2
    exit 1
fi

ACCESS_TOKEN=$(echo "$LOGIN_RESPONSE" | jq -r '.accessToken')
if [ -z "$ACCESS_TOKEN" ] || [ "$ACCESS_TOKEN" = "null" ]; then
    echo "Error: Login response did not include accessToken" >&2
    exit 1
fi

if [ -n "$STORE_ID" ]; then
    PAYLOAD=$(printf '{"deviceId":"%s","storeId":"%s"}' "$DEVICE_ID" "$STORE_ID")
else
    PAYLOAD=$(printf '{"deviceId":"%s","storeId":null}' "$DEVICE_ID")
fi

# Capture HTTP status separately so we can show a clean error
HTTP_BODY=$(curl -s -w '\n%{http_code}' -X POST "$API_URL/api/v1/admin/devices" \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $ACCESS_TOKEN" \
    -d "$PAYLOAD")

HTTP_STATUS=$(echo "$HTTP_BODY" | tail -n1)
RESPONSE=$(echo "$HTTP_BODY" | head -n-1)

if [ "$HTTP_STATUS" != "200" ]; then
    echo "Error: API returned HTTP $HTTP_STATUS" >&2
    echo "$RESPONSE" | jq -r '.error.message' 2>/dev/null || echo "$RESPONSE" >&2
    exit 1
fi

SHARED_SECRET=$(echo "$RESPONSE" | jq -r '.sharedSecret')
CREATED_AT=$(echo "$RESPONSE" | jq -r '.createdAtUtc')

echo "✓ Device registered"
echo "  Device ID:   $DEVICE_ID"
echo "  Store ID:    ${STORE_ID:-none}"
echo "  Created at:  $CREATED_AT"
echo ""

# ── Write .env file ───────────────────────────────────────────────────────────
ENV_FILE="$OUTPUT_DIR/${DEVICE_ID}.env"

cat > "$ENV_FILE" <<EOF
# PrintNest Device Credentials
# KEEP THIS FILE SECRET — treat like a password.
# Generated: $(date -u +"%Y-%m-%dT%H:%M:%SZ")
# Device:    $DEVICE_ID

PRINTNEST_API_URL=$API_URL
PRINTNEST_DEVICE_ID=$DEVICE_ID
PRINTNEST_SHARED_SECRET=$SHARED_SECRET
EOF

if [ -n "$STORE_ID" ]; then
    echo "PRINTNEST_STORE_ID=$STORE_ID" >> "$ENV_FILE"
fi

chmod 600 "$ENV_FILE"

echo "✓ Credentials written to: $ENV_FILE"
echo ""
echo "  Next steps:"
echo "  1. Copy the file to the Pi:"
echo "       scp $ENV_FILE pi@<PI_IP>:/home/pi/printnest/.env"
echo "  2. On the Pi, lock it down:"
echo "       chmod 600 /home/pi/printnest/.env"
echo ""
echo "  ⚠  The shared secret will NOT be shown again."
echo "     If you lose this file, deactivate the device and provision a new one."
