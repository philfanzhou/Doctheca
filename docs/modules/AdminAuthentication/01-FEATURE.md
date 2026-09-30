# 01-FEATURE — Doctheca Admin Authentication

## Feature overview

The Doctheca admin console logs in with SignaCore's bootstrap administrator account. The browser admin API only accepts Identity JWTs with a valid signature that contain `role=admin`; ordinary Identity users cannot establish a Doctheca admin session even with a correct username and password.

The admin frontend continues to be statically hosted by the Doctheca Host and deployed same-origin with the backend. Access Tokens and Refresh Tokens are stored only in HttpOnly Cookies, never returned to frontend JavaScript, and never written to localStorage.

## User stories

> As a Doctheca administrator, I want to log in with an Identity administrator account and keep the session secure, so that only Identity-verified administrators can view or change Doctheca data.

## Acceptance criteria

| ID | Acceptance criterion |
|------|----------|
| AC-01 | Accessing `/admin/*` other than the auth endpoints without login returns 401 |
| AC-02 | Accessing `/admin/*` with a valid ordinary-user JWT returns 403 |
| AC-03 | The Identity bootstrap administrator logs in successfully with the correct password |
| AC-04 | A wrong username or password returns a uniform 401 without leaking account existence or password-validation details |
| AC-05 | A correct ordinary-account login returns a uniform 403 and establishes no Cookie session |
| AC-06 | The login proxy calls `POST /api/auth/token` with a camelCase request body and Doctheca's own App credentials |
| AC-07 | Doctheca validates the JWT signature, issuer, audience, expiry, and `role=admin` before setting Cookies |
| AC-08 | Access Tokens and Refresh Tokens are stored only in HttpOnly, SameSite=Strict Cookies |
| AC-09 | After the Access Token expires, one-time token rotation completes via the Refresh Cookie |
| AC-10 | Logout calls Identity to revoke the Refresh Token and unconditionally clears the local Cookies |
| AC-11 | The SPA, login page, static files, and the health probes (`/health/live`, `/health/ready`, `/health`) remain anonymously accessible |
| AC-12 | Logs never record passwords, JWTs, Refresh Tokens, or Cookies |

## Scope

### In scope

- Identity password login proxy, JWT admin-role validation, Cookie session, refresh, and logout
- Admin authorization policy for the `/admin/*` endpoints
- Frontend login page, startup session check, automatic refresh, 401/403 handling, and logout entry
- Consul shared configuration of the Identity Authority, Doctheca App deployment credentials, and Cookie security attributes
- Backend authentication/authorization tests and frontend build verification

### Out of scope

- Modifying SignaCore's initial administrator creation or `role=admin` injection logic
- Creating or managing Identity users in Doctheca
- Storing or displaying any Token in the frontend
- Reserving internal service interfaces or extra service credentials with no real callers

## Related documents

- [02-SPEC.md](./02-SPEC.md)
- [03-DESIGN.md](./03-DESIGN.md)
