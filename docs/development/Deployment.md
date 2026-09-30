# Deployment — Deployment & Operations

## Service Ports

| Port | Protocol | Purpose |
|------|------|------|
| 5012 | HTTP | Web admin UI + search API + static files + health check |

The port can be overridden via `Endpoints:Http` in `appsettings.json`.

## Consul Integration

`doctheca` reads shared configuration at process startup through the shared Consul code in `src/Consul` (vendored from ruoyu.common); it currently does not register the service in the Consul Catalog.

The startup script `start.sh` only keeps the following kinds of parameters:

- `CONSUL_HTTP_ADDR`
- `CONSUL_TOKEN`
- `Endpoints:Http`
- The database-name part of `ConnectionStrings:Default`
- `OpenSearch:IndexName`
- `LlmDocumentAnalysis:*`
- `StructaDoc:ApiKey`
- `Authentication:CookieSecure`
- `IdentityService:AppId`
- `IdentityService:AppSecret`

The following configuration has moved into the shared Consul KV:

- `config/ruoyu/shared.json`
  - `PostgreSql:Host`
  - `PostgreSql:Port`
  - `PostgreSql:Username`
  - `PostgreSql:Password`
  - `Oss:InternalEndpoint`
  - `Oss:InternalSecure`
  - `Oss:AccessKey`
  - `Oss:SecretKey`
  - `Oss:BucketName`
  - `Oss:PublicBaseUrl`
  - `OpenSearch:Url`
  - `Loki:Uri`
- `config/ruoyu/service-endpoints.json`
  - `IdentityService:Authority`
  - `IdentityService:Issuer`
  - `IdentityService:AdditionalValidIssuers`
  - `IdentityService:Audience`
  - `IdentityService:RequireHttpsMetadata`
  - `IdentityService:ClockSkewSeconds`
  - `DocthecaService:Url` (for callers accessing Doctheca)
  - `StructaDoc:BaseUrl` (Doctheca calling the external StructaDoc parse service)
- `config/ruoyu/serilog.json`
  - `Serilog:MinimumLevel:*`

> `Database:Name`, `OpenSearch:IndexName`, `LlmDocumentAnalysis:*` and `StructaDoc:ApiKey` are all Doctheca-private configuration and do not go into the shared KV; `StructaDoc:BaseUrl` lives in the shared KV `service-endpoints.json`.

## Environment Variables

| Variable | Default | Description |
|------|--------|------|
| `CONSUL_HTTP_ADDR` | `192.168.100.10:8500` | Consul HTTP API address; the repository value is an example internal LAN address — replace it with the actual address at deployment time |
| `CONSUL_TOKEN` | (empty) | Consul ACL token (required when ACLs are enabled) |
| `CONSUL_KV_PREFIX` | `config/ruoyu` | Consul shared KV prefix |
| `CONSUL_CACHE_DIR` | `./data/consul` | Consul local cache directory |
| `USE_LOCAL_OSS` | (unset) | Set to `1` to use local file-system storage instead of S3 |
| `OSS_LOCAL_PATH` | `data/oss` | Local file-storage directory (used only when `USE_LOCAL_OSS=1`) |
| `DOCTHECA_COOKIE_SECURE` | `false` | Maps to `Authentication__CookieSecure`; must be set to `true` for HTTPS production deployments |
| `IDENTITY_APP_ID` | none | Doctheca's dedicated AppId in SignaCore; mapped to `IdentityService__AppId` at startup |
| `IDENTITY_APP_SECRET` | none | Doctheca AppSecret; injected only from deployment secrets and mapped to `IdentityService__AppSecret` at startup |

LLM document-analysis configuration is injected via environment variables in `start.sh`; secrets are never committed to the repository:

| Environment variable | Default | Description |
|------|------|------|
| `LLM_API_KEY` | empty | Maps to `LlmDocumentAnalysis__ApiKey`; must be set to enable document analysis |
| `LLM_BASE_URL` | `https://api.siliconflow.cn/v1` | OpenAI-compatible API address |
| `LLM_MODEL` | `Qwen/Qwen2.5-7B-Instruct` | Document-analysis model |
| `LLM_CONTEXT_LENGTH` | `128K` | Document-analysis context length |

## OSS Address Configuration

- `Oss:InternalEndpoint` / `Oss:InternalSecure` are used for server-side uploads, downloads and bucket operations; when deploying
  SeaweedFS standalone, fill in its server `IP:published-port` and the actual protocol.
- `Oss:PublicBaseUrl` is used to generate browser-accessible presigned addresses; in production it is
  `https://ry.zhoufan.asia/oss`.
- OSS addresses come from Consul `config/ruoyu/shared.json`; restart Doctheca for changes to take effect.
- The public-network reverse proxy for `/oss/` is managed by User Web Nginx; its upstream address is not generated dynamically from Consul,
  so when migrating SeaweedFS you must also modify `conf/nginx.conf` in the User Web deployment directory and restart User Web.

When running the Host directly you can also use the .NET hierarchical configuration name
`Authentication__CookieSecure`. `IdentityService:Authority/Audience/RequireHttpsMetadata`
are provided by Consul; `appsettings.json` only keeps the local-development fallback.

## Downstream Dependencies

| Dependency | Port | Purpose |
|------|------|------|
| PostgreSQL | 5432 | Primary database (`doctheca`) |
| OpenSearch | 9200 | Full-text search index |
| MinIO / SeaweedFS | Deployment-dependent (default 8333) | Object storage (S3-compatible; address comes from Consul `Oss:InternalEndpoint`) |
| Consul | 8500 | Shared configuration reads and service registration |
| Loki | 3100 | Log aggregation (configured via Consul `Loki:Uri`) |
| SignaCore | 5002 | Admin login, token refresh/revocation, OIDC discovery/JWKS |

## Admin Authentication Configuration

Minimal configuration:

```json
{
  "IdentityService": {
    "Authority": "http://192.168.100.10:5002",
    "Issuer": "http://192.168.100.10:5002",
    "Audience": "QuantumZhou.microservices",
    "RequireHttpsMetadata": false,
    "AppId": "<deployment-secret>",
    "AppSecret": "<deployment-secret>"
  },
  "Authentication": {
    "CookieSecure": false
  }
}
```

SignaCore's `POST /api/auth/token` requires application credentials for both the password and refresh grants. Therefore
Doctheca uses a dedicated SignaCore App with no callback, SMS disabled and a shared audience, and sends
`X-Admin-AppId` / `X-Admin-AppSecret` with every token request. Bootstrap admin-role injection does not depend on the
callback. Authority, Issuer, Audience and the metadata HTTPS requirement take Consul
`config/ruoyu/service-endpoints.json` as the deployment source of truth; AppId/AppSecret come only from deployment secrets. The
`192.168.100.10` above is merely the repository's fake internal-LAN example — replace it with the actual address at deployment time.

HTTPS is used by default between the browser and Doctheca, with `Authentication:CookieSecure=true`. `RequireHttpsMetadata=false` only means operations explicitly accepts HTTP Identity metadata; the HTTP issuer must also be enabled on the SignaCore side, and the code does not automatically relax just because the address is a private network, a container name, or the environment is Development.

Cookie names and paths:

| Cookie | Path | Purpose |
|--------|------|------|
| `docthecaAccessToken` | `/admin` | Admin API JWT |
| `docthecaRefreshToken` | `/admin/auth` | Refresh and logout |

Both cookies are HttpOnly and SameSite=Strict, and are not exposed to front-end JavaScript.

## Logging Configuration

Doctheca uses Serilog instead of the native Microsoft.Extensions.Logging, dual-writing to Console + Grafana Loki. The integration is exactly the same as in the Identity service.

### Serilog Configuration

The service configures Serilog via `builder.Host.UseAgentSerilog("Doctheca")`. The `Logging` section in `appsettings.json` is kept only for the few runtime components that do not go through Serilog; **business log levels are governed by the Serilog configuration**.

| Config key | Default | Description |
|--------|--------|------|
| `Serilog:MinimumLevel:Default` | Information | Default log level |
| `Serilog:MinimumLevel:Override:Microsoft.AspNetCore` | Warning | ASP.NET Core log level |
| `Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore.Database.Command` | Warning | EF Core log level |
| `Serilog:WriteTo:0:Name` | Console | Console sink (array index 0) |
| `Serilog:WriteTo:1:Name` | GrafanaLoki | Loki sink (array index 1) |
| `Serilog:WriteTo:1:Args:uri` | http://ruoyu-loki:3100 | Loki address (ultimately overridden by `Loki:Uri`) |
| `Serilog:WriteTo:1:Args:labels:0:key` | service | Loki label key |
| `Serilog:WriteTo:1:Args:labels:0:value` | Doctheca | Loki label value (service label) |

### Log Enrichers

Every log entry automatically carries the following fields:

| Field | Source | Description |
|------|------|------|
| ServiceName | UseAgentSerilog parameter | Fixed as `Doctheca` |
| ServiceVersion | UseAgentSerilog parameter | Defaults to `1.0.0` |
| InstanceId | Environment.MachineName | Instance identifier |
| MachineName | Enrichers.Environment | Host name |
| ThreadId | Enrichers.Thread | Thread ID |

### Loki Address Injection

The Loki address is injected uniformly into `Serilog:WriteTo:1:Args:uri` through the `Loki:Uri` configuration key:

| Source | Example value | Description |
|------|--------|------|
| `Loki:Uri` config key | http://ruoyu-loki:3100 | Recommended to be provided by Consul `config/ruoyu/shared.json` |
| `Loki:Uri` (fallback) | http://localhost:3100 | Fallback address in appsettings.json |

> **Fault tolerance**: If `Loki:Uri` is not set, the Loki Sink uses the fallback address in `Serilog:WriteTo:1:Args:uri` from appsettings.json. When Loki is unreachable the sink retries asynchronously and does not affect service startup. `start.sh` does not pass a `LOKI_URI` environment variable; the Loki address is provided entirely by Consul.

### Startup Diagnostics

At startup the service outputs the Consul pull process and a summary of the final effective configuration, including the `LokiUri` field, making it easy to check whether the Loki address was loaded from Consul correctly.

## Database Configuration

The code uses `UseNpgsql`. Run PostgreSQL for local development.

The database connection string is assembled at runtime from the following two parts:

- Shared Consul configuration: `PostgreSql:Host/Port/Username/Password`
- Service-private configuration: `Database:Name=doctheca`

After startup, if the configuration is correct, the diagnostic logs should show the final effective PostgreSQL host and database name.

## Startup Command

```bash
dotnet run --project src/Host
```

The Doctheca container uses the Docker default bridge network; it no longer joins `ruoyu-net` and no longer relies on Docker container names to reach Consul. At deployment time you must ensure the container can access the LAN address pointed to by `CONSUL_HTTP_ADDR`.

Callers access Doctheca via `DocthecaService:Url` in Consul `config/ruoyu/service-endpoints.json`. For cross-host deployments this value must be a LAN IP and host-mapped port reachable by the callers, for example:

```json
{
  "DocthecaService": {
    "Url": "http://192.168.100.10:5012"
  }
}
```

The Consul initialization script uses `cas=0` and only creates KVs that do not yet exist. Modifying the initialization JSON does not overwrite existing values; during migration you must also update the live KV. If callers read this configuration only at startup, they must also be restarted after the live KV is updated.

The post-deployment smoke check should hit `http://127.0.0.1:5012/health/live` for process liveness and `http://127.0.0.1:5012/health/ready` (or its `/health` alias) for database readiness on the Doctheca target host. Note the contract change: `/health` is now a readiness alias returning `200 {"status":"ready",...}` only once the startup initializer completed and a read-only schema probe succeeded, and `503 {"status":"not_ready",...}` otherwise — it no longer returns the old always-`healthy` payload with a `timestamp`.

The service automatically performs the following at startup:
1. `DatabaseInitializer.InitializeAsync` — table creation + column migration (SQL-based, no EF Core Migration)
2. `OpenSearchIndexService.EnsureIndexAsync` — create the search index (best-effort)
3. `IDocumentAnalysisService.InitializeAsync` — LLM initialization (if an ApiKey is configured)

## Prebuilt Images & Version Releases

`start.sh` runs the locally built `doctheca:latest` by default; when using a published image, specify `IMAGE_REPO` and `IMAGE_TAG`:

```bash
docker pull ghcr.io/philfanzhou/doctheca:0.1.0
IMAGE_REPO=ghcr.io/philfanzhou/doctheca IMAGE_TAG=0.1.0 ./start.sh
```

GitHub Actions (`.github/workflows/ci.yml`) runs builds, tests and image builds on PRs, `main` pushes and release tags; only `main` pushes and tags publish images to `ghcr.io/philfanzhou/doctheca`.

| Trigger | Published image tag | GitHub Release |
|------|---------------|----------------|
| Merge to `main` | `edge` (moves with the latest commit; not a formal release) | none |
| `MAJOR.MINOR.PATCH` tag | `MAJOR.MINOR.PATCH`, `MAJOR.MINOR`, `latest` | Formal release, marked as latest |
| `MAJOR.MINOR.PATCH-rc.NUMBER` tag | `MAJOR.MINOR.PATCH-rc.NUMBER` only | Pre-release; does not move `MAJOR.MINOR` or `latest` |

Releases are driven entirely by pushing tags; do not create Releases or push images manually. Tags carry no `v` prefix and are placed on commits already merged into `main`:

```bash
git switch main && git pull
git tag -a 0.1.0 -m "Doctheca 0.1.0"
git push origin 0.1.0
```

Release candidates use the `-rc.NUMBER` suffix, e.g. `git tag -a 0.1.0-rc.1 -m "Doctheca 0.1.0-rc.1"`. Tags in any other format are rejected by CI.

After a tag is pushed, the full `Build & Test` runs first; once it passes, the following execute in order:

1. **Publish GHCR Image**: build and push the image, with provenance and SBOM attached.
2. **Publish GitHub Release**: create a Release for the tag, record the digest of the actually published image, and attach the GitHub auto-generated changelog.

A Release is created only for tags whose tests pass and whose image is pullable. When the pipeline for a tag fails, neither the image nor a Release is published; fix the cause and cut a new version number — do not move a failed tag. Re-running the pipeline for a tag that already has a Release will not overwrite manually edited Release notes.

Each `edge` push leaves the old manifest as an untagged package version in GHCR; GHCR does not clean these up automatically — delete them manually in the package settings when needed.

## Database Backup & Restore

```bash
# Backup
docker exec ruoyu-postgres pg_dump -U postgres doctheca | gzip > backup_doctheca_$(date +%Y%m%d_%H%M%S).sql.gz

# Restore
gunzip -c backup_doctheca_20240101_020000.sql.gz | docker exec -i ruoyu-postgres psql -U postgres -d doctheca
```
