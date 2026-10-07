# 07-HostedLogin — Hosted Login, Server Sessions and Prepared Logout

Doctheca delegates administrator authentication to the SignaCore hosted page through a
Confidential authorization-code client with S256 PKCE. Passwords never pass through this flow;
access and ID tokens remain in server memory. Hosted login is the only browser authentication
path. Complete configuration activates it automatically. Missing required values permit startup
but all six `/admin/auth/oidc` endpoints return fixed 503; there is no password fallback.
Old POST login/refresh/logout routes return fixed 410 JSON without body binding or provider calls.
Old access/refresh Cookies no longer authenticate, refresh or revoke sessions.

## Shared and local responsibilities

Since issue #70 the OIDC protocol runs in the official `SignaCore.Client.AspNetCore` package
(0.1.13): it owns the authorization-code request, the hardened single-use callback, strict ID and
access-token validation, issuer/subject correlation, the bounded server-side ticket/pending/return
stores with their sweep, the user-neutral CSRF boundary, the local-session-first prepared logout,
and secret-free bounded logging. Doctheca keeps a thin adaptation layer in
`src/Host/Authentication`: the availability precheck with fixed 503 endpoints, the strict
sign-in-entry input guard, the pre-sign-in administrator gate on the verified access-token
principal (`AdminPreSignInAuthorizationDecision`), the fixed JSON/redirect presentation
(`AdminLoginResponseWriter`), the cookie-session request boundary with the verified-Bearer scheme
split (`AdminOidcSessionMiddleware`), the antiforgery cookie-name and loopback policies, and the
backchannel timeout normalization. The admin policy's `role=admin` assertion and the existing
JwtBearer path are unchanged.

## Configuration and application registration

| Key | Required | Meaning |
| --- | --- | --- |
| `AdminOidc:RedirectUri` | yes | Exact callback URI ending in `/admin/auth/oidc/callback`. |
| `AdminOidc:PostLogoutRedirectUri` | yes | Exact post-logout URI ending in `/admin/auth/oidc/logout/return`, on the same origin as the callback. |
| `IdentityService:Authority` | yes | Trusted SignaCore issuer/base URI. |
| `IdentityService:AppId` / `AppSecret` | yes | Confidential client ID and server-only secret. |
| `IdentityService:ClockSkewSeconds` | no; default 30 | Token skew, 0–300 seconds. |

Both registered callback URIs must be canonical absolute HTTPS URIs, at most 500 ASCII characters,
without query, fragment, user information or wildcard. Development/Testing alone allow numeric
loopback HTTP (`127.0.0.1` or `[::1]`); `localhost` is rejected. Missing required configuration logs `DOCTHECA_OIDC_NOT_CONFIGURED` with missing key names only
and returns 503 on every hosted route without pending state, tickets or provider calls. Complete
nonempty invalid URI/skew configuration fails startup with the key name only. Restart after
restoring configuration. Missing Bearer trust safely refuses header credentials with 401. Inject credentials through environment
variables or Consul KV. Configuration objects hide their values when formatted.

Before rollout, register SignaCore in this order:

1. Create a **Confidential** application and protect the one-time client secret server-side.
2. Set **PerApplication** audience (`aud = appId`). Migrate downstream access-token validators
   before switching the audience; existing Bearer trust settings remain independent.
3. Register `https://<doctheca-host>/admin/auth/oidc/callback` as `Redirect`, and
   `https://<doctheca-host>/admin/auth/oidc/logout/return` as `PostLogout`. Register the exact
   normalized values used in configuration; path case, query and trailing slash differences
   cannot be substituted at runtime.
4. Enable authorization code with `openid profile` and refresh disabled. Doctheca never requests
   `offline_access`, uses neither userinfo nor an automatic refresh path.

The public wire contract is [SignaCore Hosted Login](https://github.com/philfanzhou/SignaCore/blob/main/docs/integrations/HostedLogin.md)
and [Prepared Logout](https://github.com/philfanzhou/SignaCore/blob/main/docs/oidc/Logout.md).
Prepared logout deliberately uses `/oauth2/logout/requests`; Discovery supplies the authorization,
token and JWKS endpoints and has no standard `end_session_endpoint`.

## Browser contract

Every new route carries the ServiceMantle security response-header baseline, including callback
redirects. Unconfigured requests return
`503 {"success":false,"message":"Hosted sign-in is not configured."}`.

| Route | Configured result |
| --- | --- |
| `GET /admin/auth/oidc/start?returnUrl=...` | 302 to the trusted authorization endpoint with code, S256 PKCE, `openid profile`, state and nonce. Missing/empty return URL defaults to `/`. Invalid, duplicate or unsupported input returns a fixed 400 before creating pending state. Discovery failure redirects to `/?authError=identityUnavailable`. |
| `GET /admin/auth/oidc/callback` | Consumes pending once, validates the single-valued response, callback issuer, nonce and both tokens, then signs in only when the pre-sign-in gate allows the verified access-token `role=admin`. Success redirects to the validated station-local return path. Failure redirects to `/?authError=signInFailed`; a denied or cancelled sign-in and a transport failure redirect to `/?authError=notAdmin` and `/?authError=identityUnavailable` respectively; protocol input is never echoed. |
| `GET /admin/auth/oidc/session` | 200 `{"success":true,"data":{"authenticated":true,"reason":"authenticated","username":"...","expiresAt":"..."}}` for a live session; absent/expired session is 401 `{"success":false,"message":"Re-authentication is required.","data":{"authenticated":false,"reason":"reauthenticationRequired"}}`. Username comes from the verified ID token; the expiry is read back from the server-side ticket. |
| `GET /admin/auth/oidc/csrf` | 200 `{"token":"..."}` (the package shape) and HttpOnly antiforgery Cookie. The SPA reads the `token` member; the retired `data.requestToken` shape no longer exists. |
| `POST /admin/auth/oidc/logout` | Requires the antiforgery credential: a missing/invalid token answers 400 `{"outcome":"csrf_rejected"}`, even with a Bearer header. Atomically revokes and clears the local session first. A verified provider response redirects (302) to the verified same-origin logout URI; every provider failure, timeout and already signed-out request answers 200 `{"outcome":"local_only"}` and never follows an unverifiable URI. |
| `GET /admin/auth/oidc/logout/return?state=...` | Browser-bound, unexpired, single-use state redirects to `/?authResult=signedOut`; any invalid, missing, duplicate, expired or replayed return answers the package's fixed 400 HTML page. It never signs in and never echoes input. |

The logout endpoint performs a redirect instead of returning a navigation URL: the browser (or
SPA) follows the verified logout URI after the local revocation, and `local_only` claims only
local revocation. Upstream unavailability, timeout or request cancellation cannot resurrect the
local session. A concurrent/repeated exit can initiate at most one provider call; requests are
never retried, including ambiguous provider failures. The preparation request uses HTTP Basic
Confidential client authentication with the atomically removed server ID-token snapshot, the exact
post-logout URI, and a random state. Its bounded JSON response must contain exactly one
`logout_uri`: a relative or same-origin URI whose single query field is one 43-character base64url
`logout_handle`. External origins, user information, fragments, extra or duplicate query fields and
malformed handles are rejected; the authority dictates the path within its own origin. The
backchannel uses HTTP Basic client authentication for token redemption as well, and does not
follow redirects.

Return URLs accept station-local absolute paths, including SPA hash routes such as `/#docs`.
External origins, encoded alternate origins, backslashes, control characters, dot segments and
`/admin/auth` paths are rejected. Login code/state never enter the SPA or business return URL.

## Session, token and CSRF guarantees

The package independently verifies both tokens' signatures, issuer, application audience, type
and lifetime, and requires the pending nonce for the ID token. Before the gate runs, the access
token is validated strictly (RS256, published `kid` on the wire, `typ=at+jwt`, single exact
issuer and string audience, nonempty subject, valid `exp/nbf/iat` with a 30-second skew, real
expiry honoured even inside the skew) and both tokens' issuer/subject pairs must match; invalid
or uncorrelated tokens fail closed without invoking the gate. Only the verified access-token
administrator role decides sign-in; an ID-token administrator claim cannot authorize a non-admin
access token. Denied, failing or timed-out gate decisions, cancellations and rejected callbacks
create no new ticket or session Cookie and do not renew or replace an existing session. Standard
OIDC validation semantics apply to the ID token (a multi-valued `aud` containing the client id is
accepted and no `iat` claim is demanded); the sign-in session principal carries the verified
ID-token claims.

`docthecaAdminSession` is HttpOnly, Secure and SameSite=Lax; it contains the raw opaque key of
the server-side ticket, never a token. The ticket keeps both tokens server-side and expires no
later than the verified access-token expiry. Expired/unknown Cookie requests to protected
`/admin/*` return fixed re-authentication 401. There is no automatic refresh or write replay.

For non-safe Cookie requests to protected admin routes (outside the legacy `/admin/auth` area),
include both the `docthecaAdminCsrf` Cookie and `X-CSRF-TOKEN` from the csrf endpoint's `token`.
Missing/invalid credentials return fixed 403. The package issues and validates antiforgery pairs
user-neutrally — bound to the per-browser Cookie alone — so a pair works before or after sign-in.
A Bearer request remains on the existing path, even alongside an active or expired session
Cookie, only after the Authorization credential actually validates; it cannot borrow the Cookie's
administrator role. A forged header cannot bypass the Cookie CSRF check. Hosted logout always
uses its session CSRF boundary. Old `docthecaAccessToken` and `docthecaRefreshToken` Cookies are
ignored.

Pending sign-ins and logout returns expire after five minutes. The in-memory ticket store holds
at most 4096 sessions (capacity-bounded, refusing rather than evicting) and all stores are swept
periodically. Logout return state is bound to a separate HttpOnly, Secure, SameSite=Lax Cookie
(named after the session Cookie) scoped to the logout paths. Revocation removes the ticket in one
locked operation and at most one preparation per session is possible; a stale session Cookie copy
returns 401 after revocation.

Tokens, client secrets and PKCE verifiers stay in memory/backchannel only; the package's back
channel is cookie-free, never follows redirects, and is excluded from HttpClient instrumentation.
Code/state occur only in the required trusted authorization/callback or logout-return protocol,
never business navigation, JSON errors or logs. The package's operation log is a closed
operation/outcome vocabulary; hosting diagnostics are disabled even when required login
configuration is missing because they log raw callback query strings before application
middleware can redact them. Failure results never include upstream payloads or exception details.

## Deployment, upgrade and rollback

This feature adds no database schema/migration, persistent session dependency, object-storage
write or port change (5012). **Only one process/instance is supported**; restart or rollout
invalidates every in-process session/pending/return. SignaCore outages can prevent login or
upstream logout. Already issued access tokens may remain valid until expiry after logout.

Deploy the API and current SPA together. Complete exact application registrations and credential
injection before rollout. `AdminOidc:Enabled` no longer controls availability, even if an old
configuration still sets it to false. `Authentication:CookieSecure` and its startup variable are
retired; hosted Cookie security follows the validated callback/environment contract.

See [Breaking changes, upgrade and rollback](../../development/HostedLoginUpgrade.md). Rollback
requires the previous matching API/SPA image and its runtime configuration, restores that image's
old password/token-Cookie contracts and requires reauthentication. No SQL is required.

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
Logout is single-flight, clears displayed session data, and follows the redirect semantics: a
prepared sign-out leaves this origin for SignaCore's verified logout URI, so the SPA lands on the
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
