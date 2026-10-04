# 07-HostedLogin — Hosted Login, Server Sessions and Prepared Logout

Doctheca delegates administrator authentication to the SignaCore hosted page through a
Confidential authorization-code client with S256 PKCE. Passwords never pass through this flow;
access and ID tokens remain in server memory. Hosted login is the only browser authentication
path. Complete configuration activates it automatically. Missing required values permit startup
but all six `/admin/auth/oidc` endpoints return fixed 503; there is no password fallback.
Old POST login/refresh/logout routes return fixed 410 JSON without body binding or provider calls.
Old access/refresh Cookies no longer authenticate, refresh or revoke sessions.

## Shared and local responsibilities

ASP.NET Core OpenID Connect owns code exchange, correlation and nonce Cookies, PKCE and signature
validation; Cookie authentication owns the encrypted opaque reference Cookie; JwtBearer owns
existing Bearer validation; antiforgery owns identity-bound CSRF; ServiceMantle owns security
response headers. Doctheca owns strict token shape and issuer/subject association, access-token
administrator authorization **before** sign-in, bounded pending/ticket/return stores, the Cookie
write boundary, atomic local revocation, and the SignaCore prepared-logout adapter.

There is one protocol implementation. `SignaCore.Client.AspNetCore 0.1.11-rc.5` is not used because
its authorization extension receives an ID-token principal during session-status reading, after
callback ticket creation. This cannot guarantee sign-in authorization from the validated access
token. Reevaluate replacement when its published public API supports that decision before
sign-in, strict dual-token association, equivalent revocation/CSRF/prepared-logout guarantees and
the compatibility contract below. Do not weaken these guarantees to substitute a package.

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
| `GET /admin/auth/oidc/callback` | Consumes pending once, checks browser correlation, callback issuer, nonce and both tokens, then signs in only for the validated access-token `role=admin`. Success redirects to the validated station-local return path. Failure redirects to `/?authError=signInFailed`, non-admin to `/?authError=notAdmin`, cancellation to `/?authError=cancelled`; protocol input is never echoed. |
| `GET /admin/auth/oidc/session` | 200 `{"success":true,"data":{"authenticated":true,"reason":"authenticated","username":"...","expiresAt":"..."}}` for a live session; absent/expired session is 401 `{"success":false,"message":"Re-authentication is required.","data":{"authenticated":false,"reason":"reauthenticationRequired"}}`. Username comes from the verified access token. |
| `GET /admin/auth/oidc/csrf` | 200 `{"success":true,"data":{"requestToken":"..."}}` and HttpOnly antiforgery Cookie for a live session; otherwise fixed re-authentication 401. |
| `POST /admin/auth/oidc/logout` | Live session requires CSRF (403 if missing/invalid, even with a Bearer header). Atomically revokes and clears local session first. A verified provider response returns 200 `{"success":true,"message":"Local session signed out.","data":{"reason":"logoutPrepared","logoutUrl":"https://<signacore-host>/oauth2/logout?logout_handle=..."}}`. All provider failures and already signed-out requests return 200 with `reason: "localSignedOut"` and `logoutUrl: null`. |
| `GET /admin/auth/oidc/logout/return?state=...` | Browser-bound, unexpired, single-use state redirects to `/?authResult=signedOut`; invalid/missing/duplicate/expired/replayed return redirects to `/?authError=logoutReturnFailed`. It never signs in and never echoes input. |

The caller navigates only to the returned `logoutUrl`. `logoutPrepared` means preparation
succeeded, not that SignaCore's browser session has already ended. `localSignedOut` claims only
local revocation. Upstream unavailability, timeout or request cancellation cannot resurrect the
local session. A concurrent/repeated exit can initiate at most one provider call; requests are
never retried, including ambiguous provider failures. The preparation form uses Confidential
client authentication, the atomically removed server ID-token snapshot, exact post-logout URI,
and random state. Its bounded JSON response must contain exactly one `logout_uri`: a relative or
same-origin canonical `/oauth2/logout?logout_handle=<43 base64url characters>` URI. External
origins, user information, fragments, alternate paths, extra or duplicate query fields and
malformed handles are rejected. The backchannel does not follow redirects.

Return URLs accept station-local absolute paths, including SPA hash routes such as `/#docs`.
External origins, encoded alternate origins, backslashes, control characters, dot segments and
`/admin/auth` paths are rejected. Login code/state never enter the SPA or business return URL.

## Session, token and CSRF guarantees

Both tokens have independently verified signatures, issuer, application audience, type and
lifetime. ID tokens additionally require exactly one correctly typed issuer/subject, RS256,
`typ=JWT`, `kid`, sane `iat/exp` and the pending nonce. Access tokens require RS256 and
`typ=at+jwt`; both tokens' nonempty string `iss/sub` claims must each occur exactly once and
match ordinally. Only verified access-token administrator role decides sign-in; an ID-token
administrator claim cannot authorize a non-admin access token. Rejected/cancelled callbacks
create no new ticket or session Cookie and do not renew or replace an existing session.

`docthecaAdminSession` is HttpOnly, Secure and SameSite=Lax; it contains an encrypted opaque
ticket reference, never a token. Its absolute expiry is capped by the verified access-token
expiry and the eight-hour store limit. Expired/unknown Cookie requests to protected `/admin/*`
return fixed re-authentication 401. There is no automatic refresh or write replay.

For non-safe Cookie requests to protected admin routes (outside the legacy `/admin/auth` area),
include both the `docthecaAdminCsrf` Cookie and `X-CSRF-TOKEN` from `data.requestToken`.
Missing/invalid credentials return fixed 403. A Bearer request remains on the existing path,
even alongside an active or expired session Cookie, only after the Authorization credential
actually validates; it cannot borrow the Cookie's administrator role. A forged header cannot
bypass the Cookie CSRF check. Hosted logout always uses its session CSRF boundary. Old `docthecaAccessToken` and `docthecaRefreshToken` Cookies are ignored.

Pending transactions and logout returns expire after five minutes. Each in-memory store has
capacity 4096 and a one-minute sweep. Logout return state is bound to a separate HttpOnly,
Secure, SameSite=Lax Cookie scoped to the return path. Session snapshots are serialized;
revocation removes and acquires the snapshot in one locked operation, and renewal cannot
restore a removed ticket. A stale session Cookie copy returns 401 after revocation.

Tokens, client secrets and PKCE verifiers stay in memory/backchannel only. Code/state occur only
in the required trusted authorization/callback or logout-return protocol, never business
navigation, JSON errors or logs. Framework OIDC protocol logging is suppressed through the
safe handler; hosting diagnostics are disabled while this feature is enabled because they log
raw callback query strings before application middleware can redact them. Failure results
never include upstream payloads or exception details.

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
Logout is single-flight, clears displayed session data, and navigates to a server-validated
logout URL. A local-only response warns that the SignaCore session may remain active; a failed
request displays a fixed failure without claiming server revocation.

Run `cd frontend && npm test` for transport-timing/state tests and `npm run test:e2e` for real
Chromium tests against the compiled SPA, Kestrel, PostgreSQL 16 and the fake SignaCore public
pages/backchannel. The latter also runs in the full .NET suite and builds the current SPA.
Install the matching browser after building the test project with
`pwsh src/Tests/Doctheca.Tests/bin/Release/net10.0/playwright.ps1 install chromium`
(`--with-deps` on Linux). No browser or Playwright package is included in the runtime image.
Test contexts are isolated and do not record trace/HAR/storage state or protocol values.
