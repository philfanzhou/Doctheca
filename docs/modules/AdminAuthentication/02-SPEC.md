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
remain validated. Discovery/JWKS supplies signing keys only: issuer trust is an immutable
snapshot of the trimmed, nonempty, Ordinal-distinct `Issuer` and `AdditionalValidIssuers`.
Token issuers must match this snapshot exactly, including case and trailing slashes. A discovery
issuer outside it is rejected with a fixed diagnostic naming only these configuration keys. `DocthecaAdmin` requires an authenticated
administrator (`role=admin`, case-insensitive). Missing required Bearer trust refuses credentials
with 401; complete invalid trust still refuses startup. Legacy token Cookies are ignored.

The current SPA reads only hosted session status, fetches CSRF on demand and navigates to
SignaCore for authentication. A 401 clears UI state without refresh or request replay. A 403
shows denial. Tokens, secrets and passwords never enter browser storage or application logs.

Before upgrading, verify `IdentityService:Issuer` against actual issued tokens and list any
intentional migration issuers in `AdditionalValidIssuers`; remove those after the migration.
Deployments relying on automatic discovery issuer acceptance will now receive 401. Rolling
back the image with its matching configuration restores the previous behavior and its known
issuer trust gap. Signature-key compromise remains outside this guarantee. Hosted ID/access
tokens retain their separate exact Authority and client-audience contract.
