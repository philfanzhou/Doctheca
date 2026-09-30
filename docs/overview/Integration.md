# Integration — Integration Matrix

## Integration Overview Table

| Target | Protocol | Direction | Purpose | Security boundary |
|------|------|------|------|----------|
| Admin UI | HTTP same-origin | Inbound | Document management and search | Identity `role=admin` HttpOnly Cookie/JWT |
| SignaCore | HTTP/OIDC | Outbound | Login, refresh, revocation, JWKS | Authority from Consul |
| PostgreSQL | TCP | Outbound | Document/parse data CRUD | Service-private database |
| StructaDoc | HTTP | Outbound | Document upload, Parse Runs, Blocks/Markdown/Assets sync, image proxy, deletion (ADR-0009) | `Authorization: ApiKey` scoped key, BaseUrl from Consul/environment variables |
| MinIO/SeaweedFS | S3 | Outbound | Legacy files and parse images (read-only compatibility and delete cleanup) | Shared Consul configuration |
| OpenSearch | HTTP | Outbound | Block indexing and queries | Shared Consul configuration |
| LLM (OpenAI-compatible, optional) | HTTP | Outbound | Metadata analysis | Private ApiKey |

There is currently no Quaestura → Doctheca integration. When a real calling need
appears later, the data contract, authentication method, and deployment configuration must be
defined independently, without reusing the administrator cookie and without reserving unused interfaces.

## Admin API

Except for `/admin/auth/login`, `/admin/auth/refresh`, and `/admin/auth/logout`, all
`/admin/*` endpoints require the `DocthecaAdmin` policy. For the full authentication contract see
[AdminAuthentication](../modules/AdminAuthentication/01-FEATURE.md).

Main admin endpoints:

| Method | Path | Description |
|------|------|------|
| POST | `/admin/document-files/upload` | Upload a document |
| GET | `/admin/document-files` | Document list |
| GET | `/admin/document-files/{id}` | Document details |
| PUT | `/admin/document-files/{id}/metadata` | Update metadata |
| POST | `/admin/document-files/{id}/parse` | Trigger parsing |
| DELETE | `/admin/document-files/{id}` | Delete a document |
| GET/DELETE | `/admin/document-parses...` | Parse list and deletion |
| GET | `/admin/document-parses/{parseId}/images/{imageId}/content` | StructaDoc parse image proxy (browser authenticated via admin cookie) |
| GET | `/admin/documents/search` | Search |
| GET | `/admin/document-files|document-parses/.../export/*` | Export |

## Anonymous Entry Points

- `/`, static resources, and SPA fallback
- `/health/live` (liveness), `/health/ready` and its `/health` alias (readiness)
- `/admin/auth/login`
- `/admin/auth/refresh`
- `/admin/auth/logout`

### Health Probes

Doctheca exposes the fixed ServiceMantle health endpoints, all anonymous and always JSON
(they are mapped ahead of the SPA fallback, so they never return `index.html`):

| Endpoint | Purpose | Healthy response |
|------|------|------|
| `GET /health/live` | Liveness: the process is up. Never resolves the snapshot source or queries the database. | `200 {"status":"live"}` |
| `GET /health/ready` | Readiness: the startup initializer completed **and** a read-only probe of the EF-mapped PostgreSQL tables/columns succeeded. | `200 {"status":"ready","phase":"completed","migrationStatus":"succeeded","databaseStatus":"reachable","errorCode":null}` |
| `GET /health` | Readiness alias — **a public contract change**: it now returns the readiness projection instead of the old always-`healthy` liveness payload with a `timestamp`. | same as `/health/ready` |

Readiness fails closed to `503 {"status":"not_ready",...}` with a value-free safe code when the
initializer has not completed (`doctheca.startup_incomplete`), a mapped table/column is not
readable (`doctheca.schema_unavailable`), the database is unreachable
(`doctheca.database_unreachable`), or the probe times out / fails inside the library
(`health.probe_timeout` / `health.probe_failed`). Readiness is re-sampled on every request —
nothing is cached in the background — so a recovered database is reflected by the next call.
Use `/health/live` for process liveness and `/health/ready` (or `/health`) for database
readiness. Callers that parsed the old `/health` body must migrate: the `timestamp` field is
gone and `status` is now `ready`/`not_ready` rather than `healthy`.

## Failure Semantics

| Category | Behavior |
|------|------|
| Unauthenticated admin request | 401 |
| Authenticated but not an administrator | 403 |
| Identity unavailable | Login/refresh returns a controlled 502/401 without leaking internal exceptions |
| OpenSearch write failure | Does not block the main parsing flow; logs a Warning |
| OpenSearch query failure | Returns empty results and logs a Warning |

## Admin JSON Security Response Headers (ServiceMantle)

The JSON admin API responses carry an immutable six-header security baseline applied by the
ServiceMantle security response-header middleware (`AddSecurityResponseHeaders()` on the single
foundation builder + `UseServiceMantleSecurityResponseHeaders()` inserted after routing and
before authentication/authorization, so 401/403 challenges are covered too). Each header is
written exactly once via `OnStarting`, so a downstream component that pre-writes a duplicate or
a different value converges back to the single baseline value; the baseline cannot be weakened
by business components.

| Header | Value |
|------|------|
| `Cache-Control` | `no-store` |
| `Pragma` | `no-cache` |
| `X-Content-Type-Options` | `nosniff` |
| `X-Frame-Options` | `DENY` |
| `Referrer-Policy` | `no-referrer` |
| `Content-Security-Policy` | `default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'` |

The baseline applies to exactly the 13 marked JSON admin endpoints — the four
`/admin/auth/{login,refresh,logout,session}` entries; the six `/admin/document-files`
list/detail/upload/parse/metadata/delete entries; the two `/admin/document-parses` list/delete
entries; and `/admin/documents/search`. Endpoints are marked individually with
`RequireServiceMantleSecurityResponseHeaders`; the marker is a response classification, not an
authorization credential.

The CSP baseline is deliberately NOT applied to the rendering contracts, which keep their
existing behavior: the SPA and static assets, the four HTML/ZIP export endpoints, the parse
image content proxy, and the health endpoints. The strict `default-src 'none'` CSP would break
browser rendering of HTML previews, so those responses stay unmarked. New JSON admin endpoints
must opt in explicitly; HTML/image/export responses must not reuse the JSON CSP.

### Unhandled Error Protocol (Safe Problem Details)

The same 13 marked JSON admin endpoints — the four `/admin/auth/{login,refresh,logout,session}`
entries; the six `/admin/document-files` list/detail/upload/parse/metadata/delete entries; the
two `/admin/document-parses` list/delete entries; and `/admin/documents/search` — are also the
exact boundary of the ServiceMantle safe Problem Details protocol: an unhandled exception
becomes a fixed `application/problem+json` response carrying exactly
`type`/`title`/`status`/`correlationId`/`errorCode` (never exception message, `Data`, or stack),
shares one correlation id with the `x-correlation-id` response header and the request log
scope, and carries the same six single-value security headers as above. Business JSON
responses, 401/403 challenges, and parameter binding failures are never wrapped, and the
rendering contracts (SPA/static/health/exports/image proxy) never enter the boundary. For the
full protocol, fixed error codes, and explicit non-guarantees see
[ErrorHandling](../development/ErrorHandling.md).

## Service Identity, Correlation & Base Telemetry (ServiceMantle)

Doctheca registers the ServiceMantle host foundation (`ServiceMantle.Web` /
`ServiceMantle.Diagnostics` / `ServiceMantle.Logging` NuGet packages) through a single
extension,
`DocthecaServiceMantleExtensions.AddDocthecaServiceMantleFoundation()`, which later
migration slices extend instead of re-registering:

- **Identity fields**: `ServiceId` is the stable deployment identity `doctheca`;
  `InstanceId` is `doctheca-<32 lowercase hex characters>` and is regenerated on every host
  build (it changes on each restart and is not a persistent identity); `ServiceVersion`
  resolves from the entry assembly version. These are non-sensitive deployment metadata and
  must never be used as authentication or business identity.
- **Correlation ID (inbound contract)**: the first pipeline middleware is
  `UseServiceMantleCorrelationId()`. It wraps static files, the SPA fallback, authentication,
  all admin APIs, image proxying, and exports. The request and response header name stays
  `x-correlation-id` and the log field stays `CorrelationId`. A caller-supplied header value
  is echoed verbatim only when the request carries exactly one value of 1–64 characters whose
  first character is an ASCII letter or digit and whose remaining characters are ASCII
  letters, digits, `.`, `_`, or `-`. Missing, whitespace, overlong, illegal, comma-joined, or
  repeated values are discarded as a whole and replaced by a generated 32-character lowercase
  hexadecimal id; the original request header is never rewritten and rejected raw input is
  never logged. The same resolved value is published to the response header (injected via
  `OnStarting`, so a downstream overwrite converges back to it), the `HttpContext` accessor
  (`context.GetServiceMantleCorrelationId()`), and the request log scope, which also carries
  the `ServiceName`/`ServiceVersion`/`InstanceId` identity fields. Exceptions and request
  cancellation propagate untouched with the scope released.
- **What a Correlation ID is not**: it is a log-correlation value only — not unique, not
  unguessable, not authenticated. Callers must never use it for authorization, idempotency
  (StructaDoc idempotency keys are separate), replay protection, or audit subject identity.
- **Outbound**: the correlation id is not propagated on outbound `HttpClient` calls (the
  StructaDoc and Identity integrations are unchanged). Standard OpenTelemetry W3C
  `traceparent` headers may appear on outbound requests; downstream services must treat them
  as standard/unknown headers. No token or API key behavior changes.
- **Base telemetry**: ASP.NET Core and `HttpClient` tracing plus runtime metrics are
  instrumented in-process with the ServiceMantle identity as the OpenTelemetry resource. No
  exporter is registered in this slice, so telemetry never leaves the process; OTLP or
  Prometheus wiring is a later migration task.
- **No bootstrap or discovery side effects**: the foundation performs no Bootstrap file or
  installation reads/writes, creates no database tables, and registers no Consul service
  discovery, so it never triggers Consul Catalog write operations. Consul remains
  configuration-KV-only for this service.
- **Logging**: Console + Grafana Loki delivery runs through the `ServiceMantle.Logging`
  pipeline (release `0.2.1`), registered by
  `DocthecaLoggingExtensions.AddDocthecaLogging()` after the Consul configuration source.
  Every event passes the library's mandatory structured sanitizer before reaching either
  sink; per-category minimum levels are Information by default with Warning overrides for
  `Microsoft.AspNetCore` and `Microsoft.EntityFrameworkCore.Database.Command`. Identity comes
  from explicit `ServiceLogContext` scopes only — the request scope for HTTP requests, an
  explicit startup scope for host diagnostics/initialization, and a worker-lifetime scope for
  the StructaDoc parse worker (no invented HTTP CorrelationId outside a request). The legacy
  global Serilog enrichers (including `MachineName`/`ThreadId`) and the `Serilog`
  configuration section are retired. The fixed Loki stream label stays `service=Doctheca`
  (plus the sink-owned `level` label), so existing Grafana queries keep working; an empty
  `Loki:Uri` disables the remote sink while Console stays on, and plain-HTTP Loki endpoints
  are an explicitly accepted intranet deployment contract (`AllowInsecureHttp`).
