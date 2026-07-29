using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Ruoyu.Study.Common.Ai;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Database;
using Ruoyu.Study.DocLibrary.Database.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Ruoyu.Study.DocLibrary.Service;
using Ruoyu.Study.DocLibrary.Service.Parsing;
using Ruoyu.Study.DocLibrary.Host;
using Ruoyu.Study.DocLibrary.Host.Authentication;
using Ruoyu.Study.Consul.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddRuoyuConsulConfiguration(builder.Configuration);

// ========== Serilog (Console + Grafana Loki) ==========
builder.Configuration.AddRuoyuLokiSink();
builder.Host.UseRuoyuSerilog("Ruoyu.Study.DocLibrary");

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

builder.Services.Configure<IdentityServiceOptions>(
    builder.Configuration.GetSection(IdentityServiceOptions.SectionName));
builder.Services.Configure<DocLibraryCookieOptions>(
    builder.Configuration.GetSection(DocLibraryCookieOptions.SectionName));
builder.Services.Configure<InternalAuthOptions>(
    builder.Configuration.GetSection(InternalAuthOptions.SectionName));
builder.Services.AddHttpClient<IIdentityAuthenticationService, IdentityAuthenticationService>(
    (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<IdentityServiceOptions>>().Value;
        client.BaseAddress = new Uri(options.Authority.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
builder.Services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<IdentityServiceOptions>>().Value;
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

var identityOptions = builder.Configuration
    .GetSection(IdentityServiceOptions.SectionName)
    .Get<IdentityServiceOptions>() ?? new IdentityServiceOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = identityOptions.Authority;
        options.Audience = identityOptions.Audience;
        options.RequireHttpsMetadata = identityOptions.RequireHttpsMetadata;
        options.MapInboundClaims = false;
        options.TokenValidationParameters =
            IdentityTokenValidator.CreateValidationParameters(identityOptions);
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrWhiteSpace(context.Token))
                {
                    context.Token = context.Request.Cookies[
                        DocLibraryAuthenticationConstants.AccessCookieName];
                }
                return Task.CompletedTask;
            }
        };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
        QuestionBankServiceKeyAuthenticationHandler>(
        DocLibraryAuthenticationConstants.QuestionBankScheme,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(DocLibraryAuthorizationPolicies.Admin, policy =>
    {
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
            context.User.Identities.Any(identity => identity.IsAuthenticated)
            && context.User.Claims.Any(claim =>
                claim.Type is "role" or ClaimTypes.Role
                && string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase)));
    });
    options.AddPolicy(DocLibraryAuthorizationPolicies.QuestionBank, policy =>
    {
        policy.AddAuthenticationSchemes(
            DocLibraryAuthenticationConstants.QuestionBankScheme);
        policy.RequireAuthenticatedUser();
    });
});

var fallbackConnectionString = builder.Configuration.GetConnectionString("Default");
var connectionString = SharedPostgreSqlConnectionStringFactory.BuildOrFallback(builder.Configuration, fallbackConnectionString)
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured");
builder.Services.AddDbContext<DocLibraryDbContext>(options =>
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
        return new S3OssService(options.Endpoint, options.AccessKey, options.SecretKey, options.BucketName,
            publicEndpoint: options.PublicEndpoint);
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
builder.Services.AddScoped<IDocumentParseBlockService, DocumentParseBlockService>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// Domain Services
builder.Services.AddScoped<ISearchDomainService, SearchDomainService>();
builder.Services.AddScoped<IDocumentFileService, DocumentFileService>();
builder.Services.AddScoped<IDocumentParseService, DocumentParseService>();
builder.Services.AddScoped<IQuestionBankImportService, QuestionBankImportService>();

// MinerU Precision API Client
builder.Services.Configure<MinerUOptions>(builder.Configuration.GetSection(MinerUOptions.SectionName));
builder.Services.Configure<FileConversionOptions>(builder.Configuration.GetSection(FileConversionOptions.SectionName));

// FileConversion: 命名 HttpClient + Singleton 包装。
// AddHttpClient<TInterface, TImplementation>() 默认注册为 Transient，MinerUFileParseWorker 每 5 秒
// CreateScope().GetRequiredService<IFileConversionService>() 会重复 new 实例（构造函数日志被重复打印）。
// 改用 AddHttpClient("FileConversion") 注册命名 HttpClient（HttpClientFactory 池化 Handler），
// 再用 AddSingleton<IFileConversionService> 工厂方式包装，确保真正的 Singleton 生命周期。
builder.Services.AddHttpClient("FileConversion", client =>
{
    var url = builder.Configuration["FileConversionService:Url"] ?? "http://doc-converter:5050";
    client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(180);
});
builder.Services.AddSingleton<IFileConversionService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var options = sp.GetRequiredService<IOptions<FileConversionOptions>>();
    var logger = sp.GetRequiredService<ILogger<RemoteFileConversionService>>();
    return new RemoteFileConversionService(factory.CreateClient("FileConversion"), options, logger);
});

builder.Services.AddSingleton<MinerUPrecisionClient>();
builder.Services.AddSingleton<IPdfSplitService, PdfSplitService>();

// Background Workers
builder.Services.AddScoped<MinerUParseOrchestrator>();
builder.Services.AddScoped<MinerUResultPersistence>();
builder.Services.AddHostedService<MinerUFileParseWorker>();

var app = builder.Build();

app.Logger.LogInformation("DocLibrary Service starting");
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
    var dbContext = scope.ServiceProvider.GetRequiredService<DocLibraryDbContext>();
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    await DatabaseInitializer.InitializeAsync(dbContext, loggerFactory);
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

app.UseMiddleware<CorrelationIdMiddleware>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapAdminAuthEndpoints();

// Web Admin API endpoints
app.MapDocumentFileEndpoints();
app.MapDocumentParseEndpoints();
app.MapQuestionBankImportEndpoints();
app.MapDocumentExportEndpoints();
app.MapDocumentSearchEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));
app.MapFallbackToFile("index.html");

app.Run();
