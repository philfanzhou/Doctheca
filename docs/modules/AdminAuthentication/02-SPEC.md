# 02-SPEC — Doctheca Admin Authentication Specification

## HTTP contract

### POST `/admin/auth/login`

Anonymous endpoint. Request:

```json
{
  "username": "string",
  "password": "string"
}
```

On success returns 200 and sets the Access/Refresh HttpOnly Cookies:

```json
{
  "success": true,
  "data": {
    "username": "string",
    "roles": ["admin"],
    "expiresAt": 1719900000
  }
}
```

Failure semantics:

| Scenario | HTTP | Response message |
|------|------|----------|
| Username or password is empty | 400 | `Username and password are required.` |
| Identity rejects the credentials | 401 | `Invalid username or password.` |
| SignaCore rejects the Doctheca App credentials | 502 | `Identity service is unavailable.` |
| Identity login succeeds but the Token has no `role=admin` | 403 | `Administrator access is required.` |
| Identity is unavailable or responds invalidly | 502 | `Identity service is unavailable.` |
| Identity Token fails cryptographic validation | 502 | `Identity service returned an invalid token.` |

The request body Doctheca sends to Identity is fixed as:

```json
{
  "grantType": "password",
  "username": "<trimmed username>",
  "password": "<password>"
}
```

The above Doctheca → SignaCore request also carries Doctheca's own `X-Admin-AppId` /
`X-Admin-AppSecret`; the credentials come from deployment secrets, do not reuse any Portal App, and are never written to shared Consul KV.

### POST `/admin/auth/refresh`

Anonymous endpoint, but requires the request to carry a valid Refresh Cookie. Doctheca calls Identity:

```json
{
  "grantType": "refresh_token",
  "refreshToken": "<HttpOnly cookie value>"
}
```

After a successful refresh, the new Access Token's signature and `role=admin` must be re-validated, then both Cookies are rotated. Invalid, expired, revoked, or non-admin Tokens return 401; Identity unavailability returns 502; both failure kinds clear both Cookies. The refresh grant and the password grant use the same set of Doctheca App credentials.

### POST `/admin/auth/logout`

May be called without a valid Access Token, so expired sessions can still log out. If a Refresh Cookie exists, Doctheca calls Identity `POST /api/auth/revoke` on a best-effort basis; regardless of Identity availability, both Cookies are ultimately cleared and 200 is returned.

### GET `/admin/auth/session`

Requires the administrator policy. Returns the user ID, name, roles, and expiry from the current JWT; does not return the Token:

```json
{
  "success": true,
  "data": {
    "userId": "uuid",
    "username": "string",
    "roles": ["admin"],
    "expiresAt": 1719900000
  }
}
```

## Authorization matrix

| Caller | Auth endpoints | Other `/admin/*` |
|--------|----------|-----------------|
| Unauthenticated browser | login/logout allowed; session 401 | 401 |
| Ordinary Identity JWT | login 403; session 403 | 403 |
| `role=admin` Identity JWT/Cookie | Allowed | Allowed |

## JWT validation

| Item | Value/source |
|------|---------|
| Authority | `IdentityService:Authority` |
| Issuer | `IdentityService:Issuer`, must be configured explicitly; old Issuers go only in `AdditionalValidIssuers` |
| Audience | `IdentityService:Audience`, must be configured explicitly |
| Signature | Authority OIDC discovery + JWKS, RS256 |
| Expiry | Must be validated; ClockSkew comes from `IdentityService:ClockSkewSeconds` |
| Admin role | `role=admin`, case-insensitive |

The explicit Token validation during login and refresh and the JwtBearer validation in the request pipeline must use the same set of parameters.

## Cookies

| Cookie | Path | Attributes | Purpose |
|--------|------|------|------|
| `docthecaAccessToken` | `/admin` | HttpOnly, SameSite=Strict, Secure controlled by configuration | Admin API identity |
| `docthecaRefreshToken` | `/admin/auth` | HttpOnly, SameSite=Strict, Secure controlled by configuration | Refresh and logout |

Production HTTPS deployments must set `Authentication:CookieSecure=true`. The current HTTP-only intranet deployment may explicitly set it to `false`, but that configuration must not be interpreted as safe for public-network deployment.

## Frontend behavior

1. On startup the application calls `/admin/auth/session`.
2. On 200 it loads the admin UI; on 401/403 it shows the login page.
3. After a successful login it re-requests the session and does not read Tokens.
4. When the shared Axios client hits a 401 on a non-auth endpoint, it calls refresh only once, and concurrent requests share the same refresh Promise.
5. After a successful refresh it retries the original request; on refresh failure it clears the frontend session state and shows the login page.
6. 403 is not retried automatically; a no-permission message is shown.
7. After logout completes, the login page is shown immediately.

## Security constraints

- Never hardcode administrator usernames or passwords in the frontend.
- Never write passwords, Tokens, or Cookies to logs, exception messages, or API responses.
- Login failure logs record only generic reasons and normalized non-sensitive correlation information.
- Admin APIs do not enable cross-origin credentials; the frontend and backend stay same-origin.
- Static files and the SPA fallback must be mapped outside the authorization policy.
