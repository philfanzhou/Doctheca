# Local Setup

How to set up and run `ruoyu.docretrieval` locally.

## Prerequisites

| Dependency | Required | Notes |
|------------|----------|-------|
| .NET 8 SDK | Yes | Project targets `net8.0` |
| PostgreSQL | Optional | Default connection string in `appsettings.json` points to `localhost:5432` |
| SQLite | Optional | Auto-detected fallback; no install needed (EF Core SQLite provider is bundled) |
| OpenSearch | Optional | Full-text search index; defaults to `http://localhost:9200` |
| Qdrant | Optional | Vector search / semantic search; defaults to `http://localhost:6333` |
| MinIO / SeaweedFS | Optional | S3-compatible object storage; defaults to `localhost:8333` |

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `USE_LOCAL_OSS` | (not set) | Set to `1` to use local filesystem storage instead of S3. Files are saved under `OSS_LOCAL_PATH` |
| `OSS_LOCAL_PATH` | `data/oss` | Directory for local file storage (only used when `USE_LOCAL_OSS=1`) |

No other environment variables are required. All other configuration comes from `appsettings.json`.

## Database: SQLite Auto-Detection

The service selects the database provider based on the connection string in `appsettings.json`:

- If the connection string contains `Host=` or `Server=` (case-insensitive) → **PostgreSQL** (`UseNpgsql`)
- Otherwise → **SQLite** (`UseSqlite`)

The default `appsettings.json` ships with a PostgreSQL connection string:

```
Host=localhost;Port=5432;Database=ruoyu_study_docretrieval;Username=phil
```

To use SQLite instead, change `ConnectionStrings:Default` to a SQLite-style string, e.g.:

```json
"ConnectionStrings": {
  "Default": "Data Source=data/sqlite/ruoyu_study_docretrieval.db"
}
```

If the connection string is empty or null, SQLite defaults to `Data Source=data/sqlite/ruoyu_study_docretrieval.db`.

The database and tables are created automatically on startup via `DatabaseInitializer.InitializeAsync`.

## Running the Service

From the `backend/ruoyu.docretrieval/` directory:

```bash
dotnet run --project src/Host
```

### Port Configuration

| Protocol | Default Port | Config Key | Notes |
|----------|-------------|------------|-------|
| gRPC | 5011 | `Endpoints:Grpc` | HTTP/2 only |
| HTTP | 5012 | `Endpoints:Http` | HTTP/1 only (Admin API + health) |

Ports can be overridden in `appsettings.json` under the `Endpoints` section.

## Minimal Local Setup (No External Services)

For basic upload/list/delete functionality without any external services:

1. Set the connection string to a SQLite value (remove `Host=` / `Server=`).
2. Set environment variable `USE_LOCAL_OSS=1`.
3. Run `dotnet run --project src/Host`.

This gives you:
- SQLite database (auto-created)
- Local filesystem object storage (files saved to `data/oss/`)
- Document upload, list, delete, and metadata update via HTTP Admin API
- Background ingestion worker (parsing only; search indexing will fail gracefully)

## Features Requiring External Services

| Feature | Service | Config Section | What happens without it |
|---------|---------|----------------|------------------------|
| Full-text search | OpenSearch | `OpenSearch` | Falls back to database LIKE search; index initialization logs a warning |
| Semantic / hybrid search | Qdrant | `Qdrant` | Semantic search unavailable; collection initialization logs a warning |
| Embedding generation | SiliconFlow API | `Embedding` | Semantic search cannot produce vectors; `Embedding:ApiKey` must be set for this to work |
