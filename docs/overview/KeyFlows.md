# KeyFlows — Key Business Flows

## 0. Identity Administrator Login and Cookie Session

```text
Admin Browser        Doctheca                 Identity
     │ POST /admin/auth/login                       │
     ├────────────────►│                            │
     │                 │ POST /api/auth/token       │
     │                 ├───────────────────────────►│
     │                 │ access + refresh token     │
     │                 │◄───────────────────────────┤
     │                 │ OIDC/JWKS validation + role=admin│
     │ HttpOnly Cookie │                            │
     │◄────────────────┤                            │
     │ GET/POST /admin/* (Cookie sent automatically)│
     ├────────────────►│                            │
```

When the Access Token expires, the browser calls `/admin/auth/refresh`; Doctheca uses the HttpOnly Refresh Cookie to obtain and validate a new token pair from Identity. On logout, the Refresh Token is revoked best-effort and cookies are cleared.

---

## 1. File Upload → StructaDoc Parsing → Searchable

```
  Admin UI          Doctheca            StructaDoc           StructaDocParseWorker    OpenSearch
     │                    │                    │                       │                   │
     │ POST /admin/document-files/upload       │                       │                   │
     │───────────────────►│                    │                       │                   │
     │                    │ POST /api/v1/documents (multipart "file")   │                   │
     │                    │───────────────────►│                       │                   │
     │                    │ 201 documentId     │                       │                   │
     │                    │◄───────────────────│                       │                   │
     │                    │ write document_files (structadoc_document_id)│                  │
     │   200 + file_id    │                    │                       │                   │
     │◄───────────────────│                    │                       │                   │
     │                    │                    │                       │                   │
     │ POST /admin/document-files/{id}/parse   │                       │                   │
     │───────────────────►│                    │                       │                   │
     │                    │ write document_parses (status=pending)      │                   │
     │                    │                    │   poll pending/parsing tasks (5 seconds)    │
     │                    │                    │◄──────────────────────│                   │
     │                    │                    │ POST parse-runs       │                   │
     │                    │                    │  (Idempotency-Key=parseId)                │
     │                    │                    │ 201 parseRunId → status=parsing            │
     │                    │                    │ (internally runs MinerU parsing, Office conversion, large PDF chunking)
     │                    │                    │ GET parse-runs/{id} poll until terminal state│
     │                    │                    │◄──────────────────────│                   │
     │                    │                    │ succeeded → pull Blocks/Markdown/Assets    │
     │                    │                    │ write document_parse_blocks / _images      │
     │                    │                    │ update status=parsed   │                   │
     │                    │                    │                       │ IndexParseBlocksAsync()
     │                    │                    │                       │──────────────────►│
```

**Trigger**: the user uploads a file via the Admin UI and triggers parsing
**Participating services**: Admin UI → Doctheca → StructaDoc (primary owner of originals and parse artifacts) → PostgreSQL; StructaDocParseWorker → OpenSearch
**Data flow**: file bytes forwarded directly to StructaDoc → Parse Run parses asynchronously → Blocks/Markdown/Assets synchronized into the 3 local tables → search engine indexing
**Legacy compatibility**: files uploaded before the migration (with only an OSS path) are lazily uploaded to StructaDoc by the Worker on the first parse trigger, with the reference back-filled; parse records from before the migration remain read-only

---

## 2. Exact Search (ExactSearch)

```
  Admin UI / HTTP Client  Doctheca           OpenSearch
     │                    │                       │
     │ GET /admin/documents/search?query=... │                       │
     │───────────────────►│                       │
     │                    │ SearchDocumentAsync(query, filter) │
     │                    │──────────────────────────────────────────────►
     │                    │    results            │
     │                    │◄──────────────────────│
     │                    │                       │
     │    results         │  (returns empty results and logs LogWarning when OpenSearch is unavailable) │
     │◄───────────────────│                       │
```

**Trigger**: HTTP GET `/admin/documents/search`
**Degradation**: returns empty results and logs LogWarning when OpenSearch is unavailable
