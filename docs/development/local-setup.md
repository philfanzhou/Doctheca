# Local Setup

How to set up and run `doctheca` locally.

## Prerequisites

| Dependency | Required | Description |
|------|----------|------|
| .NET 10 SDK | Yes | The project targets `net10.0` |
| PostgreSQL | Yes | The default connection string points to `localhost:5432` (`doctheca`)|
| OpenSearch | Optional | Full-text search index; default address `http://localhost:9200`; recommended Docker image version `2.19.5` (the service references the `OpenSearch.Net` 2.2.0 client) |
| MinIO / SeaweedFS | Optional | S3-compatible object storage; the local fallback `Oss:InternalEndpoint` is `localhost:8333`; can be switched to local file-system storage via `USE_LOCAL_OSS=1` |

## Environment Variables

| Variable | Default | Description |
|------|--------|------|
| `USE_LOCAL_OSS` | (unset) | Set to `1` to use local file-system storage instead of S3; files are saved to `OSS_LOCAL_PATH` |
| `OSS_LOCAL_PATH` | `data/oss` | Local file-storage directory (used only when `USE_LOCAL_OSS=1`) |

No other environment variables are required. When running locally directly, the rest of the configuration comes from `appsettings.json`; once Consul is connected,
shared configuration such as `Oss:InternalEndpoint`, `Oss:InternalSecure` and `Oss:PublicBaseUrl` is overridden by
`config/ruoyu/shared.json`.

## Database Configuration

The service uses **PostgreSQL** (`UseNpgsql`; the connection string comes from `ConnectionStrings:Default`). The default `appsettings.json` contains:

```
Host=localhost;Port=5432;Database=doctheca;Username=phil
```

The database and tables are created automatically at startup via `DatabaseInitializer.InitializeAsync` (`CREATE TABLE IF NOT EXISTS`).

## Running the Service

Run in the `` directory:

```bash
dotnet run --project src/Host
```

### Port Configuration

| Protocol | Default port | Config key | Description |
|------|----------|--------|------|
| HTTP | 5012 | `Endpoints:Http` | Admin API + search API + health check |

The port can be overridden in the `Endpoints` section of `appsettings.json`.

## Minimal Local Setup (local storage for OSS only)

Run without depending on external object storage (PostgreSQL is still required):

1. Set the environment variable `USE_LOCAL_OSS=1` (optionally specify `OSS_LOCAL_PATH`; default `data/oss`).
2. Run `dotnet run --project src/Host`.

You will get:
- A PostgreSQL database (tables created automatically)
- Local file-system object storage (files saved to `data/oss/`)
- Document upload, listing, deletion and metadata updates via the HTTP admin API
- A background parse worker (parsing is fully functional; search returns empty results when OpenSearch is unavailable)

## Features Requiring External Services

| Feature | Service | Config section | Behavior when missing |
|------|------|--------|-------------|
| Full-text search | OpenSearch | `OpenSearch` | Search returns empty results (`SearchDomainService` returns empty + LogWarning); a warning is logged during index initialization |
