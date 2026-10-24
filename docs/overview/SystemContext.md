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
  │                    hosted code exchange/prepared logout + OIDC/JWKS
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
| Internal business services | `/internal/v1/structure-definitions`, `/internal/v1/documents`, `/internal/v1/documents/{documentFileId}/structure`, `/internal/v1/structure-nodes/{nodeId}/content`, `/internal/v1/structure-nodes/{nodeId}/images/{imageId}/content`, `/internal/v1/search`, `/internal/v1/structure-nodes/{nodeId}/audio` and its expiring content links | Anonymous; deployer restricts network reachability | Read-only definition and marked-document catalogs, current document trees, revision-checked content pages, owned image bytes and subtree search |

Regular Identity users have no administrator role and cannot log in or call the admin API.
The implemented internal GET routes are always mapped, using downstream field allow-lists.
The obsolete `InternalApi:Enabled` setting is ignored; it grants no protection.
All other `/internal` paths and unsupported methods return safe 404 instead of static files or
SPA HTML. This introduces no caller authentication or document-level authorization: deployment
must make `/internal/*` reachable only from the internal network. It grants no admin permission.
The tree selects the latest parsed parse with saved nodes (timestamp descending, nulls last,
UUID descending), and reads all tree data in one repeatable-read snapshot. Node content
resolves ownership in its own snapshot; SHA-256 revisions reject changed tree/block membership
between pages with 409. Owned images are proxied through StructaDoc or legacy OSS with safe
errors and stream disposal, without storage links. Search validates current-node ownership and strict bounded
cursors, then applies the existing indexed subtree filter; the derived index remains eventually consistent.
Direct audio uses process-local expiring same-origin links (#101), internal S3 and bounded seekable scratch buffering. Membership and expiry are rechecked after buffering before admission; no storage identity is exposed. Restart invalidates links. No public S3 address is needed for this proxy; admin signing is unchanged. Provide scratch capacity and omit token-bearing URLs from application/proxy logs. A post-admission removal may race an existing stream; every new request rechecks.
See [Internal API](../Integration/InternalApi.md) for pagination and deployment responsibilities.

## Downstream Dependencies

| Dependency | Purpose |
|------|------|
| SignaCore | Hosted sign-in, code exchange, prepared logout, OIDC discovery/JWKS |
| PostgreSQL | Files, parse records, and locally synchronized blocks/images |
| StructaDoc (external repository, :8080) | Primary-ownership storage of document originals and parse artifacts; Parse Run execution (MinerU provider + LibreOffice conversion fallback); Blocks/Markdown/Assets APIs |
| MinIO / SeaweedFS / LocalFile | Read-only compatibility and delete cleanup for legacy (pre-migration uploaded) files and parse images; structure audio under the `audio/` prefix (S3 only, optional) |
| OpenSearch | Full-text indexing of parse blocks |
| OpenAI-compatible LLM (optional) | Metadata analysis |

## Service Boundaries

- Doctheca exclusively writes its own document and parse record tables; originals and parse artifacts of new documents are stored under StructaDoc's primary ownership, and Doctheca only keeps documentId/parseRunId references and local blocks/images synchronized copies (ADR-0009).
- Doctheca does not connect directly to StructaDoc's database or object storage; everything goes through its versioned API.
- Doctheca does not store Quaestura import status or question IDs.
- Identity is responsible for user credential validation and JWT issuance; Doctheca only validates and consumes the administrator identity.
