using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocRetrieval.Database;
using Ruoyu.Study.DocRetrieval.Database.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Ruoyu.Study.DocRetrieval.Service;
using QuantumZhou.Identity.Client;
using IHttpClientFactory = System.Net.Http.IHttpClientFactory;

var builder = WebApplication.CreateBuilder(args);

var grpcPort = builder.Configuration.GetValue<int?>("Endpoints:Grpc") ?? 5011;
var httpPort = builder.Configuration.GetValue<int?>("Endpoints:Http") ?? 5012;

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(grpcPort, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });

    options.ListenAnyIP(httpPort, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });
});

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 200 * 1024 * 1024;
    options.MaxSendMessageSize = 200 * 1024 * 1024;
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 200 * 1024 * 1024;
});

var connectionString = builder.Configuration.GetConnectionString("Default");
var isPostgreSql = !string.IsNullOrWhiteSpace(connectionString)
    && (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
        || connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase));
builder.Services.AddDbContext<DocRetrievalDbContext>(options =>
{
    if (isPostgreSql)
        options.UseNpgsql(connectionString);
    else
        options.UseSqlite(connectionString ?? "Data Source=data/sqlite/ruoyu_study_docretrieval.db");
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
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// Domain Services
builder.Services.AddScoped<IDocumentDomainService, DocumentDomainService>();
builder.Services.AddScoped<ISearchDomainService, SearchDomainService>();
builder.Services.AddScoped<IDocumentParserService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<DocumentParserService>>();
    var llmSegmentation = sp.GetService<ILlmSegmentationService>(); // Optional
    return new DocumentParserService(logger, llmSegmentation);
});

// MinerU Precision API Client
builder.Services.Configure<MinerUOptions>(builder.Configuration.GetSection(MinerUOptions.SectionName));
builder.Services.AddSingleton<MinerUPrecisionClient>();

// Background Workers
builder.Services.AddHostedService<IngestionWorker>();

// ========== Identity Client SDK ==========
builder.Services.AddIdentityClient(builder.Configuration);

var app = builder.Build();

app.Logger.LogInformation("DocRetrieval Service starting");
app.Logger.LogInformation("Endpoints: gRPC={GrpcPort}, HTTP={HttpPort}", grpcPort, httpPort);
if (isPostgreSql && !string.IsNullOrEmpty(connectionString))
{
    var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
    app.Logger.LogInformation("Database: PostgreSQL {Host}:{Port}/{Database}", csb["Host"], csb.TryGetValue("Port", out var dbPort) ? dbPort : "5432", csb["Database"]);
}
else
{
    app.Logger.LogInformation("Database: SQLite");
}
app.Logger.LogInformation("OSS: {OssType}", useLocalOss ? "local" : "S3");
app.Logger.LogInformation("OpenSearch: {Url}", builder.Configuration["OpenSearch:Url"] ?? "(not configured)");

var identityOptions = app.Services.GetRequiredService<IdentityClientOptions>();
app.Logger.LogInformation("Identity: gRPC={GrpcEndpoint}, JWKS={JwksEndpoint}, RequireHttps={RequireHttps}",
    identityOptions.GrpcEndpoint, identityOptions.JwksEndpoint, identityOptions.RequireHttpsForJwks);

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
    string.IsNullOrEmpty(llmApiKey) ? "(empty - service disabled)" : "(configured)");

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
    var dbContext = scope.ServiceProvider.GetRequiredService<DocRetrievalDbContext>();
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

app.MapGrpcService<DocumentRetrievalServiceImpl>();

app.UseIdentityClient();

app.UseDefaultFiles();
app.UseStaticFiles();

// Web Admin API endpoints
app.MapIdentityAuthEndpoints();
app.MapDocumentAdminEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));
app.MapFallbackToFile("index.html");

app.Run();
