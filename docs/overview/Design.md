# Design — Service-Level Architecture

## Layered Architecture

```text
Host
  Program.cs: DI / Kestrel / Authentication / Authorization / static SPA
  Identity authentication and administrator authorization
        │
Service
  AdminAuthEndpoints / AdminOidcEndpoints / server session and CSRF
  Document*Endpoints
  StructaDoc client+worker / OpenSearch / analysis
        │
Domain
  document and parse services, repositories, models
        │
Database
  EF Core repositories + EF Core migrations (DocthecaMigrationExecutor)
```

Tech stack: .NET, ASP.NET Core Minimal APIs, EF Core 10/Npgsql, Mapster, OpenSearch.Net, Vue 3.5/TypeScript/Vite/Element Plus.

## Access Control Architecture

### Browser Administrators

- SignaCore hosted login handles passwords; Doctheca uses a Confidential code client and PKCE.
- Both tokens are validated and the verified access-token `role=admin` authorizes sign-in.
- The browser receives an opaque HttpOnly server-session Cookie, never tokens.
- Cookie-authenticated writes require identity-bound CSRF; expiry requires reauthentication.
- Header Bearer clients retain strict issuer/audience/signature/expiry/admin-role validation.
- Old password POST routes return 410 and old browser token Cookies are ignored.
- Static files and the SPA fallback remain anonymous.

## Key Decisions

| Decision | Rationale |
|------|------|
| Restore application-layer authentication and enforce the admin role | Network isolation cannot stop any reachable caller from reading or writing admin data |
| HttpOnly cookies instead of localStorage | Frontend JavaScript never touches tokens, reducing XSS token-theft risk |
| Absolute server sessions and local-first prepared logout | No refresh/replay; revocation cannot be undone by provider failures |
| Validate both tokens before sign-in | Strict token binding and administrator authorization from the access token |
| Static SPA anonymous | Unauthenticated users must be able to load the login page first |
| Do not reserve unused Quaestura interfaces | No callers exist today; avoid maintaining interfaces, credentials, and configuration with no consumers |
| Do not automatically drop legacy import tables | Data deletion must be executed as a separate, explicit, reviewable operational change |
| Keep a single port and single image | Continues the self-contained frontend, static hosting, and existing Docker deployment |

## Key Documents

- [Administrator authentication spec](../modules/AdminAuthentication/02-SPEC.md)
- [Deployment notes](../development/Deployment.md)
- [Verification notes](../development/verification.md)
