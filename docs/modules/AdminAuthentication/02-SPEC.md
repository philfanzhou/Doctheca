# 02-SPEC — Doctheca Admin Authentication Specification

## HTTP contract

The six `/admin/auth/oidc` routes, opaque session, CSRF and prepared logout are specified in
[07-HostedLogin.md](07-HostedLogin.md). Missing required configuration returns
`503 {"success":false,"message":"Hosted sign-in is not configured."}` on every hosted route.

`POST /admin/auth/login`, `/refresh` and `/logout` remain anonymous retirement handlers:
`410 {"success":false,"message":"Password authentication has been retired. Use hosted sign-in."}`.
They do not bind/read passwords or bodies, call SignaCore, write Cookies or revoke a hosted session.
Malformed JSON and non-JSON bodies receive the same retirement result.

`GET /admin/auth/session` retains the read-only administrator projection (`userId`, `username`,
`roles`, `expiresAt`) for a verified Bearer principal or live opaque session. Anonymous or old
access/refresh Cookies alone return 401; a valid non-admin Bearer returns 403.

## JWT and authorization

Bearer authentication reads only `Authorization: Bearer`. Signature, expiry, configured
`IdentityService:Authority`, `Issuer`/`AdditionalValidIssuers`, `Audience` and `ClockSkewSeconds`
remain validated through OIDC discovery/JWKS. `DocthecaAdmin` requires an authenticated
administrator (`role=admin`, case-insensitive). Missing required Bearer trust refuses credentials
with 401; complete invalid trust still refuses startup. Legacy token Cookies are ignored.

The current SPA reads only hosted session status, fetches CSRF on demand and navigates to
SignaCore for authentication. A 401 clears UI state without refresh or request replay. A 403
shows denial. Tokens, secrets and passwords never enter browser storage or application logs.
