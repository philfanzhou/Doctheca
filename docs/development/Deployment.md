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

> `Database:Name`, `OpenSearch:IndexName`, `LlmDocumentAnalysis:*`, `DocumentAudio:*`, `InternalApi:*` and `StructaDoc:ApiKey` are all Doctheca-private configuration and do not go into the shared KV; `StructaDoc:BaseUrl` lives in the shared KV `service-endpoints.json`.

## Environment Variables

| Variable | Default | Description |
|------|--------|------|
| `CONSUL_HTTP_ADDR` | `192.168.100.10:8500` | Consul HTTP API address; the repository value is an example internal LAN address — replace it with the actual address at deployment time |
| `CONSUL_TOKEN` | (empty) | Consul ACL token (required when ACLs are enabled) |
| `CONSUL_KV_PREFIX` | `config/ruoyu` | Consul shared KV prefix |
| `CONSUL_CACHE_DIR` | `./data/consul` | Consul local cache directory |
| `USE_LOCAL_OSS` | (unset) | Set to `1` to use local file-system storage instead of S3 |
| `OSS_LOCAL_PATH` | `data/oss` | Local file-storage directory (used only when `USE_LOCAL_OSS=1`) |
| `IDENTITY_APP_ID` | none | Doctheca's dedicated AppId in SignaCore; mapped to `IdentityService__AppId` at startup |
| `IDENTITY_APP_SECRET` | none | Doctheca AppSecret; injected only from deployment secrets and mapped to `IdentityService__AppSecret` at startup |

LLM document-analysis configuration is injected via environment variables in `start.sh`; secrets are never committed to the repository:

| Environment variable | Default | Description |
|------|------|------|
| `LLM_API_KEY` | empty | Maps to `LlmDocumentAnalysis__ApiKey`; must be set to enable document analysis |
| `LLM_BASE_URL` | `https://api.siliconflow.cn/v1` | OpenAI-compatible API address |
| `LLM_MODEL` | `Qwen/Qwen2.5-7B-Instruct` | Document-analysis model |
| `LLM_CONTEXT_LENGTH` | `128K` | Document-analysis context length |

## Internal API

`InternalApi:Enabled` (`InternalApi__Enabled` in the container environment) defaults to `false`.
Only an explicit boolean `true` at startup registers the anonymous GET routes
`/internal/v1/structure-definitions`, `/internal/v1/documents` and
`/internal/v1/documents/{documentFileId}/structure`,
`/internal/v1/structure-nodes/{nodeId}/content` and
`/internal/v1/structure-nodes/{nodeId}/images/{imageId}/content` and `/internal/v1/search`. Missing, false and invalid
values fail closed without stopping the host; invalid-value diagnostics contain only a fixed
reason and the key name. Restart after changing the setting. Inject it into the Doctheca
container environment directly; `start.sh` does not forward arbitrary host environment variables.

Before enabling, restrict **all `/internal/*` paths** on port 5012 to the internal network using
trusted firewall/proxy routing. Block the entire case-insensitive `/internal` path segment at
public ingress, including `/internal` itself, and prevent direct public access to the container
port. The API has no caller authentication or per-document access control. Existing admin
authentication, CSRF, SPA/static files and health checks keep their current behavior.
Disabled paths, unknown internal paths and unsupported methods return fixed JSON 404, never SPA
HTML. Catalogs, current trees, revision-checked node content, owned image bytes and subtree search are available; see
[Internal API](../Integration/InternalApi.md). Audio routes remain unavailable.
Image responses never redirect to storage, carry no-store/nosniff headers, and allow only the
documented raster image media types. Do not log image bytes, upstream error text or source references;
the StructaDoc client disables URI logging and consumes no provider error body for asset reads.
Request cancellation releases streams; a failure after response start aborts the connection.
Search uses bounded strict cursors and current-node ownership checks, with the existing eventually
consistent OpenSearch subtree index. Engine failures return empty results with fixed safe diagnostics;
query text, request JSON and provider bodies are not logged. An already-sent search read may finish
after caller cancellation, but the cancelled request cannot return a successful search response.

This is additive and creates no persisted state, schema, object-storage or port changes.
Rollback: set `InternalApi__Enabled=false` and restart, or redeploy the previous image against
the same database. Network restrictions remain the deployer's responsibility.

## OSS Address Configuration

- `Oss:InternalEndpoint` / `Oss:InternalSecure` are used for server-side uploads, downloads and bucket operations; when deploying
  SeaweedFS standalone, fill in its server `IP:published-port` and the actual protocol.
- `Oss:PublicBaseUrl` is used to generate browser-accessible presigned addresses; in production it is
  `https://ry.zhoufan.asia/oss`.
- OSS addresses come from Consul `config/ruoyu/shared.json`; restart Doctheca for changes to take effect.
- The public-network reverse proxy for `/oss/` is managed by User Web Nginx; its upstream address is not generated dynamically from Consul,
  so when migrating SeaweedFS you must also modify `conf/nginx.conf` in the User Web deployment directory and restart User Web.

## Structure Audio Storage (issue #90)

The persistence and object-lifecycle foundation (#107) reserves `audio/` in the existing S3 bucket.
Only `StructureAudioStore` can write/delete this prefix; keys are `audio/{parseId:N}/{audioId:N}{extension}`.
Legacy uploads and parse images stay read-only. Administrator import/review (#108) is available from the structure editor; administrator temporary playback (#109) is also available.

`DocumentAudio:Enabled` (`DocumentAudio__Enabled`) defaults to `false`. Setting it to `true` enables
administrator import and the internal lifecycle/recovery endpoint. It requires S3 (not `USE_LOCAL_OSS=1`)
and the existing `Oss:InternalEndpoint`, `Oss:AccessKey`, `Oss:SecretKey` and `Oss:BucketName`.
`Oss:PublicBaseUrl` is not required for import or cleanup. Invalid `DocumentAudio` flags disable the audio lifecycle without stopping existing features.
Missing S3 settings disable recovery without constructing a client; document/structure reads remain
available, while legacy storage operations keep their existing configuration requirements.
Diagnostics contain only key names. `DocumentAudio:MaxFileBytes` defaults to 20971520 (20 MiB), and `DocumentAudio:MaxArchiveBytes` to 209715200 (200 MiB) for uploaded file bytes and the expanded total across the entire request. Limits must be positive, file <= archive, and archive <= Int64.MaxValue - 1048576. The full multipart body allows only 1 MiB additional overhead. Imports are capped at 1000 audio files, 4000 ZIP entries, 1000-character paths and 255-character segments. Invalid settings disable audio. Request/expanded overflow returns 413; invalid batches/per-file excess return 400, with no object or row writes. `DocumentAudio:PlaybackLinkSeconds` defaults to 300 seconds and allows 60–3600. Invalid/malformed TTL or missing/invalid `Oss:PublicBaseUrl` disables playback only. Playback never falls back to the internal address. The public host must be browser-reachable (HTTPS for an HTTPS console); a path-prefix proxy must strip the prefix and preserve signed Host/query. Signing uses the standard `us-east-1` region without public endpoint discovery. Configure S3/proxy CORS for actual console origins when fetch/WebAudio is used, and allow required GET/HEAD/Range behavior. Internal object checks have a 30-second combined playback deadline. Treat the signed URL as a temporary credential: no logs, persistence, HAR/traces or analytics. Issued URLs may remain usable until expiry after deletion/replacement/disable, and expiry does not stop an already-open stream.

After a parse/document deletion, object cleanup is best-effort. An administrator can call
`POST /admin/structure-audio/orphan-sweep` to remove unreferenced `audio/` objects older than one
hour; recent, referenced and legacy objects are preserved. A disabled/unavailable store returns a
safe 503. See [StructureAudio](../modules/StructureAudio.md).

`IdentityService:Authority/Audience/RequireHttpsMetadata`
are provided by Consul; `appsettings.json` only keeps the local-development fallback.

## Downstream Dependencies

| Dependency | Port | Purpose |
|------|------|------|
| PostgreSQL | 5432 | Primary database (`doctheca`) |
| OpenSearch | 9200 | Full-text search index |
| MinIO / SeaweedFS | Deployment-dependent (default 8333) | Object storage (S3-compatible; address comes from Consul `Oss:InternalEndpoint`): legacy objects read-only, structure audio under `audio/` |
| Consul | 8500 | Shared configuration reads and service registration |
| Loki | 3100 | Log aggregation (configured via Consul `Loki:Uri`) |
| SignaCore | 5002 | Hosted admin login, prepared logout, OIDC discovery/JWKS |

## Admin Authentication Configuration

Hosted administration configuration (replace example hostnames and inject credentials):

```json
{
  "AdminOidc": {
    "RedirectUri": "https://admin.example.test/admin/auth/oidc/callback",
    "PostLogoutRedirectUri": "https://admin.example.test/admin/auth/oidc/logout/return"
  },
  "IdentityService": {
    "Authority": "https://identity.example.test",
    "Issuer": "https://identity.example.test",
    "Audience": "QuantumZhou.microservices",
    "RequireHttpsMetadata": true,
    "AppId": "<deployment-secret>",
    "AppSecret": "<deployment-secret>"
  }
}
```

Register a Confidential SignaCore client with PerApplication audience and exact callback and
post-logout registrations. AppId/AppSecret remain required server-only backchannel credentials;
existing Bearer trust/audience configuration remains independent. Inject secrets at runtime.
The current SPA uses [hosted login](../modules/AdminAuthentication/07-HostedLogin.md).

Hosted Cookies are Secure outside Development/Testing numeric loopback HTTP; private hostnames
or production HTTP are not exceptions. Missing required login keys permit service startup,
log a fixed code and missing key names, and return fixed 503 on all hosted routes. Complete
nonempty invalid configuration still refuses startup. Missing Bearer trust rejects all credentials.
Configuration recovery requires restart. `start.sh` no longer fails early when client credentials
are empty, so liveness/static serving can run while operators finish registration.

Old POST login/refresh/logout return fixed 410 JSON without body binding or provider calls; old
access/refresh token Cookies are ignored. `AdminOidc:Enabled`, `Authentication:CookieSecure` and
`DOCTHECA_COOKIE_SECURE` are retired. Follow [Breaking changes and upgrade](HostedLoginUpgrade.md)
for the matched-image rollback, which restores the old password contract without SQL.

## Logging Configuration

Doctheca logs through the `ServiceMantle.Logging` pipeline (Console sink + optional Grafana Loki remote sink). The wiring lives in `src/Host/DocthecaLoggingExtensions.cs` and is registered in `Program.cs` via `builder.AddDocthecaLogging()` after the Consul configuration source. All direct ServiceMantle packages (`ServiceMantle`, `.Web`, `.Diagnostics`, `.Logging`, `.Database.PostgreSql`, and `.Persistence.Relational`) are pinned to the same official **0.3.3** release. Do not float these versions.

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

The existing `StructaDoc` named HttpClient explicitly uses `AddServiceMantleCorrelationIdPropagation`: an internal call made within a request carries the middleware-resolved `x-correlation-id`. An explicit outgoing header is preserved; calls without HttpContext do not invent an ID. The pooled handler reads the current context on each send. Base URL, 300-second default timeout, and the StructaDoc ApiKey remain unchanged. Standard OIDC discovery/token backchannels, OpenAI, Consul, object storage, and OpenSearch do not opt in. Destination and redirect trust remain the caller’s responsibility. `InstanceId.CreateRandom` retains the per-host `doctheca-<Guid:N>` format and performs no persistence. This upgrade adds no configuration or data migration; rollback redeploys the previous code and matching package set.

Scope lifetimes: HTTP requests use the ServiceMantle request scope; host startup diagnostics/initialization run inside one explicit startup scope; the StructaDoc parse worker opens one worker-lifetime scope. The legacy global Serilog enrichers, including `MachineName` and `ThreadId`, are retired — dashboards keyed on those fields must switch to `InstanceId`/`ServiceName`.

### Loki Address Injection

The Loki address comes exclusively from the `Loki:Uri` configuration key:

| Source | Example value | Description |
|------|--------|------|
| `Loki:Uri` config key | http://ruoyu-loki:3100 | Recommended to be provided by Consul `config/ruoyu/shared.json` |
| `Loki:Uri` (fallback) | http://localhost:3100 | Fallback address in appsettings.json |

> **Empty value**: an empty `Loki:Uri` disables the remote sink (Console stays on and no remote request is created). The legacy fallback to a hard-coded `Serilog:WriteTo:1:Args:uri` address is gone — operators must set `Loki:Uri` explicitly.
>
> **Plain HTTP**: ServiceMantle 0.3.3 accepts HTTP and HTTPS Loki endpoints without a separate switch. Keeping intranet HTTP is a deliberate acceptance that log content travels in cleartext on that path, not an automatic presumption of network trust. Use HTTPS whenever the path crosses an untrusted network.
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

### Document structure upgrade (issue #89)

The first start of the image containing migration `20261021094612_AddDocumentStructure` migrates the
database in place (new nullable columns, three new tables, `sequence` backfilled for every block)
and recreates the OpenSearch index for mapping version 3. Afterwards, call
`POST /admin/search-index/reindex` once (or click 重建检索索引 on the parse results page) to restore
search. Rolling back to the previous image requires the documented down script first; see
[Migrations — Document Structure Upgrade](../database/migrations.md#document-structure-upgrade-issue-89).

### Structure audio upgrade (issue #90)

The first start of the image containing migration `20261021153535_AddStructureAudio` only creates the
`document_structure_audio` table; no existing table or the OpenSearch mapping changes. It delivers
the persistence/cleanup foundation (#107). Import/review (#108) and read-only administrator playback (#109) reuse this schema without a new migration. Reverting only the playback image removes those routes/player without changing audio data. Before
upgrading, take and verify a consistent database backup together with the corresponding image,
configuration and object storage recovery set. To go back, stop all instances and restore that
pre-upgrade recovery set; post-backup writes are lost. Old images need not accept the new schema,
and image rollback never automatically runs Down migrations. The destructive audio down script
is a separate optional maintenance operation. See
[Migrations — Structure Audio Upgrade](../database/migrations.md#structure-audio-upgrade-issue-90).

### Printed page label upgrade (issue #102)

Migration `20261022160829_AddDocumentParsePages` adds only the parse-owned page-label table.
Existing file/parse/Block/image/structure/audio rows, HTTP fields and OpenSearch mapping remain
unchanged. Older parses stay readable without labels and are not backfilled from StructaDoc.
Take a verified backup before upgrading. The previous image refuses the new history ID: stop
every instance, back up, apply the page-label down script, then start the previous image. This
maintenance loses labels only; standard recovery restores the matching pre-upgrade backup.
See [Migrations — Printed Page Label Upgrade](../database/migrations.md#printed-page-label-upgrade-issue-102).

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

Newly published release images contain both `linux/amd64` and `linux/arm64` under the same
tag. Docker selects the matching platform automatically, including ARM64 when Colima
runs an `aarch64` VM on Apple Silicon. Existing version tags are unchanged; use a
version published after multi-platform support was added, or a new `edge` image where
that channel is available. `docker buildx imagetools inspect <image>:<tag>` lists
the platforms included in a published tag.

The Node and .NET SDK stages run on the build host and produce static web assets and
framework-dependent assemblies. The runtime base image and OS packages follow the
target architecture. CI builds both complete images without publishing on pull
requests, and publishing waits for that check as well as the existing tests.


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

### Hosted administration frontend

The administration SPA uses [hosted login](../modules/AdminAuthentication/07-HostedLogin.md).
See [Breaking changes and upgrade](HostedLoginUpgrade.md) for registration, configuration,
retired endpoints/Cookies and the matched-image rollback contract.
