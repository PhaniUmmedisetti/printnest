# Temporary Test Overrides

## OTP Override (kiosk E2E testing)

- File: `Infrastructure/Auth/OtpService.cs`
- Status: removed on 2026-03-07.
- Prior temporary behavior: generated OTP was hardcoded to `999999`.
- Reason it existed: speed up repeated kiosk testing without fetching a new OTP each run.

## Current Production-Safe Behavior

`OtpService.Generate()` must remain on secure random generation:

```csharp
var plaintext = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
```
