# Verification Guide

## Prerequisites

Register a Confidential SignaCore client and exact callback/post-logout URIs; inject server-only
credentials and trust settings per [Hosted Login](../modules/AdminAuthentication/07-HostedLogin.md).
Choose the effective external HTTP or HTTPS profile in every environment; exact callback and
post-logout origins/paths must match registration. HTTPS uses Secure Cookies; HTTP omits Secure
for all four carriers and provides no network confidentiality, integrity or listener authenticity.
Operators choose network/TLS protection. Keep trusted forwarded-scheme handling behind TLS
termination, and explicitly set `IdentityService:RequireHttpsMetadata=false` for HTTP Authority or
Issuer (the Bearer default remains true). Never include credentials, protocol parameters, tokens
or Cookies in test reports.

## Anonymous Entry Points

```bash
curl -i http://localhost:5012/
curl -i http://localhost:5012/health/live
curl -i http://localhost:5012/health/ready
```

None is expected to return 401. `/health/live` returns `200 {"status":"live"}` whenever the process is up (it never queries the database). `/health/ready` — and its `/health` alias — return `200 {"status":"ready",...}` only when the startup initializer completed and a read-only probe of the mapped PostgreSQL tables/columns succeeded, otherwise `503 {"status":"not_ready",...}` with a value-free safe error code. This is a contract change from the previous `/health`, which always returned `{"status":"healthy","timestamp":...}`; the `timestamp` field is gone.

## Admin API Without Login

```bash
curl -i http://localhost:5012/admin/document-files
```

Expected: 401.

## Hosted Login and Session

Open the SPA and use its SignaCore login button. Authenticate on SignaCore, then confirm the
original local route returns and an administrator can read the protected API. A non-admin or
invalid/cancelled callback must not create a new session. No tokens enter browser storage.
Session restoration uses `/admin/auth/oidc/session`; writes carry the CSRF pair obtained from
`/admin/auth/oidc/csrf`. Missing/wrong CSRF returns 403; expired sessions return reauthentication
401 without refresh or write replay. The old token Cookies alone must return 401.

Use the SPA logout button: local revocation precedes provider preparation, successful preparation
returns the validated provider redirect, and provider failure reports local-only exit. Repeated
exit cannot resurrect a session or repeat provider preparation.

Run the browser flow on a Production host at a non-loopback HTTP origin as well as the HTTPS
profile. The automated Chromium test maps synthetic `.test` names to loopback, uses random
Kestrel ports and asserts the page is not a secure context. It verifies route restoration,
protected reads, missing/wrong CSRF rejection, a CSRF-authorized DELETE reaching its handler,
non-Secure HttpOnly Cookies, prepared logout/return via a CSRF-authorized top-level form, and
subsequent 401. The existing SPA button's cross-origin fetch path is tested separately; the form
ensures Chromium actually traverses the provider and return redirects. No production service,
trace, HAR or storage-state capture is used. Effective-options/Set-Cookie tests also cover all
four carriers and clearing when request and configured callback schemes differ; HTTPS remains
Secure. HTTP failure/cancellation/replay/concurrency and mixed Bearer/session checks retain the
same authorization boundaries. Settings cover all environments, independent Authority transport,
structural URI failures, safe diagnostics and the 500-character registration boundary.

HTTPS deployment configuration needs no migration. Before rollback to the preceding image,
restore HTTPS registrations/configuration: that image refuses Production HTTP. Restart requires
sign-in again; no SQL/data rollback applies.

## Retired Endpoints and Missing Configuration

POST empty, malformed JSON or non-JSON bodies to `/admin/auth/login`, `/refresh` and `/logout`.
Every result must be fixed 410 JSON, without provider calls or token Cookie writes. The old
logout must not revoke a current hosted session. See [Breaking changes](HostedLoginUpgrade.md).

With a required hosted key absent, startup, static serving and health remain available while all
six hosted routes return fixed 503 without state/tickets/provider calls. Logs contain only the
fixed diagnostic and missing key names. Complete malformed trust/URI/skew still rejects startup;
missing Bearer trust returns 401 rather than trusting substitute credentials.

## Removed Endpoints

`/internal/question-bank/*`, the old `/admin/document-parses/importable` and
`POST .../import-status` must all be unmapped.

## Automated Verification

Run at the repository root:

```bash
dotnet build src/Doctheca.sln --configuration Release
dotnet test src/Doctheca.sln --configuration Release --no-build

cd frontend
npm ci && npm run build && npm test && npm run test:e2e
```

### Test prerequisites

- The unit and hosting tests run everywhere the .NET SDK runs; no external service is needed.
- The container-backed integration tests in `Doctheca.Tests.Integration` boot the real
  `Program.cs` host through `WebApplicationFactory` against a one-off PostgreSQL via
  Testcontainers, so they require a reachable Docker daemon (the first run pulls
  `postgres:16-alpine`). They never touch a production Consul, Identity, StructaDoc,
  OpenSearch, or Loki endpoint: Consul points at a closed loopback port with the disk cache
  disabled, the empty `Loki:Uri` disables the remote Loki sink (Console stays on), external
  clients are replaced by in-process doubles, the
  LLM stays disabled, and every credential (including the per-run PostgreSQL password and the
  JWT signing key) is synthetic and never leaves the test process.
- To run only the Docker-free tests:

```bash
dotnet test src/Doctheca.sln --configuration Release \
  --filter "FullyQualifiedName!~Doctheca.Tests.Integration"
```

> Doctheca is a standalone repository; verification no longer depends on the Ruoyu.Study monorepo's `tests/integration/scripts/pre-commit.sh`. The commands above are this repository's complete local verification (see also the "Verification" section of the root `AGENTS.md`).

### GitHub-hosted browser test environment

`Build & Test` keeps its 30-minute limit and runs the complete existing Release suite,
including real Chromium, PostgreSQL, MinIO, OpenSearch and loopback Kestrel/OIDC.
Checkout, the .NET build, Node 20/frontend checks, the original production Dockerfile
build and startup/failure-exit verification still run on the disposable hosted runner.
The C# and JavaScript CodeQL checks and both complete image architectures are unchanged.
Only `dotnet test --no-build` runs inside the prepared browser environment.

The environment is the official `mcr.microsoft.com/playwright/dotnet:v1.63.0-noble`,
pinned to OCI index
`sha256:aa3eaf29a65df8c93fc32ab20cf9359f4d8ecc53e6e89f79378647ace87b46b3`
(AMD64 manifest
`sha256:4bb37fb71e75c18b6f8b8e4230139c7654de3dd39e55b66e84392c93933fed4e`).
It contains Chromium and Linux libraries; setup no longer downloads apt packages.
The reviewed AMD64 image has 1,382,462,645 compressed layer bytes, so a cold pull has a
real cost. A fresh hosted runner is not promised a warm Docker cache. The immutable
digest is the cache identity; there is no mutable browser-cache restore key.

`scripts/ci-test-environment.py prepare` pulls a missing image, verifies its platform
and digest, and launches an actual Chromium page. `test` repeats the gates and invokes
the unchanged full solution test command; it never skips tests on a cache hit.
`probe` checks real Linux tool/package/browser failure paths and nonzero/signal
propagation. `test-ci-test-environment.py` additionally checks the host wrapper's
digest/platform rejection, registry failure, phase reporting and exact cleanup boundary.
These small probes supplement the full application suite.

The wrapper mounts the host build's .NET installation, complete Node 20 installation
and **only** its NuGet `packages` directory read-only at the same absolute paths.
Generated MSBuild imports can contain those paths even with `--no-build`. It checks
the exact host SDK, SDK/runtime lists, Node/npm and Playwright package/driver version.
The image's own .NET runtime is not selected. Package/image updates must change the
reviewed version/digest mapping together; missing or mismatched tools, packages or
browsers fail setup rather than silently installing a different environment.

The repository/reports stay writable as the runner UID/GID. Home, npm/cache and temp
directories belong only to this invocation. No host environment wholesale, NuGet
configuration, Docker credentials or production configuration is mounted. The
test jobs also disable checkout credential persistence before mounting the repository.
The image pull has a five-minute setup limit and fixed failure diagnostics. Only the
disposable runner's local Unix Docker socket is used. Linux host networking plus
`TESTCONTAINERS_HOST_OVERRIDE=127.0.0.1` preserves random fixture ports and loopback
browser endpoints. Ryuk stays enabled; the wrapper has an init process, 1 GiB shared
memory and no privileged mode. Termination stops/removes its unique wrapper name;
it never prunes other containers, networks or caches. Runner disposal is the final
cleanup boundary after forced job cancellation.

The `Prepare test browser environment`, `Verify test environment failures` and
`Unit tests` steps separate setup time from test failures. The uploaded
`reports/ci-test-environment.json` records UTC starts, elapsed seconds, cold/warm
identity, image/tool/browser versions and failure status, alongside the original TRX.
Use GitHub job timestamps to measure queue delay and total job duration separately;
the report does not mistake queue time for installation time.

For an independent warm observation, add the `ci-measure-warm` label to a PR, or
manually run **Browser environment measurement** after it reaches the default branch.
This separate opt-in workflow has its own 30-minute limit. An explicitly named
preparation step first pulls/validates the
same digest on that runner; the normal prepare step then records a warm hit and the
complete suite, original Dockerfile and startup verifier still run once. Report the
prewarming time and complete job time as well as the warm setup/test phases. This
measures reuse on one isolated daemon, not cache persistence across hosted runners.
Normal PR checks measure their actual initial cache state without prewarming.

For context, the old PR #126 run 39953119170 attempt 1 spent 13m19s installing
dependencies: apt downloaded 21.2 MB in 12m54s with retries, whereas browser downloads
took about six seconds. PR #137 run 40081520374 used the same setup in 25s and completed
the job in 19m13s. The post-audio-import workload in PR #138 took 22m16s, including
18m52s of tests. These observations explain the dependency bottleneck and remaining
network/test variability; they do not establish a fixed percentage improvement.
Compare the actual post-change cold and warm reports and complete job durations,
including all 1,415 current tests and the two 58-recording browser scenarios.

This helper is Linux AMD64 CI tooling. On macOS/other local hosts use the native
verification commands and install the matching browser through
`pwsh src/Tests/Doctheca.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium`
when needed. Never mount macOS .NET or Node binaries into a Linux container.
Rollback reverts the workflow/helper changes and restores that host browser-install
step plus the original direct `dotnet test` command. No API, schema, data, application
configuration, production image or deployment rollback is involved.
