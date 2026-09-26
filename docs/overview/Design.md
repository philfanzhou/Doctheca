# Design — Service-Level Architecture

## Layered Architecture

```text
Host
  Program.cs: DI / Kestrel / Authentication / Authorization / static SPA
  Identity authentication and administrator authorization
        │
Service
  AdminAuthEndpoints / IdentityAuthenticationService / IdentityTokenValidator
  Document*Endpoints
  StructaDoc client+worker / OpenSearch / analysis
        │
Domain
  document and parse services, repositories, models
        │
Database
  EF Core repositories + SQL DatabaseInitializer
```

Tech stack: .NET 10, ASP.NET Core Minimal APIs, EF Core 10/Npgsql, Mapster, OpenSearch.Net, Vue 3.5/TypeScript/Vite/Element Plus.

## Access Control Architecture

### Browser Administrators

- Identity password grant validates the username and password.
- The Identity bootstrap administrator automatically receives `role=admin` on password login.
- Doctheca fully validates the Access Token via Identity OIDC/JWKS at login and refresh.
- Access/Refresh Tokens are stored only in HttpOnly, SameSite=Strict cookies.
- The `DocthecaAdmin` policy protects the admin route group, requiring a valid JWT and `role=admin`.
- Static files and the SPA fallback remain anonymous to avoid a login deadlock.

## Key Decisions

| Decision | Rationale |
|------|------|
| Restore application-layer authentication and enforce the admin role | Network isolation cannot stop any reachable caller from reading or writing admin data |
| HttpOnly cookies instead of localStorage | Frontend JavaScript never touches tokens, reducing XSS token-theft risk |
| Refresh token rotation and revocation on logout | Keeps the session experience and closes refresh capability after sign-out |
| Re-validate the JWT at login | Do not trust downstream JSON roles alone; ensure the signature and standard claims are valid |
| Static SPA anonymous | Unauthenticated users must be able to load the login page first |
| Do not reserve unused Quaestura interfaces | No callers exist today; avoid maintaining interfaces, credentials, and configuration with no consumers |
| Do not automatically drop legacy import tables | Data deletion must be executed as a separate, explicit, reviewable operational change |
| Keep a single port and single image | Continues the self-contained frontend, static hosting, and existing Docker deployment |

## Key Documents

- [Administrator authentication spec](../modules/AdminAuthentication/02-SPEC.md)
- [Deployment notes](../development/Deployment.md)
- [Verification notes](../development/verification.md)
