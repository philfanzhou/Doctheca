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
curl -i http://localhost:5012/health
```

Neither is expected to return 401. The health check returns 200.

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

> Doctheca is a standalone repository; verification no longer depends on the Ruoyu.Study monorepo's `tests/integration/scripts/pre-commit.sh`. The commands above are this repository's complete local verification (see also the "Verification" section of the root `AGENTS.md`).
