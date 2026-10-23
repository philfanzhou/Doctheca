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
Hosted login is always configured when required values are complete. Canonical HTTP and HTTPS
callbacks are accepted in every environment; callback and post-logout origins must match exactly.
The configured external callback scheme selects Secure for HTTPS and no Secure for HTTP on all
four Cookie carriers, including deletion, independently of request scheme. Network/TLS protection
is the deployer's responsibility; plain HTTP provides no confidentiality, integrity or listener
authenticity. HTTP Authority or Issuer still requires `IdentityService:RequireHttpsMetadata=false`;
its default and issuer trust are unchanged.

Use fixed English errors and code/key-only startup diagnostics. Never log passwords, protocol
parameters, tokens, Cookies or client secrets. Browser auth material never enters persistent
storage. Shared Axios transports attach CSRF to every write, do not refresh or replay on 401,
and do not retry on 403. Login uses top-level hosted navigation; there is no password form.
