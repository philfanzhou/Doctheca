# Hosted Login Upgrade — Breaking Changes

This release retires the password proxy after the hosted API and SPA are available. Deploy
both parts in the same image. This change does not alter PostgreSQL, migrations, legacy object
storage or HTTP port 5012. It introduces no dependency or automatic refresh.

## Breaking changes

- `POST /admin/auth/login`, `/refresh` and `/logout` return fixed 410 JSON:
  `{"success":false,"message":"Password authentication has been retired. Use hosted sign-in."}`.
  They never bind/forward passwords or bodies, write token Cookies or revoke a hosted session.
- `docthecaAccessToken` and `docthecaRefreshToken` no longer authenticate, refresh or revoke.
  Clients should remove cached old Cookies; supplying them alongside a hosted session or Bearer
  credential does not change identity selection. Authorization header clients keep their existing
  strict trust and administrator-role checks.
- `AdminOidc:Enabled` is ignored. Complete configuration always activates hosted login, including
  deployments that still set it to false. `Authentication:CookieSecure` and
  `DOCTHECA_COOKIE_SECURE` are removed. Secure hosted Cookies follow validated TLS/environment rules.
- Missing any required hosted key allows service startup but all six hosted endpoints return
  `503 {"success":false,"message":"Hosted sign-in is not configured."}` without provider calls or
  pending/ticket state. Startup logs contain only `DOCTHECA_OIDC_NOT_CONFIGURED` and key names.
  Complete nonempty invalid URI/skew configuration still fails startup. Missing Bearer trust
  refuses credentials with 401; it never trusts a substitute issuer or browser token Cookie.

## Upgrade

1. Register a Confidential SignaCore application with PerApplication audience (`aud=appId`),
   `openid profile`, S256 PKCE and refresh disabled. Preserve the independent existing API Bearer
   trust/audience settings; plan any caller audience migration separately.
2. Register exact canonical HTTPS `/admin/auth/oidc/callback` and
   `/admin/auth/oidc/logout/return` URIs on the same origin; inject them through
   `AdminOidc:RedirectUri` and `AdminOidc:PostLogoutRedirectUri`.
3. Inject `IdentityService:Authority`, `AppId` and `AppSecret` at runtime. AppId/AppSecret remain
   required for confidential code exchange and prepared logout. Never expose them to browser code.
4. Remove retired switches/variables and callers of legacy POST endpoints. Use the hosted
   session/CSRF/logout routes specified in [07-HostedLogin.md](../modules/AdminAuthentication/07-HostedLogin.md).
5. Roll out a matched API/SPA image, restart after configuration recovery and authenticate again.
   Only one instance/process is supported: restart loses in-memory sessions/pending/returns.
   Verify anonymous health/static serving, hosted login, CSRF rejection and logout before use.

## Rollback

Restore the previous matching API/SPA image and all runtime configuration required by that image,
including its old login enablement/Cookie security settings when applicable. Users must sign in
again. No migration or rollback SQL is required. Rolling back restores the old password proxy,
refresh/revoke calls and token-Cookie contracts; disabling a removed switch in the current image
cannot perform that rollback.

## Package adoption (issue #70)

The self-written OIDC protocol slice was replaced by the official `SignaCore.Client.AspNetCore`
client (0.1.13). Routes, the session Cookie name, the CSRF header name, configuration keys and
the admin policy's role assertion are unchanged; wire-level presentation differences are:

- Token redemption and logout preparation authenticate with HTTP Basic instead of a form-carried
  `client_secret`; the form carries no credentials either way.
- `GET /admin/auth/oidc/csrf` answers `{"token":"..."}` instead of
  `{"success":true,"data":{"requestToken":"..."}}`; the SPA reads `token`.
- `POST /admin/auth/oidc/logout` redirects (302) to the verified logout URI on success and answers
  `200 {"outcome":"local_only"}` on every failure instead of returning `logoutUrl` JSON. A missing
  or invalid antiforgery token answers `400 {"outcome":"csrf_rejected"}` instead of 403 JSON.
- `GET /admin/auth/oidc/logout/return` failure answers the package's fixed 400 page instead of
  redirecting to `/?authError=logoutReturnFailed`; an otherwise valid return tolerates unknown
  extra query members.
- A cancelled sign-in and a gate denial share the merged `authError=notAdmin` outcome; a
  token-endpoint transport failure now surfaces as `authError=identityUnavailable`.
- The ID token follows standard OIDC validation semantics (multi-valued `aud` containing the
  client id is accepted, `iat` is not demanded, the signature is verified against the published
  JWKS), and the access token must carry `nbf` per the SignaCore contract.
- The logout-return correlation Cookie is named after the session Cookie
  (`docthecaAdminSession-logout-return`) instead of `docthecaLogoutBinding`.

Rollback to the previous image restores the self-written slice with its old response shapes; the
session Cookie name is unchanged but tickets are not compatible across the switch, so browsers
re-authenticate after either direction of the change. No SQL or configuration change is required.
