# Doctheca

A self-hosted **document library & full-text retrieval** service for educational content. Doctheca manages document uploads, delegates parsing to the external [StructaDoc](https://github.com/philfanzhou/StructaDoc) service, synchronizes structured Blocks/Markdown/Images locally, indexes them into OpenSearch, and exposes an admin console for management and precise search. It ships as a single container hosting a .NET minimal API and a Vue 3 admin SPA on the same port (5012).

> Doctheca was extracted from the Ruoyu.Study platform (`ruoyu.doclibrary`, earlier `ruoyu.docretrieval`) with its subtree history preserved. It pairs with StructaDoc: **StructaDoc** ingests and parses documents; **Doctheca** stores references, keeps searchable local copies, and serves retrieval.

## Features

| Module | Capability |
|--------|-----------|
| **DocumentManagement** | Upload (multipart), list, detail, metadata update (subject/grade/year), delete |
| **DocumentParse** | Delegate parsing to StructaDoc (Parse Run create/poll), sync Blocks/Markdown/Assets locally, proxy parsed images (ADR-0009) |
| **DocumentExport** | Export Markdown / HTML per document file or per parse |
| **DocumentMetadataAnalysis** | Optional LLM analysis of parsed Markdown to auto-detect subject/grade and backfill; failure never blocks parsing |
| **DocumentSearch** | OpenSearch full-text precise search with block-level indexing and subject/grade filters |
| **AdminAuthentication** | SignaCore (OIDC discovery/JWKS) admin login with `role=admin`, HttpOnly Cookie/JWT sessions, refresh/revoke |

## Tech Stack

- **Backend**: .NET + ASP.NET Core minimal API
- **Admin frontend**: Vue 3 + TypeScript + Vite + Element Plus
- **Database**: PostgreSQL (EF Core 10 + Npgsql)
- **Search**: OpenSearch
- **Parsing**: external StructaDoc service (HTTP + scoped API key)
- **Object storage**: S3-compatible (SeaweedFS/MinIO) — legacy objects read-compat & deletion only; new originals/artifacts are owned by StructaDoc
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
| S3 (SeaweedFS/MinIO) | Legacy | read-compat & deletion of pre-migration objects |
| OpenAI-compatible LLM | Optional | document metadata analysis |

## Documentation

Inherited Chinese documentation lives under [`docs/`](docs/): service overview, module specs, integration matrix, database schema, and development guides. Domain language is in [`CONTEXT.md`](CONTEXT.md). AI collaboration conventions are in [`AGENTS.md`](AGENTS.md). These inherited documents are still in Chinese; their translation to English is tracked in [#2](https://github.com/philfanzhou/Doctheca/issues/2).

Contributions should follow [CONTRIBUTING.md](CONTRIBUTING.md), and vulnerabilities should be reported through [SECURITY.md](SECURITY.md).

## License

MIT — see [LICENSE](LICENSE).
