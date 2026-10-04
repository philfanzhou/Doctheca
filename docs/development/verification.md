# Verification Guide

## Prerequisites

Register a Confidential SignaCore client and exact callback/post-logout URIs; inject server-only
credentials and trust settings per [Hosted Login](../modules/AdminAuthentication/07-HostedLogin.md).
Use HTTPS outside Development/Testing numeric loopback. Never include credentials, protocol
parameters, tokens or Cookies in test reports.

## Anonymous Entry Points

```bash
curl -i http://localhost:5012/
curl -i http://localhost:5012/health/live
curl -i http://localhost:5012/health/ready
```

None is expected to return 401. `/health/live` returns `200 {"status":"live"}` whenever the process is up (it never queries the database). `/health/ready` — and its `/health` alias — return `200 {"status":"ready",...}` only when the startup initializer completed and a read-only probe of the mapped PostgreSQL tables/columns succeeded, otherwise `503 {"status":"not_ready",...}` with a value-free safe error code. This is a contract change from the previous `/health`, which always returned `{"status":"healthy","timestamp":...}`; the `timestamp` field is gone.

## Admin API Without Login

```bash
curl -i http://localhost:5012/admin/document-files
```

Expected: 401.

## Hosted Login and Session

Open the SPA and use its SignaCore login button. Authenticate on SignaCore, then confirm the
original local route returns and an administrator can read the protected API. A non-admin or
invalid/cancelled callback must not create a new session. No tokens enter browser storage.
Session restoration uses `/admin/auth/oidc/session`; writes carry the CSRF pair obtained from
`/admin/auth/oidc/csrf`. Missing/wrong CSRF returns 403; expired sessions return reauthentication
401 without refresh or write replay. The old token Cookies alone must return 401.

Use the SPA logout button: local revocation precedes provider preparation, successful preparation
navigates to the validated provider URL, and provider failure reports local-only exit. Repeated
exit cannot resurrect a session or repeat provider preparation.

## Retired Endpoints and Missing Configuration

POST empty, malformed JSON or non-JSON bodies to `/admin/auth/login`, `/refresh` and `/logout`.
Every result must be fixed 410 JSON, without provider calls or token Cookie writes. The old
logout must not revoke a current hosted session. See [Breaking changes](HostedLoginUpgrade.md).

With a required hosted key absent, startup, static serving and health remain available while all
six hosted routes return fixed 503 without state/tickets/provider calls. Logs contain only the
fixed diagnostic and missing key names. Complete malformed trust/URI/skew still rejects startup;
missing Bearer trust returns 401 rather than trusting substitute credentials.

## Removed Endpoints

`/internal/question-bank/*`, the old `/admin/document-parses/importable` and
`POST .../import-status` must all be unmapped.

## Automated Verification

Run at the repository root:

```bash
dotnet build src/Doctheca.sln --configuration Release
dotnet test src/Doctheca.sln --configuration Release --no-build

cd frontend
npm ci && npm run build && npm test && npm run test:e2e
```

### Test prerequisites

- The unit and hosting tests run everywhere the .NET SDK runs; no external service is needed.
- The container-backed integration tests in `Doctheca.Tests.Integration` boot the real
  `Program.cs` host through `WebApplicationFactory` against a one-off PostgreSQL via
  Testcontainers, so they require a reachable Docker daemon (the first run pulls
  `postgres:16-alpine`). They never touch a production Consul, Identity, StructaDoc,
  OpenSearch, or Loki endpoint: Consul points at a closed loopback port with the disk cache
  disabled, the empty `Loki:Uri` disables the remote Loki sink (Console stays on), external
  clients are replaced by in-process doubles, the
  LLM stays disabled, and every credential (including the per-run PostgreSQL password and the
  JWT signing key) is synthetic and never leaves the test process.
- To run only the Docker-free tests:

```bash
dotnet test src/Doctheca.sln --configuration Release \
  --filter "FullyQualifiedName!~Doctheca.Tests.Integration"
```

> Doctheca is a standalone repository; verification no longer depends on the Ruoyu.Study monorepo's `tests/integration/scripts/pre-commit.sh`. The commands above are this repository's complete local verification (see also the "Verification" section of the root `AGENTS.md`).
