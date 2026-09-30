# 07-HostedLogin — SignaCore Hosted Login Callback and Server-Side Sessions

Status: Implemented (issue #47, first slice)

## Overview

Doctheca can delegate administrator sign-in to SignaCore's hosted login page instead of proxying
username/password locally. The browser is redirected to SignaCore, the administrator authenticates
there, and Doctheca receives only a one-time authorization code at its registered callback. Doctheca
never sees the administrator's password, and SignaCore tokens (access and ID) never reach the
browser: they live exclusively in a server-side session ticket behind an opaque cookie.

The feature ships behind a switch and is **disabled by default**. While disabled, every existing
route, the legacy password login, the refresh/logout flow, and the Bearer API behave exactly as
before; the three new endpoints answer a fixed `503`.

## Configuration

| Key | Required | Meaning |
|-----|----------|---------|
| `AdminOidc:Enabled` | no (default `false`) | Turns the hosted-login slice on. `false` = fixed 503 on the three new endpoints, zero behavior change elsewhere. |
| `AdminOidc:RedirectUri` | when enabled | The exact redirect URI registered in SignaCore; must be an absolute HTTPS URI (loopback HTTP allowed in Development/Testing) whose path is exactly `/admin/auth/oidc/callback`, at most 500 ASCII characters. |
| `IdentityService:Authority` | when enabled | SignaCore base address; reused from the existing trust section. |
| `IdentityService:AppId` / `AppSecret` | when enabled | Doctheca's Confidential client credentials; reused from the existing section. |
| `IdentityService:ClockSkewSeconds` | no (default 30) | Token validation clock skew (0–300). |

Enabling with a missing or malformed value fails startup with a diagnostic that contains only the
offending configuration key. Secrets are injected through the environment or Consul KV like every
other credential; `AdminOidcSettings.ToString` hides all bound values.

## SignaCore registration

Perform these steps in the SignaCore admin console before enabling (order matters):

1. Register the application as a **Confidential** client and keep the one-time `appSecret`.
2. Switch the application audience to **PerApplication** (access tokens are issued with
   `aud = appId`; migrate downstream validators first — see the SignaCore standards-conformance
   guide).
3. Register the exact redirect URI: `https://<doctheca-host>/admin/auth/oidc/callback`.
4. Enable the Code flow with `allowedScopes = ["openid", "profile"]` and
   `allowRefreshToken = false`. Doctheca never requests `offline_access`.

## Endpoints

| Route | While disabled | While enabled |
|-------|----------------|---------------|
| `GET /admin/auth/oidc/start?returnUrl=...` | fixed 503 | Validates `returnUrl` (same-site absolute path only; everything else answers a fixed 400), then redirects (302) to the SignaCore authorization endpoint with `response_type=code`, `scope=openid profile`, S256 PKCE, `state`, `nonce`. A missing `returnUrl` targets `/`. Unreachable discovery answers a fixed failure redirect. |
| `GET /admin/auth/oidc/callback` | fixed 503 | Handled by the OpenID Connect handler: verifies `state` (server-side, single-use), `iss`, the correlation/nonce cookies, the ID token (RS256 via JWKS, `typ`, issuer, audience = AppId, lifetime, `nonce`), and the access token (RS256, `typ=at+jwt`, issuer, audience = AppId, lifetime). Only an access token carrying `role=admin` signs in; anything else answers a fixed failure/denied/cancelled redirect and mints nothing. |
| `GET /admin/auth/oidc/csrf` | fixed 503 | Returns the anti-forgery request token for a live session (401 without one). |

All three carry the ServiceMantle security response-header baseline; failures redirect to fixed
SPA locations (`/?authError=...`) that never echo protocol parameters.

## Session semantics

- The session cookie `docthecaAdminSession` is HttpOnly, Secure, SameSite=Lax, and contains only an
  opaque random identifier. The access token, ID token, and all protocol secrets stay in the
  server-side ticket.
- The session lifetime never exceeds the access token's own expiry (15 minutes for a Confidential
  client). When it expires, any request answers the fixed
  `401 {"success":false,"message":"Re-authentication is required."}` result: no automatic refresh,
  no write replay, no downstream call. Renewing means a fresh trip through the hosted login.
- The legacy `docthecaAccessToken` cookie (JwtBearer fallback) keeps working unchanged until the
  password flow is retired; the two cookie names never collide.

### CSRF boundary

While enabled, a cookie-session request to `/admin/*` outside `/admin/auth` must present the CSRF
credential pair for non-safe methods (everything except GET/HEAD/OPTIONS/TRACE): the
`docthecaAdminCsrf` cookie (set by `GET /admin/auth/oidc/csrf`) plus the `X-CSRF-TOKEN` request
header carrying the same response's `requestToken` value. A missing or mismatched credential answers
a fixed 403 and the endpoint never runs. Bearer callers (`Authorization` header) are exempt and
keep the exact legacy behavior.

## Known limitations (first stage)

- **Tickets are in-process memory**: restarting the service drops every session, and only a single
  instance is supported. The store is bounded (4096 tickets) and swept every minute. Multi-instance
  or persistent sessions require a follow-up task.
- No prepared logout, no front-end switch, and the legacy password login is untouched — those are
  deliberately excluded from this slice (tracked separately as #48–#50).

## Security properties

- PKCE S256, single-use server-side `state` (5-minute lifetime, 4096-capacity), `nonce` bound
  through the framework's nonce cookie, correlation cookie enforced.
- `code`, `code_verifier`, tokens, secrets, and `state` never enter responses beyond the protocol
  minimum, logs (the framework's hosting diagnostics are silenced for the OIDC paths), or URLs.
- Endpoints are pinned to the exact registered `RedirectUri`; Host and forwarded headers cannot
  influence it.
- Authorization still requires `role=admin`; the session principal is built from the fully
  validated access token.

## Related

- [01-FEATURE.md](./01-FEATURE.md) — legacy password login (still the default)
- SignaCore integration guide: `docs/integrations/HostedLogin.md` in the SignaCore repository
- Reference implementation: Ruoyu.Admin hosted-login slices (#38, #42, #45, #46)
