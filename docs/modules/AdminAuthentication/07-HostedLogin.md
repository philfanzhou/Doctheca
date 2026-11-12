# 07-HostedLogin — Hosted Login, Server Sessions and Standard Logout

Doctheca delegates administrator authentication to the SignaCore hosted page through a
Confidential authorization-code client with S256 PKCE. Passwords never pass through this flow;
access and ID tokens remain in server memory. Hosted login is the only browser authentication
path. Complete configuration activates it automatically. Missing required values permit startup
but all six `/admin/auth/oidc` endpoints return fixed 503; there is no password fallback.
Old POST login/refresh/logout routes return fixed 410 JSON without body binding or provider calls.
Old access/refresh Cookies no longer authenticate, refresh or revoke sessions.

## Shared and local responsibilities

The OIDC protocol runs on the framework's own OpenID Connect handler
(`Microsoft.AspNetCore.Authentication.OpenIdConnect`, referenced as a package in .NET 10); no
SignaCore client package is involved any more — the official `SignaCore.Client.AspNetCore`
package is in its deprecation window (SignaCore ADR 0010). The framework handler owns the
authorization-code request with PKCE, the correlation/nonce browser binding, standard ID-token
validation, and the standard RP-initiated logout through Discovery's `end_session_endpoint`.
Doctheca keeps a thin adaptation layer in `src/Host/Authentication`: the availability precheck
with fixed 503 endpoints, the strict sign-in-entry input guard, the strict authorization-response
shape (single-valued protocol parameters and the RFC 9207 issuer check), the pre-sign-in
administrator gate on the verified ID-token principal, the fixed JSON/redirect presentation
(`AdminLoginResponseWriter`), the capacity-bounded server-side ticket store
(`AdminOidcTicketStore`), the cookie-session request boundary with the verified-Bearer scheme
split and the antiforgery validation (`AdminOidcSessionMiddleware`), the antiforgery and
backchannel configuration, and the backchannel timeout normalization. The admin policy's
`role=admin` assertion and the existing JwtBearer path are unchanged.

## Configuration and application registration

| Key | Required | Meaning |
| --- | --- | --- |
| `AdminOidc:RedirectUri` | yes | Exact callback URI ending in `/admin/auth/oidc/callback`. |
| `AdminOidc:PostLogoutRedirectUri` | yes | Exact post-logout URI ending in `/admin/auth/oidc/logout/return`, on the same origin as the callback. |
| `IdentityService:Authority` | yes | Trusted SignaCore issuer/base URI. |
| `IdentityService:AppId` / `AppSecret` | yes | Confidential client ID and server-only secret. |
| `IdentityService:ClockSkewSeconds` | no; default 30 | Token skew, 0–300 seconds (parsed as a startup contract; the hosted slice pins the documented 30-second tolerance). |

Both registered callback URIs must be canonical absolute HTTP or HTTPS URIs, at most 500 ASCII
characters, without query, fragment, user information, wildcard or control characters. Their
scheme, host and port must match. Both schemes work in every environment for DNS names and IP
addresses, with no opt-in or host allowlist. Authority accepts either scheme independently;
its existing structural and discovery-origin checks still apply. Missing required configuration logs `DOCTHECA_OIDC_NOT_CONFIGURED` with missing key names only
and returns 503 on every hosted route without pending state, tickets or provider calls. Complete
nonempty invalid URI/skew configuration fails startup with the key name only. Restart after
restoring configuration. Missing Bearer trust safely refuses header credentials with 401. Inject credentials through environment
variables or Consul KV. Configuration objects hide their values when formatted.

### Transport profiles

The configured **external callback scheme** controls every Cookie carrier: the framework's
correlation and nonce cookies of the sign-in handshake, `docthecaAdminSession`, and
`docthecaAdminCsrf`. HTTPS uses Secure Cookies; HTTP omits Secure, including when an inbound
request uses a different scheme. Cookie clearing uses the same profile. Names, HttpOnly,
SameSite, paths and lifetimes are unchanged. The CSRF Cookie remains HttpOnly/SameSite=Strict
and uses `X-CSRF-TOKEN`; the other carriers use SameSite=Lax.

Transport protection is a deployment decision. Plain HTTP provides no network confidentiality,
integrity or listener authenticity; deployers choose network/TLS protection for the browser and
identity backchannel. Register exact effective external URIs on both sides. Behind TLS
termination, keep HTTPS callbacks and the existing trusted-proxy/forwarded-scheme configuration:
ASP.NET needs an effective HTTPS request to issue and validate its Always-secure antiforgery
credentials. This change does not redesign proxy trust or downgrade HTTPS Cookies for internal
HTTP hops.

Bearer discovery keeps its independent `IdentityService:RequireHttpsMetadata=true` default.
An intentionally HTTP Authority **or Issuer** requires explicit `false`; HTTP callbacks alone
with HTTPS identity metadata do not. The hosted slice enables `RequireHttpsMetadata=false` on its
own handler exactly when the configured Authority itself is plain HTTP. Issuer/audience trust
remains unchanged. HTTPS deployments need no configuration migration.

Before rollout, register SignaCore in this order:

1. Create a **Confidential** application and protect the one-time client secret server-side.
2. Set **PerApplication** audience (`aud = appId`). Migrate downstream access-token validators
   before switching the audience; existing Bearer trust settings remain independent.
3. Register `<origin>/admin/auth/oidc/callback` as `Redirect`, and
   `<origin>/admin/auth/oidc/logout/return` as `PostLogout`, where `<origin>` is the effective
   external HTTP or HTTPS scheme, host and port. Register the exact
   normalized values used in configuration; path case, query and trailing slash differences
   cannot be substituted at runtime. The post-logout registration is what the standard
   RP-initiated logout presents as `post_logout_redirect_uri` at `end_session_endpoint`.
4. Enable authorization code with `openid profile` and refresh disabled. Doctheca never requests
   `offline_access`, uses neither userinfo nor an automatic refresh path.

The wire contract is standard OIDC: Discovery (including `end_session_endpoint`), the
authorization endpoint (code + PKCE S256, response mode `query`), the token endpoint, and
RP-initiated logout `GET /oauth2/session/end?id_token_hint=...&post_logout_redirect_uri=...&state=...`
exactly as SignaCore publishes it since ADR 0010 stages 1/2/4. The client authenticates the
token request with the secret in the form body (`client_secret_post`), which SignaCore accepts
alongside HTTP Basic.

## Browser contract

Unconfigured requests return
`503 {"success":false,"message":"Hosted sign-in is not configured."}`.

| Route | Configured result |
| --- | --- |
| `GET /admin/auth/oidc/start?returnUrl=...` | 302 to the trusted authorization endpoint with code, S256 PKCE, `openid profile`, state and nonce. Short-lived HttpOnly correlation and nonce Cookies are scoped to the callback path; the browser must return them. Missing/empty return URL defaults to `/`. Invalid, duplicate or unsupported input returns a fixed 400 before creating pending state. Discovery failure redirects to `/?authError=identityUnavailable`. |
| `GET /admin/auth/oidc/callback` | Intercepts the sign-in response (single-valued protocol parameters, RFC 9207 issuer, correlation cookie, nonce and ID-token validation required), then signs in only when the pre-sign-in gate allows the verified ID-token `role=admin`. Success redirects to the validated station-local return path. Failure redirects to `/?authError=signInFailed`; a denied or cancelled sign-in and a transport failure redirect to `/?authError=notAdmin` and `/?authError=identityUnavailable` respectively; protocol input is never echoed. |
| `GET /admin/auth/oidc/session` | 200 `{"success":true,"data":{"authenticated":true,"reason":"authenticated","username":"...","expiresAt":"..."}}` for a live session; absent/expired session is 401 `{"success":false,"message":"Re-authentication is required.","data":{"authenticated":false,"reason":"reauthenticationRequired"}}`. Username comes from the verified ID token; the expiry is read back from the server-side ticket. |
| `GET /admin/auth/oidc/csrf` | 200 `{"token":"..."}` and HttpOnly antiforgery Cookie. The SPA reads the `token` member. |
| `POST /admin/auth/oidc/logout` | Requires the antiforgery credential: a missing/invalid token answers 400 `{"outcome":"csrf_rejected"}`, even with a Bearer header. Revokes and clears the local session, then redirects (302) to the authority's end-session endpoint carrying the server-held ID token as `id_token_hint`, the registered post-logout URI and a protected `state`; a request without a live session, and one whose upstream redirect cannot be constructed, answers 200 `{"outcome":"local_only"}` with the local session still revoked. |
| `GET /admin/auth/oidc/logout/return?state=...` | A state that unprotects redirects to `/?authResult=signedOut`; a missing, invalid or duplicated state answers the fixed 400 HTML page. It never signs in and never echoes input. |

The logout endpoint performs a redirect instead of returning a navigation URL: the browser (or
SPA) follows the end-session URL after the local revocation, and `local_only` claims only local
revocation. Upstream unavailability (for example a Discovery document without a usable
`end_session_endpoint`) cannot resurrect the local session, and a request is never retried.
The redirect target is the authority's own published endpoint; Doctheca sends the exact
registered post-logout URI (never a request-scheme-derived one, which a TLS-terminating proxy
would break). The `state` is a data-protected value, so a valid return is idempotent: a replay
lands on the same SPA result rather than a one-time 400.

Return URLs accept station-local absolute paths, including SPA hash routes such as `/#docs`.
External origins, encoded alternate origins, backslashes, control characters, dot segments and
`/admin/auth` paths are rejected. Login code/state never enter the SPA or business return URL.

The correlation and nonce Cookies follow the configured transport profile above, are
SameSite=Lax and expire with the handshake window. A missing, wrong or other-sign-in correlation
Cookie fails before code redemption and creates no session; a callback finishes its matching
Cookies. Application pages, redirects and logs never echo protocol values.

## Session, token and CSRF guarantees

The framework handler verifies the ID token's signature against the published JWKS, its issuer
against the Discovery issuer, its application audience and its lifetime (30-second skew), and
requires the handshake nonce. Standard OIDC semantics apply: a multi-valued `aud` containing the
client id is accepted, no `iat` claim is demanded, an unknown `kid` falls back to the whole JWKS,
and an expiry is not cross-checked against `iat`. Doctheca refuses an unsecured (`alg=none`) ID
token explicitly — the framework skips the signature requirement for tokens received over its
authenticated backchannel — and the sign-in session principal carries the verified ID-token
claims. Only the verified administrator role decides sign-in; a denied or cancelled sign-in
creates no new ticket or session Cookie and does not renew or replace an existing session.

The access token is no longer client-validated (a standard relying party consumes it
opportunistically); the session's expiry is taken from the token response's `expires_in`
instead of the token's own claims, so the session still never outlives the issued access-token
lifetime. Keep Identity and Host clocks synchronized; no new configuration key or SQL migration
is needed.

`docthecaAdminSession` is HttpOnly and SameSite=Lax; its Secure attribute follows the configured
callback scheme; it contains a data-protected reference to the server-side ticket, never a
token. The ticket keeps both tokens server-side. Expired/unknown Cookie requests to protected
`/admin/*` return fixed re-authentication 401. There is no automatic refresh or write replay.

For non-safe Cookie requests to protected admin routes (outside the legacy `/admin/auth` area),
include both the `docthecaAdminCsrf` Cookie and `X-CSRF-TOKEN` from the csrf endpoint's `token`.
Missing/invalid credentials return fixed 403. Antiforgery pairs are issued and validated against
the ambient anonymous principal of the auth area, so a pair works before or after sign-in. A
Bearer request remains on the existing path, even alongside an active or expired session
Cookie, only after the Authorization credential actually validates; it cannot borrow the Cookie's
administrator role. A forged header cannot bypass the Cookie CSRF check. Hosted logout always
uses its session CSRF boundary. Old `docthecaAccessToken` and `docthecaRefreshToken` Cookies are
ignored.

The in-memory ticket store holds at most 4096 sessions (capacity-bounded, evicting the
least-recently-used ticket when full — the declared trade-off against the replaced package's
refuse-when-full policy) and is swept periodically. Revocation removes the ticket before the
upstream redirect is handed to the browser; a stale session Cookie copy returns 401 after
revocation. Session cookies are data-protected references, so replicas must share the ASP.NET
data-protection key ring (single-process deployments are unaffected).

Tokens, client secrets and PKCE verifiers stay in memory/backchannel only; the backchannel is
cookie-free, never follows redirects, writes no request/response log line, and normalizes
timeouts into transport failures. Code/state occur only in the required trusted
authorization/callback or logout protocol, never business navigation, JSON errors or logs; the
framework handler's protocol-message logging category is silenced for the same reason, and
hosting diagnostics are disabled even when required login configuration is missing because they
log raw callback query strings before application middleware can redact them. Failure results
never include upstream payloads or exception details.

## Deployment, upgrade and rollback

This feature adds no database schema/migration, persistent session dependency, object-storage
write or port change (5012). **Only one process/instance is supported**; restart or rollout
invalidates every in-process session. SignaCore outages can prevent login or upstream logout.
Already issued access tokens may remain valid until expiry after logout.

Deploy the API and current SPA together. Complete exact application registrations and credential
injection before rollout. `AdminOidc:Enabled` no longer controls availability, even if an old
configuration still sets it to false. `Authentication:CookieSecure` and its startup variable are
retired; hosted Cookie security follows the configured callback scheme.

See [Breaking changes, upgrade and rollback](../../development/HostedLoginUpgrade.md). Rollback
to the preceding image requires its matching API/SPA and HTTPS-compatible configuration: that
image again refuses Production HTTP. Restore HTTPS registration/configuration before rolling
back, then sign in again. No SQL or data rollback is required.

## Administration SPA

The login card provides one top-level SignaCore navigation. It reconstructs return URLs only
from exact `#overview`, `#docs`, `#results`, `#search`, or `#detail/<id>` routes; detail IDs
contain only ASCII letters, digits, underscores and hyphens. Arbitrary search/path/protocol
parameters are never forwarded. The SPA maps single fixed `authError`/`authResult` values to
local messages and removes those parameters from browser history. It does not read protocol
code/state or read/store tokens or secrets.

Session restoration calls only `/admin/auth/oidc/session`. CSRF is retrieved on demand and kept
in memory; all writes, including multipart upload and logout, carry `X-CSRF-TOKEN`.
Initialization and CSRF requests each share one in-flight operation. Generation checks prevent
late responses from restoring revoked UI or authorizing writes after expiry. A 401 clears the
session and requests explicit reauthentication; a 403 displays denial without navigating,
refreshing credentials, or replaying the request. Network failure never implies authentication.
Logout is single-flight, clears displayed session data, and follows the redirect semantics: the
standard sign-out leaves this origin for SignaCore's end-session page, so the SPA lands on the
fixed `/?authResult=signedOut` result after the chain. A local-only response warns that the
SignaCore session may remain active; a rejected request displays a fixed failure without claiming
server revocation. A cancelled sign-in and a denied one share the merged notAdmin message.

Run `cd frontend && npm test` for transport-timing/state tests and `npm run test:e2e` for real
Chromium tests against the compiled SPA, Kestrel, PostgreSQL 16 and the fake SignaCore public
pages/backchannel. The latter also runs in the full .NET suite and builds the current SPA.
Install the matching browser after building the test project with
`pwsh src/Tests/Doctheca.Tests/bin/Release/net10.0/playwright.ps1 install chromium`
(`--with-deps` on Linux). No browser or Playwright package is included in the runtime image.
Test contexts are isolated and do not record trace/HAR/storage state or protocol values.
