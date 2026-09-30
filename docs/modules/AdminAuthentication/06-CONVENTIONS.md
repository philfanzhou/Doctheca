# 06-CONVENTIONS — Doctheca Admin Authentication Conventions

## Paths

| Kind | Prefix |
|------|------|
| Browser authentication | `/admin/auth` |
| Browser admin API | `/admin` |
| Anonymous health probes | `/health/live`, `/health/ready`, `/health` (readiness alias) |

## Policy and configuration naming

| Name | Value |
|------|----|
| Admin authorization policy | `DocthecaAdmin` |
| Access Cookie | `docthecaAccessToken` |
| Refresh Cookie | `docthecaRefreshToken` |
| Authority configuration | `IdentityService:Authority` |
| Issuer configuration | `IdentityService:Issuer` |
| Audience configuration | `IdentityService:Audience` |
| AppId deployment secret | `IdentityService:AppId` |
| AppSecret deployment secret | `IdentityService:AppSecret` |
| Cookie Secure configuration | `Authentication:CookieSecure` |

## Errors and logging

- Externally facing authentication errors use concise, stable English messages.
- 401 means no valid identity; 403 means a valid identity without the admin role.
- Error codes use the `DOCTHECA_` prefix.
- Never log passwords, Access Tokens, Refresh Tokens, or Cookies.
- Identity failures log only the HTTP status, correlation ID, and a generic failure category.

## Frontend

- Never use localStorage/sessionStorage to store authentication material.
- API modules must reuse the single shared Axios instance.
- A 401 on auth endpoints does not trigger recursive refresh.
- 403 is not retried automatically.
- The login form must use `autocomplete="username"` and `autocomplete="current-password"`.
