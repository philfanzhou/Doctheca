# 06-CONVENTIONS — Doctheca Admin Authentication Conventions

| Name | Value |
| --- | --- |
| Administrator policy | `DocthecaAdmin` |
| Hosted authentication routes | `/admin/auth/oidc` |
| Opaque session Cookie | `docthecaAdminSession` |
| CSRF Cookie / header | `docthecaAdminCsrf` / `X-CSRF-TOKEN` |
| Trust configuration | `IdentityService:Authority/Issuer/AdditionalValidIssuers/Audience/RequireHttpsMetadata/ClockSkewSeconds` |
| Confidential credentials | `IdentityService:AppId/AppSecret` (server runtime injection) |
| Exact application callbacks | `AdminOidc:RedirectUri/PostLogoutRedirectUri` |

`AdminOidc:Enabled`, `Authentication:CookieSecure` and `DOCTHECA_COOKIE_SECURE` are retired.
Hosted login is always configured when required values are complete. Secure Cookies are required
outside Development/Testing numeric loopback HTTP.

Use fixed English errors and code/key-only startup diagnostics. Never log passwords, protocol
parameters, tokens, Cookies or client secrets. Browser auth material never enters persistent
storage. Shared Axios transports attach CSRF to every write, do not refresh or replay on 401,
and do not retry on 403. Login uses top-level hosted navigation; there is no password form.
