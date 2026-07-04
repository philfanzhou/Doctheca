using System.Data.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Database;
using Ruoyu.Study.DocLibrary.Database.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Ruoyu.Study.DocLibrary.Service;
using Ruoyu.Study.DocLibrary.Host;

var builder = WebApplication.CreateBuilder(args);

// ========== Serilog (Console + Grafana Loki) ==========
// LOKI_URI environment variable injects Loki address (overrides appsettings.json fallback).
// Loki Sink throws ArgumentNullException when uri is null; fallback uri in config ensures startup.
// Loki unreachable: Sink retries asynchronously, does not affect service.
var lokiUri = Environment.GetEnvironmentVariable("LOKI_URI");
if (!string.IsNullOrWhiteSpace(lokiUri))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Serilog:WriteTo:1:Args:uri"] = lokiUri
    });
}
builder.Host.UseAgentSerilog("Ruoyu.Study.DocLibrary");

var httpPort = builder.Configuration.GetValue<int?>("Endpoints:Http") ?? 5012;

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(httpPort);
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 200 * 1024 * 1024;
});

var connectionString = builder.Configuration.GetConnectionString("Default")
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

// LLM Segmentation Services (optional, falls back to rule-based if not configured)
var llmSection = builder.Configuration.GetSection(LlmSegmentationOptions.SectionName);
if (llmSection.Exists() && !string.IsNullOrEmpty(llmSection["ApiKey"]))
{
    builder.Services.Configure<LlmSegmentationOptions>(llmSection);
    builder.Services.AddHttpClient<ILlmSegmentationService, LlmSegmentationService>();
}

// Repositories
builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
builder.Services.AddScoped<IDocumentPageRepository, DocumentPageRepository>();
builder.Services.AddScoped<IDocumentSegmentRepository, DocumentSegmentRepository>();
builder.Services.AddScoped<IQuestionSegmentRepository, QuestionSegmentRepository>();
builder.Services.AddScoped<IDocumentOccurrenceRepository, DocumentOccurrenceRepository>();
builder.Services.AddScoped<IDocumentIngestionJobRepository, DocumentIngestionJobRepository>();
builder.Services.AddScoped<IDocumentFileRepository, DocumentFileRepository>();
builder.Services.AddScoped<IDocumentParseRepository, DocumentParseRepository>();
builder.Services.AddScoped<IDocumentParseImageRepository, DocumentParseImageRepository>();
builder.Services.AddScoped<IDocumentParseBlockRepository, DocumentParseBlockRepository>();
builder.Services.AddScoped<IDocumentParseBlockService, DocumentParseBlockService>();
builder.Services.AddScoped<IDocumentParseImportRepository, DocumentParseImportRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// Domain Services
builder.Services.AddScoped<IDocumentDomainService, DocumentDomainService>();
builder.Services.AddScoped<ISearchDomainService, SearchDomainService>();
builder.Services.AddScoped<IDocumentFileService, DocumentFileService>();
builder.Services.AddScoped<IDocumentParseService, DocumentParseService>();
builder.Services.AddScoped<IDocumentParserService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<DocumentParserService>>();
    var llmSegmentation = sp.GetService<ILlmSegmentationService>(); // Optional
    return new DocumentParserService(logger, llmSegmentation);
});

// MinerU Precision API Client
builder.Services.Configure<MinerUOptions>(builder.Configuration.GetSection(MinerUOptions.SectionName));
builder.Services.AddSingleton<MinerUPrecisionClient>();
builder.Services.AddSingleton<IPdfSplitService, PdfSplitService>();
builder.Services.AddSingleton<IFileConversionService, LibreOfficeConversionService>();

// Background Workers
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<MinerUFileParseWorker>();

// ========== Authentication & Authorization ==========
// Identity 去 gRPC Phase 1: DocLibrary 不再引用 QuantumZhou.Identity.Client SDK。
// JWT Bearer 使用 OIDC Authority 方式，自动从 Identity 的 /.well-known/openid-configuration 发现 JWKS 端点。
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["IdentityService:Authority"];
        options.Audience = builder.Configuration["Jwt:Audience"] ?? "QuantumZhou.microservices";
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "QuantumZhou.Identity",
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

// ========== Identity HTTP Client ==========
// 用于调用 Identity 的 HTTP 端点 POST /api/auth/token（login / refresh）。
// AppId / AppSecret 在 AuthEndpoints 中按需通过 X-Admin-AppId / X-Admin-AppSecret 头传递。
builder.Services.AddHttpClient("IdentityService", client =>
{
    var authority = builder.Configuration["IdentityService:Authority"] ?? "http://localhost:5002";
    client.BaseAddress = new Uri(authority);
    client.Timeout = TimeSpan.FromSeconds(10);
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

var app = builder.Build();

app.Logger.LogInformation("DocLibrary Service starting");
app.Logger.LogInformation("Endpoints: HTTP={HttpPort}", httpPort);
{
    var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
    app.Logger.LogInformation("Database: PostgreSQL {Host}:{Port}/{Database}", csb["Host"], csb.TryGetValue("Port", out var dbPort) ? dbPort : "5432", csb["Database"]);
}
app.Logger.LogInformation("OSS: {OssType}", useLocalOss ? "local" : "S3");
app.Logger.LogInformation("OpenSearch: {Url}", builder.Configuration["OpenSearch:Url"] ?? "(not configured)");
app.Logger.LogInformation("Identity: Authority={Authority}",
    builder.Configuration["IdentityService:Authority"] ?? "(not configured)");

// Log LLM configuration
var llmApiKey = builder.Configuration["LlmSegmentation:ApiKey"];
var llmBaseUrl = builder.Configuration["LlmSegmentation:BaseUrl"];
var llmModel = builder.Configuration["LlmSegmentation:Model"];
var llmContextLength = builder.Configuration["LlmSegmentation:ContextLength"];
var llmMaxTokens = builder.Configuration["LlmSegmentation:MaxTokens"];
var llmEnabled = !string.IsNullOrEmpty(llmApiKey);
app.Logger.LogInformation(
    "LLM Segmentation: Enabled={Enabled}, BaseUrl={BaseUrl}, Model={Model}, ContextLength={ContextLength}, MaxTokens={MaxTokens}, ApiKey={ApiKeyStatus}",
    llmEnabled,
    llmBaseUrl ?? "(not configured)",
    llmModel ?? "(not configured)",
    llmContextLength ?? "(not configured)",
    llmMaxTokens ?? "4K (default)",
    string.IsNullOrEmpty(llmApiKey) ? "(empty - service disabled)" : SensitiveDataMasker.MaskApiKey(llmApiKey));

// Initialize LLM segmentation at startup (verify config, compute ChunkSize)
if (llmEnabled)
{
    using var llmScope = app.Services.CreateScope();
    var llmService = llmScope.ServiceProvider.GetService<ILlmSegmentationService>();
    if (llmService != null)
    {
        try
        {
            await llmService.InitializeAsync();
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "LLM segmentation initialization failed at startup");
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
        initLogger.LogWarning(ex, "Search index initialization failed, will use database fallback search");
    }
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();

// Web Admin API endpoints
app.MapAuthEndpoints();
app.MapDocumentFileEndpoints();
app.MapDocumentParseEndpoints();
app.MapQuestionBankImportEndpoints();
app.MapDocumentExportEndpoints();
app.MapDocumentSearchEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));
app.MapFallbackToFile("index.html");

app.Run();
