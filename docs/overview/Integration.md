# Integration — Integration Matrix

## Integration Overview Table

| Target | Protocol | Direction | Purpose | Security boundary |
|------|------|------|------|----------|
| Admin UI | HTTP same-origin | Inbound | Document management and search | Identity `role=admin` HttpOnly Cookie/JWT |
| SignaCore | HTTP/OIDC | Outbound | Login, refresh, revocation, JWKS | Authority from Consul |
| PostgreSQL | TCP | Outbound | Document/parse data CRUD | Service-private database |
| StructaDoc | HTTP | Outbound | Document upload, Parse Runs, Blocks/Markdown/Assets sync, image proxy, deletion (ADR-0009) | `Authorization: ApiKey` scoped key, BaseUrl from Consul/environment variables |
| MinIO/SeaweedFS | S3 | Outbound | Legacy files and parse images (read-only compatibility and delete cleanup) | Shared Consul configuration |
| OpenSearch | HTTP | Outbound | Block indexing and queries | Shared Consul configuration |
| LLM (OpenAI-compatible, optional) | HTTP | Outbound | Metadata analysis | Private ApiKey |

There is currently no Quaestura → Doctheca integration. When a real calling need
appears later, the data contract, authentication method, and deployment configuration must be
defined independently, without reusing the administrator cookie and without reserving unused interfaces.

## Admin API

Except for `/admin/auth/login`, `/admin/auth/refresh`, and `/admin/auth/logout`, all
`/admin/*` endpoints require the `DocthecaAdmin` policy. For the full authentication contract see
[AdminAuthentication](../modules/AdminAuthentication/01-FEATURE.md).

Main admin endpoints:

| Method | Path | Description |
|------|------|------|
| POST | `/admin/document-files/upload` | Upload a document |
| GET | `/admin/document-files` | Document list |
| GET | `/admin/document-files/{id}` | Document details |
| PUT | `/admin/document-files/{id}/metadata` | Update metadata |
| POST | `/admin/document-files/{id}/parse` | Trigger parsing |
| DELETE | `/admin/document-files/{id}` | Delete a document |
| GET/DELETE | `/admin/document-parses...` | Parse list and deletion |
| GET | `/admin/document-parses/{parseId}/images/{imageId}/content` | StructaDoc parse image proxy (browser authenticated via admin cookie) |
| GET | `/admin/documents/search` | Search |
| GET | `/admin/document-files|document-parses/.../export/*` | Export |

## Anonymous Entry Points

- `/`, static resources, and SPA fallback
- `/health`
- `/admin/auth/login`
- `/admin/auth/refresh`
- `/admin/auth/logout`

## Failure Semantics

| Category | Behavior |
|------|------|
| Unauthenticated admin request | 401 |
| Authenticated but not an administrator | 403 |
| Identity unavailable | Login/refresh returns a controlled 502/401 without leaking internal exceptions |
| OpenSearch write failure | Does not block the main parsing flow; logs a Warning |
| OpenSearch query failure | Returns empty results and logs a Warning |
