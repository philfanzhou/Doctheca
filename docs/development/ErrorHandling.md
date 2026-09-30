# Error Handling Standards

## HTTP Status Code Rules

HTTP endpoint implementations must use standard HTTP status codes; custom status codes are not allowed:

| HTTP status code | When to use | Example |
|-------------|----------|------|
| `400 Bad Request` | Request parameter validation failed | Invalid ID format, required field empty |
| `401 Unauthorized` | Missing, expired or invalid identity | Admin request without login |
| `403 Forbidden` | Valid identity but insufficient permissions | Regular Identity user accessing an admin endpoint |
| `404 Not Found` | Requested resource does not exist | Student not found, wrong question not found |
| `409 Conflict` | Resource already exists (conflict on creation) | Duplicate submission |
| `422 Unprocessable Entity` | Business precondition not met | Auditing a wrong question that is not in the pending-audit state |
| `502 Bad Gateway` | A synchronous downstream such as Identity returned an invalid response or is unreachable | Identity unavailable during admin login |
| `500 Internal Server Error` | Internal service error | Database exception, unexpected error |
| `503 Service Unavailable` | Service unavailable | Dependent service down |

## Error Message Standards

1. Authentication and inter-service protocol errors use concise, stable English messages; the front end is responsible for presenting Chinese copy
2. Error messages must not reveal whether an account exists, password-validation details, token contents or internal exceptions
3. The same class of error uses identical wording across endpoints

## Error Response Format

All HTTP endpoints return a unified JSON error response format:

```json
{
  "success": false,
  "message": "Error description message",
  "errorCode": "ERROR_CODE"
}
```

- Domain exceptions map to the corresponding HTTP status code (400 / 404 / 409 / 422)
- All other uncaught exceptions return 500 with a sanitized error message

## Parameter Validation

Parameter validation should happen at the entry of HTTP endpoint methods, returning errors as early as possible.

## Logging Standards

- Use structured logging placeholders; do not use string interpolation
- The exception object must be passed in: use `LogError(ex, ...)` rather than `LogError(ex.Message, ...)`
- Expected NotFound cases use the Warning level
- Never log passwords, access/refresh tokens, cookies, phone numbers or other sensitive information

### ServiceMantle Logging Pipeline (Console + Loki)

Doctheca logs through the `ServiceMantle.Logging` pipeline (Console sink + optional Grafana
Loki remote sink), registered by `builder.AddDocthecaLogging()`
(`src/Host/DocthecaLoggingExtensions.cs`) after the Consul configuration source is loaded.
Every event passes the library's mandatory structured sanitizer — denied field names
(`password`, `token`, `apikey`, `authorization`, `cookie`, `connection string`, …) are
redacted, and exception `Message`/stack/`Data` are not emitted by the pipeline at all. This
is a second boundary behind the caller-side masking below, not a replacement for it: free-text
secrets inside message templates are still the caller's responsibility.

Log levels are configured in code, not in `appsettings.json`: minimum level Information with
Warning overrides for `Microsoft.AspNetCore` and `Microsoft.EntityFrameworkCore.Database.Command`.
The legacy `Serilog` configuration section is retired; `Logging:LogLevel` in `appsettings.json`
remains only as the framework pre-filter and does not promise to cover the library pipeline floor.

The Loki address comes exclusively from the `Loki:Uri` configuration key (environment
variable/command line > Consul KV > `appsettings.json`):

| Config key | Source | Example value | Description |
|--------|------|--------|------|
| `Loki:Uri` | Consul `config/ruoyu/shared.json` | http://ruoyu-loki:3100 | Recommended to be provided by the Consul shared configuration |
| `Loki:Uri` (fallback) | `appsettings.json` | http://localhost:3100 | Fallback address when Consul is unreachable |

> **Empty value**: an empty `Loki:Uri` disables the remote sink (Console stays on); the legacy
> fallback to a hard-coded `Serilog:WriteTo:1:Args:uri` address no longer exists — operators
> must set `Loki:Uri` explicitly.
>
> **Plain HTTP**: the existing intranet HTTP Loki deployment is accepted through an explicit
> `AllowInsecureHttp = true` in `DocthecaLoggingExtensions`. This is a deliberate acceptance
> that log content travels in cleartext on that path, not an automatic presumption that the
> network is trusted; use HTTPS whenever the path crosses an untrusted network.
>
> **Invalid values**: a malformed or credential-bearing URI (userinfo, query, or fragment)
> fails the host start with the safe library code `loki.invalid_endpoint` without echoing the
> configured value.
>
> **Fault tolerance**: when Loki is unreachable or answers 503, the sink retries/drops inside
> its bounded in-memory queue and never blocks business requests; shutdown performs a bounded
> flush/drain (no exactly-once delivery, no disk buffering, no flush guarantee after SIGKILL).
> `start.sh` does not pass a `LOKI_URI` environment variable; the Loki address is provided
> entirely by Consul.

The fixed Loki stream labels stay `{service="Doctheca"}` plus the sink-owned `level` label, so
existing Grafana queries keep working. No request/user/id value is promoted to a stream label.

### Sensitive Field Masking

The following fields must be masked before being written to logs (including Loki). Logs ultimately end up in Loki dashboards, and plaintext sensitive information would violate compliance requirements:

| Field type | Masking rule | Example |
|----------|---------|------|
| LLM ApiKey / StructaDoc ApiKey | Keep the first 4 + last 4 characters and replace the middle with `****`; if shorter than 8 characters, replace the whole value with `****` | `sk-a****1b2c` |
| OSS AccessKey / SecretKey | Never logged | — |
| Passwords / JWT / Refresh Token / Cookie | Never logged | — |

Implementation location: the `Doctheca.Ai.SensitiveDataMasker` static utility class (in the vendored library `src/Ai`). Business code calls it in the form `_logger.LogInformation("... ApiKey={ApiKey}", SensitiveDataMasker.MaskApiKey(apiKey))`.

> Database fields are not subject to this rule and still store original values as the business requires; the rule only constrains log output.

### CorrelationId Flow

CorrelationId applies to the HTTP path, making it easy to trace request chains across services in Loki:

- HTTP path: the ServiceMantle correlation ID middleware (`app.UseServiceMantleCorrelationId()` from the `ServiceMantle.Web` package) resolves exactly one Correlation ID per request. A caller-supplied `x-correlation-id` request header is reused verbatim only when it carries exactly one value of 1–64 characters whose first character is an ASCII letter or digit and whose remaining characters are ASCII letters, digits, `.`, `_`, or `-`. Missing, whitespace, overlong, illegal, comma-joined, or repeated values are discarded as a whole and replaced by a generated 32-character lowercase hexadecimal id. The resolved value is published to the `x-correlation-id` response header (injected via `OnStarting` before any response starts, so a downstream write converges back to the same single value), to the `HttpContext` accessor (`context.GetServiceMantleCorrelationId()`), and to the request log scope as the `CorrelationId` field, alongside the `ServiceName`, `ServiceVersion`, and `InstanceId` identity fields from the ServiceMantle host foundation.
- The original request headers are never rewritten; rejected raw input never reaches the logs; exceptions and cancellation propagate untouched with the request scope released; and the id is not propagated to outbound `HttpClient` calls (OpenTelemetry W3C trace propagation is a separate protocol).

HTTP controllers (`DocumentFileEndpoints`, etc.), static files, and the SPA fallback must be within the scope of this middleware.

Middleware pipeline position (registration order in `Program.cs`):

```
UseServiceMantleCorrelationId()
  → UseDefaultFiles() / UseStaticFiles()
  → UseAuthentication() / UseAuthorization()
  → MapAdminAuthEndpoints / MapAdminEndpoints
  → MapHealth / MapFallbackToFile
```

Static files and the SPA fallback remain anonymous; the admin route group requires `DocthecaAdmin`.

> A Correlation ID is a log-correlation value only: it is not unique, unguessable, or authenticated, and must never be used for authorization, idempotency, replay protection, or audit subject identity. During a request, the ServiceMantle request scope supplies the current `CorrelationId`/`ServiceName`/`ServiceVersion`/`InstanceId` values to the logging pipeline. Non-request logs carry identity through explicit `ServiceLogContext` scopes instead of the retired global Serilog enrichers: one scope wraps host startup diagnostics/initialization, and the StructaDoc parse worker opens one worker-lifetime scope — neither invents an HTTP CorrelationId.
