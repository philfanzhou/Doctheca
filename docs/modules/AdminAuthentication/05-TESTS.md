# 05-TESTS — Doctheca Admin Authentication Tests

## Backend automated tests

| ID | Scenario | Expected |
|------|------|------|
| AUTH-UT-01 | Access the admin API without login | 401 |
| AUTH-UT-02 | Access the admin API with a valid ordinary-user JWT | 403 |
| AUTH-UT-03 | Access the admin API with a valid admin JWT | Request reaches the endpoint |
| AUTH-UT-04 | Admin login with correct credentials | 200, sets two HttpOnly Cookies, response contains no Token |
| AUTH-UT-05 | Login with a wrong password | 401, uniform error message, no Cookie |
| AUTH-UT-06 | Ordinary account login with correct credentials | 403, no Cookie |
| AUTH-UT-07 | Identity returns a forged, wrong-issuer, wrong-audience, expired, or no-admin-role Token | No session established |
| AUTH-UT-08 | Access the admin API after Access Cookie login | Allowed |
| AUTH-UT-09 | Refresh Cookie rotation | New Tokens overwrite Cookies only after all validations succeed |
| AUTH-UT-10 | Invalid Refresh Token | 401 and Cookies cleared |
| AUTH-UT-11 | Logout | Calls revoke and clears Cookies |
| AUTH-UT-12 | Logout when Identity revoke is unavailable | Still clears Cookies and returns 200 |
| AUTH-UT-13 | The health probes (`/health/live`, `/health/ready`, `/health`), static entries, and SPA fallback | Anonymously accessible |
| AUTH-UT-14 | `/internal/question-bank/*` and old import-status paths | Not mapped |
| AUTH-UT-15 | Identity token request | Fields are `grantType`, `username`, `password` / `refreshToken`, and Doctheca AppId/AppSecret headers are sent |
| AUTH-UT-16 | SignaCore returns 401 rejecting the App credentials | Mapped to Identity unavailable, not disguised as a user-password error |

Tests use fixed RSA test keys and a controlled Identity HTTP handler, with no dependency on real passwords or production keys.

## Interface removal regression

- No `/internal/question-bank/*` is mapped.
- No import-status write interface exists.
- Doctheca does not create or update `document_parse_imports`.
- `start.sh` does not inject shared Identity trust or Quaestura (formerly QuestionBank) configuration; it only injects Doctheca App credentials and its own Cookie Secure switch.

## Frontend verification

The frontend currently has no unit test framework configured, and no new npm dependencies are introduced for this feature. Must run:

```bash
cd frontend
npm run build
```

And manually verify: login page shown on first open, wrong password, ordinary account rejection, admin login, automatic refresh, 401/403 redirects, and logout.

## Project verification

Run from the repository root:

```bash
dotnet test src/Tests/Doctheca.Tests/Doctheca.Tests.csproj --configuration Release
dotnet build src/Doctheca.sln --configuration Release
```

When a real Identity is available, additionally run the Cookie session smoke test per `docs/development/verification.md`.
