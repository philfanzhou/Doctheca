# SystemContext — Service Positioning

## Service Positioning

`doctheca` is the document retrieval microservice of the Ruoyu.Study platform, responsible for
storing, parsing, managing, and full-text exact searching of educational documents. The service
hosts the Vue admin frontend, the admin API, and health checks together on HTTP 5012.

## Call Relationships

```text
Admin Browser
  ├─ anonymous SPA/login
  └─ Identity admin HttpOnly session
          │
          ▼
Doctheca :5012 ─────► SignaCore :5002
  │                    password/refresh/revoke + OIDC/JWKS
  ├────► PostgreSQL
  ├────► SeaweedFS/MinIO (legacy objects only)
  ├────► OpenSearch
  ├────► StructaDoc :8080 (document upload and parsing, ADR-0009)
  └────► optional LLM (metadata analysis)
```

## Upstream Callers

| Caller | Entry | Identity | Purpose |
|--------|------|------|------|
| Admin UI | `/`, `/admin/*` | Identity `role=admin` Cookie/JWT | File, parse, metadata, export, and search management |

Regular Identity users have no administrator role and cannot log in or call the admin API. Currently Doctheca
provides no dedicated HTTP interface for Quaestura; if a real caller appears later, the data
contract and service authentication must be redesigned.

## Downstream Dependencies

| Dependency | Purpose |
|------|------|
| SignaCore | Password login, token refresh/revocation, OIDC discovery/JWKS |
| PostgreSQL | Files, parse records, and locally synchronized blocks/images |
| StructaDoc (external repository, :8080) | Primary-ownership storage of document originals and parse artifacts; Parse Run execution (MinerU provider + LibreOffice conversion fallback); Blocks/Markdown/Assets APIs |
| MinIO / SeaweedFS / LocalFile | Read-only compatibility and delete cleanup for legacy (pre-migration uploaded) files and parse images only |
| OpenSearch | Full-text indexing of parse blocks |
| OpenAI-compatible LLM (optional) | Metadata analysis |

## Service Boundaries

- Doctheca exclusively writes its own document and parse record tables; originals and parse artifacts of new documents are stored under StructaDoc's primary ownership, and Doctheca only keeps documentId/parseRunId references and local blocks/images synchronized copies (ADR-0009).
- Doctheca does not connect directly to StructaDoc's database or object storage; everything goes through its versioned API.
- Doctheca does not store Quaestura import status or question IDs.
- Identity is responsible for user credential validation and JWT issuance; Doctheca only validates and consumes the administrator identity.
