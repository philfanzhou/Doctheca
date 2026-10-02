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
- `DATABASE_ALLOW_CREATE` (maps to `Database__AllowCreate`; unset keeps the default refusal)
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
  - `Serilog:MinimumLevel:*` (no longer consumed by Doctheca: the logging migration retired the
    `Serilog` configuration section and levels are now configured in code; the shared KV entry
    may still serve other Ruoyu services)

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

Doctheca logs through the `ServiceMantle.Logging` pipeline (Console sink + optional Grafana Loki remote sink). The wiring lives in `src/Host/DocthecaLoggingExtensions.cs` and is registered in `Program.cs` via `builder.AddDocthecaLogging()` after the Consul configuration source. All direct ServiceMantle packages (`ServiceMantle`, `.Web`, `.Diagnostics`, `.Logging`, `.Database.PostgreSql`, and `.Persistence.Relational`) are pinned to the same formal **0.3.0** release. Do not float these versions.

### Log Levels

Levels are configured in code, not in `appsettings.json`: minimum level Information, with Warning overrides for `Microsoft.AspNetCore` and `Microsoft.EntityFrameworkCore.Database.Command`. The legacy `Serilog` configuration section is retired. The `Logging:LogLevel` section in `appsettings.json` remains only as the framework pre-filter for the few runtime components outside the library pipeline; it does not promise to cover the pipeline floor.

### Log Identity Fields

Every log entry carries identity fields from explicit `ServiceLogContext` scopes:

| Field | Source | Description |
|------|------|------|
| ServiceName | ServiceMantle `ServiceId` | Fixed as `doctheca` (lowercase; distinct from the Loki stream label `Doctheca`) |
| ServiceVersion | Entry assembly informational version | Resolved at startup |
| InstanceId | Per host build | `doctheca-<32 lowercase hex characters>`, regenerated on every restart |
| CorrelationId | Per HTTP request | Only inside the request scope (see ErrorHandling.md) |

Scope lifetimes: HTTP requests use the ServiceMantle request scope; host startup diagnostics/initialization run inside one explicit startup scope; the StructaDoc parse worker opens one worker-lifetime scope. The legacy global Serilog enrichers, including `MachineName` and `ThreadId`, are retired — dashboards keyed on those fields must switch to `InstanceId`/`ServiceName`.

### Loki Address Injection

The Loki address comes exclusively from the `Loki:Uri` configuration key:

| Source | Example value | Description |
|------|--------|------|
| `Loki:Uri` config key | http://ruoyu-loki:3100 | Recommended to be provided by Consul `config/ruoyu/shared.json` |
| `Loki:Uri` (fallback) | http://localhost:3100 | Fallback address in appsettings.json |

> **Empty value**: an empty `Loki:Uri` disables the remote sink (Console stays on and no remote request is created). The legacy fallback to a hard-coded `Serilog:WriteTo:1:Args:uri` address is gone — operators must set `Loki:Uri` explicitly.
>
> **Plain HTTP**: intranet HTTP Loki endpoints are accepted through an explicit `AllowInsecureHttp = true` — a deliberate acceptance that log content travels in cleartext on that path, not an automatic presumption of network trust. Use HTTPS whenever the path crosses an untrusted network.
>
> **Invalid values**: a malformed or credential-bearing URI (userinfo, query, or fragment) fails the host start with the safe code `loki.invalid_endpoint` without echoing the configured value.
>
> **Fault tolerance**: when Loki is unreachable or answers 503, the sink retries/drops inside its bounded in-memory queue and never blocks business requests; shutdown performs a bounded flush/drain (no exactly-once delivery and no flush guarantee after SIGKILL). `start.sh` does not pass a `LOKI_URI` environment variable; the Loki address is provided entirely by Consul.

The Loki stream labels stay fixed at `{service="Doctheca"}` plus the sink-owned `level` label, so existing Grafana queries and dashboards keep working.

### Startup Diagnostics

At startup the service outputs the Consul pull process and a summary of the final effective configuration, including the `LokiUri` field, making it easy to check whether the Loki address was loaded from Consul correctly. These startup logs run inside the explicit startup identity scope.

## Database Configuration

The code uses `UseNpgsql`. Run PostgreSQL for local development.

The database connection string is assembled at runtime from the following two parts:

- Shared Consul configuration: `PostgreSql:Host/Port/Username/Password`
- Service-private configuration: `Database:Name=doctheca`

After startup, if the configuration is correct, the diagnostic logs should show the final effective PostgreSQL host and database name.

### Database creation policy (`Database:AllowCreate`)

| Key | Default | Values | Description |
|-----|---------|--------|-------------|
| `Database:AllowCreate` (env `Database__AllowCreate`) | `false` | `true` / `false` only | Whether startup may create the target database when it is verifiably missing. Any other value refuses startup with `database_target_preparation.invalid_target`. |

- By default a missing `doctheca` database refuses startup with the fixed error code
  `database_target_preparation.creation_not_allowed` and writes nothing. Implicit database creation (the old
  EF `Migrate` behavior) is retired.
- Deployments that relied on implicit creation must either create the database manually before
  upgrading (`CREATE DATABASE doctheca OWNER <migration user>;`) or set
  `Database__AllowCreate=true`. With creation enabled, the migration account needs CREATEDB and
  access to the `postgres` maintenance database; the maintenance connection uses the same
  credentials as the target connection string (only the database name differs) and never reaches
  the logs.
- Unreachable servers, authentication or permission failures, and server/database identity
  conflicts are refused with the provider's safe `database_target_preparation.*` codes — never
  answered with a creation fallback.

### ServiceMantle 0.3.0 upgrade and rollback

Startup now calls the shared `StartupDatabaseGate` directly once, after LLM initialization and before OpenSearch initialization; no automatic hosted gate is registered. Health uses the shared scoped `EfCoreHealthSnapshotSource<DocthecaDbContext>` in MappedSchema mode, with a 3-second probe budget. The existing HTTP routes, JSON fields, readiness error codes, and anonymous access remain unchanged. Connection-opening failures, including SQLSTATE class 28 authentication failures, still map to `doctheca.database_unreachable`.

Update operational alerts for these two intentional code changes:

| Previous code | Current code |
|---|---|
| `DOCTHECA_DB_CREATION_NOT_ALLOWED` | `database_target_preparation.creation_not_allowed` |
| `DOCTHECA_DB_ALLOW_CREATE_INVALID` | `database_target_preparation.invalid_target` |

There is no schema/data migration or new configuration switch in this package upgrade. Keep existing backups and stop the old version during upgrades. Roll back by redeploying the previous image against the same existing compatible database and reverting alert codes; no database rollback script is required. Creation and committed migration effects are not undone after a later failure or cancellation.

### Multi-instance startup semantics

Startup database work runs in three phases (see [Migrations](../database/migrations.md)):
deployment validation, target preparation, and migration orchestration under a PostgreSQL session
advisory lock keyed by the `doctheca` service id. When several instances start against the same
database, only the lock holder executes the migration; the others wait (fixed 30-second acquire
budget), re-read the state, and skip. Lock timeout, lease loss, a too-new database version, a
failed migration, or a mismatching final state all exit the process non-zero with the safe
`migration.*` error codes. During an upgrade window, stop the old (lock-free) version first — do
not mix old and new versions against the same database. The readiness `migrationStatus` field
follows the same orchestration result (`succeeded` only after the held-lock final inspection
passed).

## Startup Command

```bash
dotnet run --project src/Host
```

The Doctheca container uses the Docker default bridge network; it no longer joins `ruoyu-net` and no longer relies on Docker container names to reach Consul. At deployment time you must ensure the container can access the LAN address pointed to by `CONSUL_HTTP_ADDR`.

The image includes the distribution's [Tini](https://github.com/krallin/tini) package as PID 1 and starts `dotnet Doctheca.Host.dll` as its child. Tini forwards termination signals and preserves the child's exit status; callers do not need `docker run --init`. A database startup refusal exits non-zero before HTTP begins listening. `start.sh` keeps `--restart unless-stopped`, so Docker may retry a failed start: inspect the restart count and safe diagnostic code rather than treating a restarting container as ready. An explicit `--entrypoint` override bypasses the image's process wrapper.

This entrypoint change needs no configuration or database migration and keeps the port, application arguments, and HTTP contracts unchanged. Roll back by redeploying the previous image; that also restores its previous process entrypoint and failure-exit behavior. No SQL rollback is required.

To verify the built image's default entrypoint (Python 3 and a local Docker daemon required):

```bash
docker build -t doctheca:startup-verify .
python3 scripts/verify-container-startup.py doctheca:startup-verify
```

The check creates an isolated PostgreSQL 16 container/network and runtime-only synthetic credentials. Without adding `--init` or overriding the entrypoint, it checks invalid AllowCreate, missing-target default refusal, unreachable PostgreSQL, and authentication refusal for actual non-zero container exit, no HTTP listener, safe codes, no credential disclosure, and no missing-target creation. It also checks successful startup, exact health responses, a single gate invocation, and graceful SIGTERM shutdown. Containers, the network, and temporary configuration files are cleaned up. CI runs this check against the release Dockerfile image.

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
1. `IDocumentAnalysisService.InitializeAsync` — LLM initialization (if an ApiKey is configured)
2. Shared `StartupDatabaseGate` — deployment validation, explicit target preparation, and `DocthecaMigrationExecutor` under the PostgreSQL session advisory lock; the gate publishes success only after the held-lock final inspection passes (see [Migrations](../database/migrations.md))
3. `OpenSearchIndexService.EnsureIndexAsync` — create the search index (best-effort)

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
