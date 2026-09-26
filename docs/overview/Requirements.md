# Requirements — Service-Level Requirements

> This file is the requirements summary entry point. For detailed functional requirements see the [modules/](../modules/README.md) directory.

## Functional Requirements

### Document Management and Parsing

| ID | Requirement | Details |
|------|------|------|
| FR-01 | Document management: upload document files (PDF/DOC/DOCX/PPT/PPTX); originals are forwarded to StructaDoc for primary-ownership storage (ADR-0009), while the local `document_files` table keeps references and metadata; list/details/metadata update/deletion (cascading to parses, images, indexes, and StructaDoc document deletion) | [DocumentManagement](../modules/DocumentManagement.md) |
| FR-02 | StructaDoc document parsing: a background Worker calls the StructaDoc API to create and poll Parse Runs; on success it synchronizes Blocks/Markdown/Assets into `document_parses` / `document_parse_blocks` / `document_parse_images`; Office conversion and large-file chunking are handled by StructaDoc's built-in capabilities | [DocumentParse](../modules/DocumentParse.md) |
| FR-03 | Document export: file-level/parse-level Markdown/HTML export; image paths support relative/Base64/Presigned modes; ZIP packaging supported | [DocumentExport](../modules/DocumentExport/01-FEATURE.md) |
| FR-04 | Document metadata analysis: after parsing completes, when a document lacks `subject`/`grade`/`year`, an LLM fills them in automatically on a best-effort basis | [DocumentMetadataAnalysis](../modules/DocumentMetadataAnalysis/01-FEATURE.md) |
| FR-05 | Update document metadata: modify `subject`/`grade`/`year` via `PUT /admin/document-files/{id}/metadata`, refreshing the OpenSearch index accordingly | [DocumentSearch](../modules/DocumentSearch/01-FEATURE.md) |
| FR-06 | Exact search: OpenSearch-based keyword matching with subject/grade/year/documentTitle filters; includes index writes (auto-indexing after parsing completes) | [DocumentSearch](../modules/DocumentSearch/01-FEATURE.md) |
| FR-07 | Administrator authentication: login with the Identity bootstrap administrator; all browser admin APIs require a valid `role=admin`; tokens stored only in HttpOnly cookies | [AdminAuthentication](../modules/AdminAuthentication/01-FEATURE.md) |

## Non-Functional Requirements

| ID | Requirement |
|------|------|
| NFR-01 | Search response time < 2s (exact search) |
| NFR-02 | Document parsing executes asynchronously (StructaDocParseWorker + StructaDoc-persisted Parse Runs), without blocking the upload response |
| NFR-03 | OpenSearch indexing failures do not block the main parsing flow; they are only logged |
| NFR-04 | Database uses PostgreSQL (`UseNpgsql` hard-coded) |
| NFR-05 | Single service port: HTTP(5012) |
| NFR-06 | Admin authentication validates JWT issuer, audience, signature, and expiry; a regular-user JWT must return 403 |
| NFR-07 | SPA, static files, and health checks remain anonymous; admin APIs must use the Identity administrator identity |
