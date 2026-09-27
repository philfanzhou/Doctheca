# 03-DESIGN — Doctheca Admin Authentication Design

## Component boundaries

```text
Browser
  ├─ anonymous SPA/login page
  └─ HttpOnly cookies
          │
          ▼
Doctheca Host
  ├─ AdminAuthEndpoints
  ├─ IdentityAuthenticationService
  ├─ IdentityTokenValidator
  ├─ JwtBearer + DocthecaAdmin policy
  └─ existing /admin domain endpoints
          │
          ▼
SignaCore
  ├─ POST /api/auth/token
  ├─ POST /api/auth/revoke
  └─ OIDC discovery + JWKS
```

### `AdminAuthEndpoints`

Responsible only for the HTTP contract, Cookie writing/deletion, and result mapping; does not directly implement downstream protocols or Token cryptographic validation.

### `IdentityAuthenticationService`

Wraps Identity's password grant, refresh token grant, and revoke calls. Request DTOs use explicit camelCase JSON names; token requests carry Doctheca's own AppId/AppSecret. The service never logs request passwords, app secrets, or response Tokens.

### `IdentityTokenValidator`

Validates the Identity Access Token using the Authority's OIDC metadata/JWKS and the same TokenValidationParameters as JwtBearer, returning a validated `ClaimsPrincipal`. Login and refresh establish Cookies only when the principal contains `role=admin`.

### JwtBearer

Reads the Token from `Authorization: Bearer`; if the Header is missing, reads it from the `docthecaAccessToken` Cookie. The `DocthecaAdmin` policy requires an authenticated user with the `admin` role.

## Middleware and endpoint order

```text
CorrelationId
DefaultFiles / StaticFiles
Authentication
Authorization
Anonymous auth endpoints
Admin route groups -> DocthecaAdmin policy
Anonymous /health
Anonymous SPA fallback
```

Static files, `/health`, login/refresh/logout, and the SPA fallback are not intercepted by the admin policy. `/admin/auth/session` alone requires `DocthecaAdmin`.

## Login data flow

```text
Browser -> Doctheca: username/password
Doctheca -> Identity: POST /api/auth/token + Doctheca App credentials
Identity -> Doctheca: accessToken + refreshToken
Doctheca -> Identity discovery/JWKS: validate accessToken
Doctheca: require role=admin
Doctheca -> Browser: HttpOnly access/refresh cookies + non-sensitive session summary
```

An ordinary account's Identity Token, even with a valid signature, is rejected at the role-check stage, and no Cookie is set.

## Refresh and logout

- Refresh Tokens are only sent to `/admin/auth/refresh` and `/admin/auth/logout`.
- Identity revokes the old Refresh Token on every refresh and returns a new Token pair; Doctheca overwrites the old Cookies with the new ones.
- The new Access Token returned by a refresh must still pass full cryptographic validation and the admin role check.
- On logout, the Identity revoke is best-effort; local Cookie cleanup is mandatory.

## Quaestura boundary

> Quaestura is the former QuestionBank service, migrated out on 2026-09-25 as the standalone repository [philfanzhou/Quaestura](https://github.com/philfanzhou/Quaestura) (ADR-0011). The boundary below is the same before and after the migration: Doctheca never provided its dedicated interfaces.

The current repository has no Quaestura → Doctheca callers, so Doctheca provides no
Quaestura-dedicated HTTP interfaces, service authentication policies, or service key configuration. When a real calling
need appears later, the data contract and authentication method will be redesigned; no unused interfaces are reserved.

The former `POST /admin/document-parses/{parseId}/import-status`, the `document_parse_imports` runtime model, and the list filtering have all been removed. Legacy tables in existing databases are not automatically dropped at startup.

## Deployment compatibility

- The frontend is still built in the first stage of the Doctheca Docker image and copied into the Host `wwwroot`.
- The Host still listens on the single HTTP port 5012.
- Identity Authority, Audience, and metadata HTTPS requirements reuse the shared Consul
  `IdentityService` configuration; `start.sh` does not inject them again.
- Doctheca AppId/AppSecret are deployment secrets, injected by `start.sh`, never placed in shared Consul.
- Cookie security configuration remains local Doctheca configuration; no separate frontend container or reverse proxy is added.
