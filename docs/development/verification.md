# Verification Guide

## Prerequisites

- Doctheca: `http://localhost:5012`
- Identity: `http://localhost:5002`
- SignaCore has completed first-time application setup and created the administrator
- The admin password comes from the first-setup result stored in the deployment secrets
- Doctheca has `IdentityService:Authority` configured

Never commit real passwords, tokens or cookies to the repository or paste them into test reports.

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

## Admin Login

```bash
curl -i -X POST http://localhost:5012/admin/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"<SIGNACORE_ADMIN_PASSWORD>"}' \
  -c doctheca.cookies
```

Expected: 200; the response body contains no `accessToken` or `refreshToken`; `Set-Cookie` includes the HttpOnly `docthecaAccessToken` and `docthecaRefreshToken`.

The username comes from the SignaCore first-time setup and `Admin:Username` in the database; it should not be hardcoded by the Doctheca front end.

## Wrong Password & Regular Accounts

```bash
curl -i -X POST http://localhost:5012/admin/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"wrong-password"}'
```

Expected: 401 with a uniform error message; no auth cookies are set.

Repeat the call with the correct password of a regular Identity account; expected: 403, no auth cookies set.

## Session & Admin Operations

```bash
curl -i http://localhost:5012/admin/auth/session -b doctheca.cookies
curl -i http://localhost:5012/admin/document-files -b doctheca.cookies
```

Expected: 200; the session's `roles` include `admin`; responses contain no tokens.

Upload, parse, metadata, delete, search and export requests must all carry the cookies. For example:

```bash
curl -i -X POST http://localhost:5012/admin/document-files/upload \
  -b doctheca.cookies \
  -F "file=@test.pdf"
```

## Refresh

```bash
curl -i -X POST http://localhost:5012/admin/auth/refresh \
  -b doctheca.cookies \
  -c doctheca.cookies
```

Expected: 200; both cookies are rotated and the response body contains no tokens. An invalid refresh cookie returns 401 and clears the auth cookies.

## Logout

```bash
curl -i -X POST http://localhost:5012/admin/auth/logout \
  -b doctheca.cookies \
  -c doctheca.cookies

curl -i http://localhost:5012/admin/document-files -b doctheca.cookies
```

Expected: logout returns 200; subsequent admin requests return 401.

## Removed Endpoints

`/internal/question-bank/*`, the old `/admin/document-parses/importable` and
`POST .../import-status` must all be unmapped.

## Automated Verification

Run at the repository root:

```bash
dotnet test src/Tests/Doctheca.Tests/Doctheca.Tests.csproj --configuration Release
dotnet build src/Doctheca.sln --configuration Release

cd frontend
npm run build
```

### Test prerequisites

- The unit and hosting tests run everywhere the .NET SDK runs; no external service is needed.
- The container-backed integration tests in `Doctheca.Tests.Integration` boot the real
  `Program.cs` host through `WebApplicationFactory` against a one-off PostgreSQL via
  Testcontainers, so they require a reachable Docker daemon (the first run pulls
  `postgres:16-alpine`). They never touch a production Consul, Identity, StructaDoc,
  OpenSearch, or Loki endpoint: Consul and the log sinks point at a closed loopback port with
  the Consul disk cache disabled, external clients are replaced by in-process doubles, the
  LLM stays disabled, and every credential (including the per-run PostgreSQL password and the
  JWT signing key) is synthetic and never leaves the test process.
- To run only the Docker-free tests:

```bash
dotnet test src/Doctheca.sln --configuration Release \
  --filter "FullyQualifiedName!~Doctheca.Tests.Integration"
```

> Doctheca is a standalone repository; verification no longer depends on the Ruoyu.Study monorepo's `tests/integration/scripts/pre-commit.sh`. The commands above are this repository's complete local verification (see also the "Verification" section of the root `AGENTS.md`).
