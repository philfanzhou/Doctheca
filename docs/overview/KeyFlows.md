# KeyFlows — Key Business Flows

## 0. Hosted Administrator Login and Server Session

1. The browser opens `/admin/auth/oidc/start` with a validated station-local return URL.
2. Doctheca redirects to the trusted SignaCore hosted page with S256 PKCE, state and nonce.
3. The callback consumes pending state once; code exchange stays server-side. Both tokens are
   verified and strictly bound; the validated access-token administrator role authorizes sign-in.
4. The browser receives only an opaque HttpOnly server-session Cookie and returns to its local
   route. Cookie-authenticated writes include identity-bound CSRF credentials.
5. Expiry requires explicit reauthentication without refresh or write replay. Logout revokes
   the local ticket first, then prepares provider logout and validates its navigation result.

Old POST login/refresh/logout return 410 without body binding or provider calls. Old token
Cookies are ignored; header Bearer callers retain their strict validation path. Missing required
hosted configuration permits startup and returns fixed 503 on hosted routes. See
[Hosted Login](../modules/AdminAuthentication/07-HostedLogin.md).

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
