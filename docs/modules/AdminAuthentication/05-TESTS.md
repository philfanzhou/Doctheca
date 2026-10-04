# 05-TESTS — Doctheca Admin Authentication Tests

Backend coverage uses real Program.cs, isolated PostgreSQL 16, synthetic signing keys and a fake
SignaCore authority. It checks dual-token validation, non-admin denial, cancellation/replay,
expiry/restart, identity-bound CSRF, mixed Bearer/session credentials, local-first revocation,
prepared logout, concurrent/repeated exit and safe failure results.

Retirement regressions send empty, malformed, JSON and non-JSON bodies to all old POST routes
with old Cookies and a live session: fixed 410, no token Cookie, no provider call and no hosted
session revocation. Signed old access Cookies alone return 401. Each missing required hosted
key permits startup, returns 503 on all six routes, creates no state/ticket and logs only fixed
code/key names. Missing Bearer trust refuses authentication; complete invalid trust is rejected.
A complete hosted configuration remains active even when an old `Enabled=false` is present.
At framework Trace level, callback and logout-return query canaries remain absent from logs
with complete configuration and with each required key missing. The capture consumes the real
host's logger filters and verifies that unrelated Trace messages still reach the capture.

```bash
dotnet build src/Doctheca.sln --configuration Release
dotnet test src/Doctheca.sln --configuration Release --no-build
cd frontend && npm ci && npm run build && npm test && npm run test:e2e
```

The browser suite loads the current compiled SPA using real Chromium/Kestrel/PG16. It covers
login, deep links, fixed provider failures, expiry/reauthentication and prepared/local-only logout.
No real credentials, traces, HAR or browser storage state are recorded.

Build the release image and run `python3 scripts/verify-container-startup.py IMAGE` for real
container startup/refusal, health, missing-login configuration and graceful shutdown checks,
including protocol query canaries at Trace level in the unconfigured-login container.
