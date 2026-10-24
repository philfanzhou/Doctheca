# Doctheca

A self-hosted **document library & full-text retrieval** service for educational content. Doctheca manages document uploads, delegates parsing to the external [StructaDoc](https://github.com/philfanzhou/StructaDoc) service, synchronizes structured Blocks/Markdown/Images locally, indexes them into OpenSearch, and exposes an admin console for management and precise search. It ships as a single container hosting a .NET minimal API and a Vue 3 admin SPA on the same port (5012).

> Doctheca was extracted from the Ruoyu.Study platform (`ruoyu.doclibrary`, earlier `ruoyu.docretrieval`) with its subtree history preserved. It pairs with StructaDoc: **StructaDoc** ingests and parses documents; **Doctheca** stores references, keeps searchable local copies, and serves retrieval.

## Features

| Module | Capability |
|--------|-----------|
| **DocumentManagement** | Upload (multipart), list, detail, metadata update (subject/grade/year), delete |
| **DocumentParse** | Delegate parsing to StructaDoc (Parse Run create/poll), sync Blocks/Markdown/Assets and optional printed page labels locally, proxy parsed images (ADR-0009) |
| **DocumentExport** | Export Markdown / HTML per document file or per parse |
| **DocumentMetadataAnalysis** | Optional LLM analysis of parsed Markdown to auto-detect subject/grade and backfill; failure never blocks parsing |
| **DocumentSearch** | OpenSearch full-text precise search with block-level indexing and subject/grade filters |
| **DocumentStructure** | Runtime-configured structure definitions and levels with required-ancestor audio policy (recorded only; enforcement pending), hand-marked structure nodes on a continuous view of a parse, and search narrowed to a node's subtree |
| **StructureAudio** | Bounded ZIP/folder/file import, hierarchical matching and administrator link review, metadata persistence, deletion/orphan recovery and administrator playback using fresh expiring public S3 links |
| **AdminAuthentication** | SignaCore hosted admin login via the official `SignaCore.Client.AspNetCore` client: `role=admin` pre-sign-in gate, server sessions, CSRF and prepared logout; legacy Bearer compatibility |
| **InternalCatalog** | Anonymous read-only structure definition and marked-document catalogs, current document trees with direct audio counts and audio-filtered discovery, node content pages, owned image bytes, subtree search and expiring same-origin direct-node audio; always mapped and restricted to the internal network by the deployer |

## Tech Stack

- **Backend**: .NET + ASP.NET Core minimal API
- **Admin frontend**: Vue 3 + TypeScript + Vite + Element Plus
- **Database**: PostgreSQL (EF Core 10 + Npgsql)
- **Search**: OpenSearch
- **Parsing**: external StructaDoc service (HTTP + scoped API key)
- **Object storage**: S3-compatible (SeaweedFS/MinIO) — legacy objects read-compat & deletion only; new originals/artifacts are owned by StructaDoc; structure audio is written under the `audio/` prefix (when internal S3 is configured)
- **Auth**: SignaCore or any OIDC discovery/JWKS-compatible issuer (RS256)
- **Optional**: OpenAI-compatible LLM for metadata analysis

## Repository Layout

```
Doctheca/
├── frontend/                # Vue 3 admin SPA (doctheca-admin)
├── src/
│   ├── Common/              # Doctheca.Common (vendored snapshot from ruoyu.common)
│   ├── Ai/                  # Doctheca.Ai (OpenAI-compatible client, SSE reader, masker)
│   ├── Consul/              # Doctheca.Consul (Consul KV configuration)
│   ├── Database/            # EF Core entities & repositories
│   ├── Domain/              # domain services & models
│   ├── Service/             # minimal API endpoints, StructaDoc client, OpenSearch, parse sync, LLM analysis
│   ├── Host/                # host composition, admin auth, ServiceMantle logging, wwwroot SPA
│   ├── Tests/               # unit & integration tests
│   └── Doctheca.sln
├── docs/                    # inherited Chinese documentation
├── Dockerfile               # single-image multi-stage build (backend + frontend)
├── start.sh                 # Docker run script
├── CONTEXT.md               # domain language (Document Library)
├── AGENTS.md                # AI collaboration conventions
├── LICENSE                  # MIT
└── README.md
```

> `src/Common`, `src/Ai`, and `src/Consul` are **vendored copies** (2026-09-26 snapshot) of Ruoyu.Study's `ruoyu.common`. They have no compile-time upstream sync; evaluate upstream fixes manually.

## Quick Start

### Docker

```bash
# build (context = repo root)
docker build -t doctheca:latest .

# run — requires IDENTITY_APP_ID / IDENTITY_APP_SECRET for the admin OIDC app
IDENTITY_APP_ID=<appid> IDENTITY_APP_SECRET=<secret> ./start.sh
```

`start.sh` reads shared configuration (PostgreSQL, OSS, OpenSearch, Loki, StructaDoc) from Consul KV; environment variables can override. See [`docs/development/Deployment.md`](docs/development/Deployment.md).

Anonymous catalog, current tree, node content, owned-image, subtree-search and direct-node audio GET routes
are always mapped under `/internal/v1/`. Restrict the entire case-insensitive `/internal` path segment
to trusted internal callers at the network/reverse proxy before deployment, and prevent direct public
access to port 5012. These routes have no caller authentication or document-level authorization.
Unknown internal paths and unsupported methods return JSON 404, never the SPA.
Audio storage is available with complete internal S3 configuration; LocalFile cannot store audio.
Audio proxy delivery uses the existing playback TTL without a public S3 address. Links expire and
are invalidated on restart; provide bounded scratch capacity and exclude token URLs from access logs.
See the [internal API contract](docs/Integration/InternalApi.md).

| Configuration key | Default | Requirement |
| --- | --- | --- |
| `DocumentAudio:MaxFileBytes` | `20971520` (20 MiB) | Positive integer |
| `DocumentAudio:MaxArchiveBytes` | `209715200` (200 MiB) | Integer >= MaxFileBytes and <= Int64.MaxValue - 1048576 |
| `DocumentAudio:PlaybackLinkSeconds` | `300` | Integer, 60–3600 inclusive |

Administrator preview separately requires a valid `Oss:PublicBaseUrl`; its absence affects only that preview.
Absent numeric keys use defaults; empty, malformed, overflowing or out-of-range values refuse
startup before database initialization or listening. Missing S3 settings disable import/sweep and
playback, while relational metadata and document reads remain available. The obsolete keys
`InternalApi:Enabled` and `DocumentAudio:Enabled` are ignored, with one startup Warning listing
only the keys present. No schema or data migration is needed. To roll back to the previous image
with the same enabled behavior, set both old switches to `true` again and retain network isolation.

The target PostgreSQL database must exist before startup: a missing database is refused by default with the fixed error `database_target_preparation.creation_not_allowed` (`DATABASE_ALLOW_CREATE=true` opts into automatic creation). Startup runs the EF Core migrations under a PostgreSQL advisory lock, so multiple instances starting against the same database serialize and only one executes the migration. See [`docs/database/migrations.md`](docs/database/migrations.md).

Printed page labels add the `document_parse_pages` table without changing HTTP responses or the
physical page numbering. Existing parses remain readable without labels; no remote backfill runs.
The previous image requires the documented page-label down script after stopping all instances
and taking a verified backup; it cannot run directly against the newer migration history.

Required-level policy (#129) adds default-false `structure_levels.required` and an administrator
level control/response field, including embedded administrator tree definitions. All ancestors of
a required level must also be required. This records policy only; audio behavior is unchanged.
A complete older-client PUT omitting the field resets flags to false. Stop mixed-version writers
and keep a verified backup and matching image/configuration before upgrading. Earlier images
require backup recovery or the explicit policy down maintenance (which discards flags); see
[required-policy upgrade and recovery](docs/database/migrations.md#required-level-policy-issue-129).

Startup and readiness use the shared ServiceMantle 0.3.1-rc.1 gate, receipt, and EF Core mapped-schema health source. The configuration and HTTP contracts remain unchanged. The operational code transition from the earlier 0.3.0 adoption remains: alerts replace `DOCTHECA_DB_CREATION_NOT_ALLOWED` with `database_target_preparation.creation_not_allowed` and `DOCTHECA_DB_ALLOW_CREATE_INVALID` with `database_target_preparation.invalid_target`. There is no schema/data migration in this upgrade; rollback redeploys the previous image against the same existing compatible database.

### Local .NET

```bash
dotnet run --project src/Host --configuration Release
```

### Verify

```bash
dotnet build src/Doctheca.sln --configuration Release
dotnet test src/Doctheca.sln --configuration Release --no-build
cd frontend && npm ci && npm run build
```

## Dependencies

| Dependency | Required | Purpose |
|-----------|----------|---------|
| PostgreSQL | Yes | document files, parses, local block/image copies |
| StructaDoc | Yes | document parsing & original/artifact storage (ADR-0009) |
| SignaCore (OIDC) | Yes | admin authentication, JWKS |
| OpenSearch | Yes | full-text block index & search |
| S3 (SeaweedFS/MinIO) | Legacy / optional | read-compat & deletion of pre-migration objects; structure audio under `audio/` when internal S3 is configured |
| OpenAI-compatible LLM | Optional | document metadata analysis |

## Documentation

Inherited Chinese documentation lives under [`docs/`](docs/): service overview, module specs, integration matrix, database schema, and development guides. Domain language is in [`CONTEXT.md`](CONTEXT.md). AI collaboration conventions are in [`AGENTS.md`](AGENTS.md). These inherited documents are still in Chinese; their translation to English is tracked in [#2](https://github.com/philfanzhou/Doctheca/issues/2).

Contributions should follow [CONTRIBUTING.md](CONTRIBUTING.md), and vulnerabilities should be reported through [SECURITY.md](SECURITY.md).

## License

MIT — see [LICENSE](LICENSE).

### Hosted administration frontend

The administration SPA uses SignaCore hosted login, driven by the official
`SignaCore.Client.AspNetCore` package with a thin host adaptation layer. Register exact
callback/post-logout URIs and inject server-only Confidential client credentials; deploy API and
SPA in the same image. Canonical HTTP and HTTPS callback pairs work in every environment.
The configured external callback scheme selects Secure Cookies for HTTPS and non-Secure Cookies
for HTTP, including clearing. Plain HTTP provides no network confidentiality, integrity or
listener authenticity; network/TLS protection is the deployer's responsibility. HTTP Authority
or Issuer still requires explicit `IdentityService:RequireHttpsMetadata=false` (default true).
Keep HTTPS callbacks and existing trusted forwarded-scheme handling behind TLS termination.
HTTPS deployments need no configuration migration.
See [hosted login](docs/modules/AdminAuthentication/07-HostedLogin.md) and
[Breaking changes and upgrade](docs/development/HostedLoginUpgrade.md). Old password endpoints
return 410 and old token Cookies are ignored. Missing login configuration permits startup with
fixed 503 login responses. Before rolling back to the preceding matching API/SPA image, restore
HTTPS registrations/configuration: it again refuses Production HTTP. Restart requires sign-in
again. No database migration or SQL/data rollback applies.

Document-owned chapters and audio survive successful reparse and parse deletion. Reparse detaches old content bindings; explicit binding enables new content. Existing short-lived audio playback remains available through current document/tree ownership. See [ownership and backup recovery](docs/database/migrations.md#document-owned-structure-132). Administrators can create a named book without a file, arrange custom chapters, attach its first source later and explicitly bind parsed content without changing chapter order or identity. Start with **New book** in document management; see [the authoring flow](docs/modules/DocumentManagement.md#books-authored-before-a-source-exists-133). Document audio import/review and the final listening scenario remain follow-up work.
