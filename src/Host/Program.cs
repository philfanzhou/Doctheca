using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Doctheca.Common.Authentication;
using Doctheca.Ai;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Service;
using Doctheca.Service.Parsing;
using Doctheca.Service.StructaDoc;
using Doctheca.Host;
using Doctheca.Host.Authentication;
using Doctheca.Host.Health;
using Doctheca.Consul;
using ServiceMantle.Health;
using ServiceMantle.Web.Http;

var builder = WebApplication.CreateBuilder(args);

// ========== ServiceMantle (service identity, correlation id, base telemetry) ==========
// The single foundation registration root; later ServiceMantle migration slices extend this
// builder instead of regenerating the identity or registering a second root (see
// DocthecaServiceMantleExtensions for the identity and no-exporter contract).
var serviceMantle = builder.Services.AddDocthecaServiceMantleFoundation();

// ========== ServiceMantle health probes (live + readiness, /health alias) ==========
// No dependency contributors are registered: readiness is exactly the startup receipt plus
// the read-only PostgreSQL probe owned by DocthecaHealthSnapshotSource below.
serviceMantle.AddServiceMantleHealthEndpoints(options =>
{
    options.ProbeTimeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddSingleton<DocthecaStartupReceipt>();
builder.Services.AddScoped<IServiceHealthSnapshotSource, DocthecaHealthSnapshotSource>();

// ========== ServiceMantle security response headers (marked JSON admin endpoints) ==========
// Immutable six-header baseline (Cache-Control/Pragma/X-Content-Type-Options/X-Frame-Options/
// Referrer-Policy/Content-Security-Policy) applied only to endpoints explicitly marked with
// RequireServiceMantleSecurityResponseHeaders; no weakening options exist.
serviceMantle.AddSecurityResponseHeaders();

// ========== ServiceMantle safe Problem Details (marked JSON admin endpoints) ==========
// Conditional mapping for the only exception type whose framework status must survive the
// boundary: a BadHttpRequestException escaping a handler or the server-side body-read stage
// keeps its status through ordered candidates (413 → 415 → unconditional 400 default declared
// last; the library picks the first matching candidate and rejects a second unconditional
// candidate at startup). Everything else falls through to the library's fixed, safe 500. No
// Exception-wide mapping is registered, no exception message/detail is projected into the
// response, and no extension fields are used.
serviceMantle.AddConditionalExceptionMapping<BadHttpRequestException>(
[
    new ExceptionMappingCandidate<BadHttpRequestException>(
        StatusCodes.Status413PayloadTooLarge,
        "http.payload_too_large",
        "The request payload is too large.",
        condition: exception => exception.StatusCode == StatusCodes.Status413PayloadTooLarge),
    new ExceptionMappingCandidate<BadHttpRequestException>(
        StatusCodes.Status415UnsupportedMediaType,
        "http.unsupported_media_type",
        "The request media type is not supported.",
        condition: exception => exception.StatusCode == StatusCodes.Status415UnsupportedMediaType),
    new ExceptionMappingCandidate<BadHttpRequestException>(
        StatusCodes.Status400BadRequest,
        "http.request_invalid",
        "The request could not be processed."),
]);

builder.Configuration.AddRuoyuConsulConfiguration(builder.Configuration);

// ========== ServiceMantle logging (Console + optional Grafana Loki) ==========
// Registered after the Consul configuration source so Loki:Uri from Consul KV is visible.
builder.AddDocthecaLogging();

var consulOptions = RuoyuConsulOptions.Bind(builder.Configuration);
var consulRuntimeState = RuoyuConsulRuntimeState.Instance;

var httpPort = builder.Configuration.GetValue<int?>("Endpoints:Http") ?? 5012;

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpPort);
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 200 * 1024 * 1024;
});

builder.Services.Configure<DocthecaCookieOptions>(
    builder.Configuration.GetSection(DocthecaCookieOptions.SectionName));
builder.Services.AddOptions<IdentityClientCredentialsOptions>()
    .Bind(builder.Configuration.GetSection(IdentityClientCredentialsOptions.SectionName))
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.AppId)
            && !string.IsNullOrWhiteSpace(options.AppSecret),
        "IdentityService:AppId and IdentityService:AppSecret must be configured for Doctheca.")
    .ValidateOnStart();
builder.Services.AddHttpClient<IIdentityAuthenticationService, IdentityAuthenticationService>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<IdentityAuthenticationOptions>>().Value;
        client.BaseAddress = new Uri(options.Authority.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
builder.Services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<IdentityAuthenticationOptions>>().Value;
    var documentRetriever = new HttpDocumentRetriever
    {
        RequireHttps = options.RequireHttpsMetadata
    };
    return new ConfigurationManager<OpenIdConnectConfiguration>(
        $"{options.Authority.TrimEnd('/')}/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        documentRetriever);
});
builder.Services.AddSingleton<IIdentityTokenValidator, IdentityTokenValidator>();

builder.Services.AddRuoyuJwtBearer(
    builder.Configuration,
    builder.Environment,
    consumer =>
    {
        consumer.MapInboundClaims = false;
        consumer.AccessTokenCookieName = DocthecaAuthenticationConstants.AccessCookieName;
        consumer.NameClaimType = "unique_name";
        consumer.RoleClaimType = "role";
    },
    options =>
{
    options.AddPolicy(DocthecaAuthorizationPolicies.Admin, policy =>
    {
        // The AdminSession scheme joins the policy unconditionally; with AdminOidc:Enabled=false
        // no session cookie exists, so authentication and challenge behave exactly as before.
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, AdminOidcConstants.SessionScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
            context.User.Identities.Any(identity => identity.IsAuthenticated)
            && context.User.Claims.Any(claim =>
                claim.Type is "role" or ClaimTypes.Role
                && string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase)));
    });
});

// SignaCore hosted login (issue #47, first slice): authorization-code callback plus opaque
// server-side session cookies. Enabled=false by default leaves every existing route and the
// legacy password flow byte-for-byte unchanged; the three new endpoints answer a fixed 503.
builder.Services.AddDocthecaAdminOidc(builder.Configuration, builder.Environment);

var fallbackConnectionString = builder.Configuration.GetConnectionString("Default");
var connectionString = SharedPostgreSqlConnectionStringFactory.BuildOrFallback(builder.Configuration, fallbackConnectionString)
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured");
builder.Services.AddDbContext<DocthecaDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

var useLocalOss = Environment.GetEnvironmentVariable("USE_LOCAL_OSS") == "1";
if (useLocalOss)
{
    var localPath = Environment.GetEnvironmentVariable("OSS_LOCAL_PATH") ?? "data/oss";
    builder.Services.AddSingleton<IOssService>(new LocalFileOssService(localPath));
}
else
{
    builder.Services.Configure<OssOptions>(builder.Configuration.GetSection("Oss"));
    builder.Services.AddScoped<IOssService, S3OssService>(sp =>
    {
        var options = sp.GetRequiredService<IOptions<OssOptions>>().Value;
        return new S3OssService(options);
    });
}

// Search Index Services
builder.Services.Configure<OpenSearchOptions>(builder.Configuration.GetSection("OpenSearch"));
builder.Services.AddSingleton<ISearchIndexService, OpenSearchIndexService>();

// LLM document analysis service (optional)
var llmSection = builder.Configuration.GetSection(DocumentAnalysisOptions.SectionName);
if (llmSection.Exists() && !string.IsNullOrEmpty(llmSection["ApiKey"]))
{
    builder.Services.Configure<DocumentAnalysisOptions>(llmSection);
    // Register OpenAiCompatibleClient in streaming mode (HttpClient.Timeout disabled,
    // per-attempt timeout + SSE idle timeout enforced by the client/reader).
    // OpenAiCompatibleClient's constructor configures BaseAddress + Bearer auth on the
    // HttpClient, so AddHttpClient only needs to register the named client for pooling.
    builder.Services.AddHttpClient(nameof(OpenAiCompatibleClient));
    builder.Services.AddSingleton<OpenAiCompatibleClient>(sp =>
    {
        var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient(nameof(OpenAiCompatibleClient));
        var options = sp.GetRequiredService<IOptions<DocumentAnalysisOptions>>().Value;
        var logger = sp.GetRequiredService<ILogger<OpenAiCompatibleClient>>();
        return new OpenAiCompatibleClient(httpClient, options, logger, options.StreamIdleTimeoutSeconds);
    });
    builder.Services.AddTransient<IDocumentAnalysisService, DocumentAnalysisService>();
}

// Repositories
builder.Services.AddScoped<IDocumentFileRepository, DocumentFileRepository>();
builder.Services.AddScoped<IDocumentParseRepository, DocumentParseRepository>();
builder.Services.AddScoped<IDocumentParseImageRepository, DocumentParseImageRepository>();
builder.Services.AddScoped<IDocumentParseBlockRepository, DocumentParseBlockRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// Domain Services
builder.Services.AddScoped<ISearchDomainService, SearchDomainService>();
builder.Services.AddScoped<IDocumentFileService, DocumentFileService>();
builder.Services.AddScoped<IDocumentParseService, DocumentParseService>();

// StructaDoc parsing service client (ADR-0009): named HttpClient + Singleton wrapper so the
// polling worker does not recreate the client (HttpClientFactory pools the handler).
builder.Services.Configure<StructaDocOptions>(builder.Configuration.GetSection(StructaDocOptions.SectionName));
builder.Services.AddHttpClient("StructaDoc", client =>
{
    var options = builder.Configuration.GetSection(StructaDocOptions.SectionName).Get<StructaDocOptions>()
        ?? new StructaDocOptions();
    if (!string.IsNullOrWhiteSpace(options.BaseUrl))
    {
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    }
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});
builder.Services.AddSingleton<IStructaDocClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var options = sp.GetRequiredService<IOptions<StructaDocOptions>>();
    var logger = sp.GetRequiredService<ILogger<StructaDocClient>>();
    return new StructaDocClient(factory.CreateClient("StructaDoc"), options, logger);
});

// Background Workers
builder.Services.AddScoped<IStructaDocParseResultSync, StructaDocParseResultSync>();
builder.Services.AddScoped<ParseImageContentSource>();
builder.Services.AddHostedService<StructaDocParseWorker>();

var app = builder.Build();
var identityTrust = app.Services
    .GetRequiredService<IOptions<IdentityAuthenticationOptions>>()
    .Value;

// Startup diagnostics and initialization run on the main thread outside any request scope.
// The retired global Serilog enrichers used to stamp every log line with identity; under the
// ServiceMantle pipeline identity comes from explicit scopes, so the whole startup region
// opens one ServiceLogContext scope (ServiceName/ServiceVersion/InstanceId, deliberately no
// HTTP CorrelationId) and disposes it before the request pipeline starts.
var serviceLogContext = app.Services.GetRequiredService<ServiceMantle.Web.Logging.ServiceLogContext>();
using (serviceLogContext.BeginScope(app.Logger))
{
    app.Logger.LogInformation("Doctheca Service starting");
    app.Logger.LogInformation(
        "Identity trust: Authority={Authority}, Issuers={Issuers}, Audience={Audience}, RequireHttpsMetadata={RequireHttpsMetadata}",
        identityTrust.Authority,
        string.Join(",", identityTrust.GetValidIssuers()),
        identityTrust.Audience,
        identityTrust.RequireHttpsMetadata);
    app.Logger.LogInformation("Endpoints: HTTP={HttpPort}", httpPort);
    app.Logger.LogInformation(
        "Consul startup diagnostics: Address={Address}, Token={Token}, Source={Source}, KeyCount={KeyCount}, Prefixes={Prefixes}, LastError={LastError}",
        $"{consulOptions.Host}:{consulOptions.Port}",
        StartupDiagnosticsFormatter.MaskSecret(consulOptions.Token),
        consulRuntimeState.Source,
        consulRuntimeState.KeyCount,
        StartupDiagnosticsFormatter.SummarizePrefixes(consulRuntimeState.LoadedPrefixes),
        StartupDiagnosticsFormatter.SummarizeError(consulRuntimeState.LastError));
    {
        var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
        app.Logger.LogInformation("Database: PostgreSQL {Host}:{Port}/{Database}", csb["Host"], csb.TryGetValue("Port", out var dbPort) ? dbPort : "5432", csb["Database"]);
    }
    app.Logger.LogInformation(
        "Effective configuration diagnostics: PostgreSqlHost={PostgreSqlHost}, PostgreSqlPort={PostgreSqlPort}, PostgreSqlUsername={PostgreSqlUsername}, PostgreSqlPassword={PostgreSqlPassword}, DatabaseName={DatabaseName}, LokiUri={LokiUri}, OpenSearchUrl={OpenSearchUrl}",
        StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Host"]),
        StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Port"]),
        StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["PostgreSql:Username"]),
        StartupDiagnosticsFormatter.SummarizePassword(builder.Configuration["PostgreSql:Password"]),
        StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["Database:Name"]),
        StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["Loki:Uri"]),
        StartupDiagnosticsFormatter.SummarizeValue(builder.Configuration["OpenSearch:Url"]));
    app.Logger.LogInformation("OSS: {OssType}", useLocalOss ? "local" : "S3");
    app.Logger.LogInformation("OpenSearch: {Url}", builder.Configuration["OpenSearch:Url"] ?? "(not configured)");

    var structaDocConfig = builder.Configuration.GetSection(StructaDocOptions.SectionName).Get<StructaDocOptions>()
        ?? new StructaDocOptions();
    app.Logger.LogInformation(
        "StructaDoc: BaseUrl={BaseUrl}, ApiKey={ApiKeyStatus}",
        structaDocConfig.BaseUrl ?? "(not configured)",
        string.IsNullOrEmpty(structaDocConfig.ApiKey) ? "(empty - parsing disabled)" : SensitiveDataMasker.MaskApiKey(structaDocConfig.ApiKey));

    // Log LLM configuration
    var llmApiKey = builder.Configuration["LlmDocumentAnalysis:ApiKey"];
    var llmBaseUrl = builder.Configuration["LlmDocumentAnalysis:BaseUrl"];
    var llmModel = builder.Configuration["LlmDocumentAnalysis:Model"];
    var llmContextLength = builder.Configuration["LlmDocumentAnalysis:ContextLength"];
    var llmEnabled = !string.IsNullOrEmpty(llmApiKey);
    app.Logger.LogInformation(
        "LLM Document Analysis: Enabled={Enabled}, BaseUrl={BaseUrl}, Model={Model}, ContextLength={ContextLength}, ApiKey={ApiKeyStatus}",
        llmEnabled,
        llmBaseUrl ?? "(not configured)",
        llmModel ?? "(not configured)",
        llmContextLength ?? "(not configured)",
        string.IsNullOrEmpty(llmApiKey) ? "(empty - service disabled)" : SensitiveDataMasker.MaskApiKey(llmApiKey));

    // Initialize LLM document analysis at startup
    if (llmEnabled)
    {
        using var llmScope = app.Services.CreateScope();
        var llmService = llmScope.ServiceProvider.GetService<IDocumentAnalysisService>();
        if (llmService != null)
        {
            try
            {
                await llmService.InitializeAsync();
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "LLM document analysis initialization failed at startup");
            }
        }
    }

    using (var scope = app.Services.CreateScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        await DatabaseInitializer.InitializeAsync(dbContext, loggerFactory);
        // One-way process-local receipt: only a successful initializer return may publish
        // migrationStatus=Succeeded later, and only together with a passing read-only schema
        // probe (the initializer swallows some ALTER failures, so this alone is not proof).
        scope.ServiceProvider.GetRequiredService<DocthecaStartupReceipt>()
            .MarkInitializationCompleted();
    }

    // Initialize search indices
    using (var initScope = app.Services.CreateScope())
    {
        var searchIndexService = initScope.ServiceProvider.GetRequiredService<ISearchIndexService>();
        var initLogger = initScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        try
        {
            await searchIndexService.EnsureIndexAsync();
            initLogger.LogInformation("Search index initialization completed");
        }
        catch (Exception ex)
        {
            initLogger.LogWarning(ex, "Search index initialization failed");
        }
    }
}

// First middleware in the pipeline: everything registered below (static files, the SPA
// fallback, authentication, and every API/image/export response) runs inside the
// ServiceMantle request scope and receives the x-correlation-id response header, injected via
// OnStarting before any response starts (static file responses included).
app.UseServiceMantleCorrelationId();

// Explicit routing ahead of the security-header middleware so the matched endpoint's marker
// metadata is resolvable there; inserting the middleware before authentication/authorization
// also covers 401/403 challenge responses of marked endpoints. The correlation id stays the
// outermost middleware, and static files/SPA fallback carry no marker so their rendering
// contracts are untouched.
app.UseRouting();
app.UseServiceMantleSecurityResponseHeaders();

// Safe Problem Details boundary for the marked JSON admin endpoints only: the UseWhen branch
// runs exactly for endpoints carrying the RequireServiceMantleSecurityResponseHeaders marker
// resolved by the explicit UseRouting above, so static files, the SPA fallback, health, image
// proxies, and HTML/ZIP exports never enter it (the library swallows exceptions once a
// response has started, which streaming/rendering contracts must not inherit). The branch
// builder is an independent IApplicationBuilder instance, so this composition does not
// conflict with the library's PipelineComposition rules for UseServiceMantlePipeline; the
// security-header middleware stays outside the branch and writes its six headers via
// OnStarting, so problem responses carry the same single-value baseline.
app.UseWhen(
    context => context.GetEndpoint()?.Metadata
        .GetMetadata<SecurityResponseHeadersMetadata>() is not null,
    branch => branch.UseServiceMantleProblemDetails());

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();

// Cookie-session boundary for the protected admin API (hosted login): expired sessions get the
// fixed re-authentication result before any endpoint runs, and non-safe methods must carry the
// CSRF credential. Bearer callers and the disabled configuration bypass the boundary entirely.
app.UseMiddleware<AdminOidcSessionMiddleware>();

app.UseAuthorization();

app.MapAdminAuthEndpoints();
app.MapAdminOidcEndpoints();

// Web Admin API endpoints
app.MapDocumentFileEndpoints();
app.MapDocumentParseEndpoints();
app.MapDocumentExportEndpoints();
app.MapDocumentSearchEndpoints();

// ServiceMantle fixed health endpoints: GET /health/live (liveness, never touches the
// database), GET /health/ready and the GET /health alias (readiness projection). All three
// are anonymous and mapped ahead of the SPA fallback, so they always answer JSON. Note the
// deliberate public contract change: /health is now a readiness alias (503 while not ready)
// instead of the old always-healthy liveness payload with a timestamp.
app.MapGroup(string.Empty)
    .AllowAnonymous()
    .MapServiceMantleHealthEndpoints();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Exposes the minimal-hosting entry point to WebApplicationFactory<Program> for the
// container-backed integration tests in src/Tests/Doctheca.Tests/Integration.
public partial class Program
{
}
