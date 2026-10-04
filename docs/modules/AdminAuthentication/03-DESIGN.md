# 03-DESIGN — Doctheca Admin Authentication Design

ASP.NET Core OpenID Connect owns code exchange, PKCE, correlation/nonce and token validation;
Cookie authentication owns the opaque reference, JwtBearer owns header credentials, and
antiforgery owns identity-bound CSRF. Doctheca supplies strict dual-token validation, access-token
administrator authorization before sign-in, bounded in-memory stores and prepared logout.
See [shared and local boundaries](07-HostedLogin.md#shared-and-local-responsibilities).

`AdminAuthEndpoints` provides only fixed retirement handlers and the protected read-only
projection. No password client, refresh/revoke adapter or browser token Cookie fallback remains.
`AdminAuthenticationRegistration` retains strict common Bearer validation when trust is complete;
with missing trust its rejecting handler preserves policy/scheme names and returns 401.

The request order is static files, authentication, hosted session/CSRF boundary, authorization,
marked admin endpoints, anonymous health probes and anonymous SPA fallback. API/SPA retain
same-origin deployment and port 5012. AppId/AppSecret remain server-only Confidential credentials.
No database migration, persistent session store or object-storage write is introduced.

## Quaestura boundary

There are no reserved `/internal/question-bank/*` interfaces, import-status writes or unused
service key policies. Legacy tables in existing databases are not automatically dropped.
