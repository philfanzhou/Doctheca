# 01-FEATURE — Doctheca Admin Authentication

The administration console uses SignaCore hosted login with a Confidential authorization-code
client and S256 PKCE. Doctheca validates both tokens and authorizes the verified access-token
`role=admin` before creating an opaque HttpOnly server-session Cookie. Tokens stay server-side.
Ordinary users cannot create an administrator session. Bearer API callers retain their issuer,
audience, discovery/JWKS and administrator-role checks.

The SPA and API share one image and origin. Static files and health probes remain anonymous.
Missing login configuration permits service startup but all hosted entrypoints return fixed 503.
Expired sessions require explicit login; requests are never refreshed or replayed.

The password proxy, refresh/revoke client and browser token Cookies are retired. Old POST
login/refresh/logout requests return fixed 410 JSON without binding or forwarding their body.
See [the complete hosted contract](07-HostedLogin.md), [HTTP specification](02-SPEC.md) and
[breaking changes and upgrade](../../development/HostedLoginUpgrade.md).
