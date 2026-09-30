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
- `/health`
- `/admin/auth/login`
- `/admin/auth/refresh`
- `/admin/auth/logout`

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

## Service Identity, Correlation & Base Telemetry (ServiceMantle)

Doctheca registers the ServiceMantle host foundation (`ServiceMantle.Web` /
`ServiceMantle.Diagnostics` NuGet packages) through a single extension,
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
- **Logging**: the legacy Serilog Console/Loki pipeline is retained in this slice, including
  its global enrichers (fixed `ServiceName`/`ServiceVersion` and machine-name `InstanceId`)
  as the fallback for non-request logs; during a request, the ServiceMantle scope supplies the
  current identity and correlation fields. Loki stream labels are unchanged. Unifying the
  global enrichers with the ServiceMantle identity belongs to the logging migration task.
