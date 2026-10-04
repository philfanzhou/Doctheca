# 07-HostedLogin — Hosted Login, Server Sessions and Prepared Logout

Doctheca delegates administrator authentication to the SignaCore hosted page through a
Confidential authorization-code client with S256 PKCE. Passwords never pass through this flow;
access and ID tokens remain in server memory. The feature is **disabled by default**. While
disabled, all six `/admin/auth/oidc` endpoints return the fixed 503 result below, and legacy
password login, refresh, logout, token Cookies and Bearer clients retain their existing behavior.
The frontend switch and legacy-path removal are separate changes (#49 and #50).

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
| `AdminOidc:Enabled` | no; default `false` | Enable the new protocol routes. |
| `AdminOidc:RedirectUri` | when enabled | Exact callback URI ending in `/admin/auth/oidc/callback`. |
| `AdminOidc:PostLogoutRedirectUri` | when enabled | Exact post-logout URI ending in `/admin/auth/oidc/logout/return`, on the same origin as the callback. |
| `IdentityService:Authority` | when enabled | Trusted SignaCore issuer/base URI. |
| `IdentityService:AppId` / `AppSecret` | when enabled | Confidential client ID and server-only secret. |
| `IdentityService:ClockSkewSeconds` | no; default 30 | Token skew, 0–300 seconds. |

Both registered callback URIs must be canonical absolute HTTPS URIs, at most 500 ASCII characters,
without query, fragment, user information or wildcard. Development/Testing alone allow numeric
loopback HTTP (`127.0.0.1` or `[::1]`); `localhost` is rejected. Missing or invalid enabled
configuration fails startup with the key name only. Inject credentials through environment
variables or Consul KV. Configuration objects hide their values when formatted.

Before enabling, register SignaCore in this order:

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
redirects. Disabled requests return
`503 {"success":false,"message":"Hosted sign-in is not enabled."}`.

| Route | Enabled result |
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

Return URLs accept station-local absolute paths, including SPA hash routes such as `/#/documents`.
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
bypass the Cookie CSRF check. Hosted logout always uses its session CSRF boundary. The legacy
`docthecaAccessToken` Cookie remains distinct during migration.

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

Deploy this backend with the switch disabled to preserve the existing frontend contract.
Complete exact application registrations and credential injection before enabling for tests.
This completed backend requires the new post-logout configuration whenever enabled. A deployment
using the earlier login-only PR build must add it before upgrading. Roll back this stage by
setting `AdminOidc:Enabled=false` or restoring the previous matching image/configuration; users
must authenticate again. #49 will coordinate frontend consumption and enablement; #50 will
separately retire the old password/token-Cookie flow and its rollback boundary.

Related: [legacy authentication](./01-FEATURE.md).
