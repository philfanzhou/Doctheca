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
