using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocRetrieval.Database;
using Ruoyu.Study.DocRetrieval.Database.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Ruoyu.Study.DocRetrieval.Service;

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
        return new S3OssService(options.Endpoint, options.AccessKey, options.SecretKey, options.BucketName);
    });
}

// Search Index Services
builder.Services.Configure<OpenSearchOptions>(builder.Configuration.GetSection("OpenSearch"));
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection("Qdrant"));
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection("Embedding"));
builder.Services.AddSingleton<ISearchIndexService, OpenSearchIndexService>();
builder.Services.AddSingleton<IQdrantService, QdrantService>();

// Repositories
builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
builder.Services.AddScoped<IDocumentPageRepository, DocumentPageRepository>();
builder.Services.AddScoped<IDocumentSegmentRepository, DocumentSegmentRepository>();
builder.Services.AddScoped<IQuestionSegmentRepository, QuestionSegmentRepository>();
builder.Services.AddScoped<IDocumentOccurrenceRepository, DocumentOccurrenceRepository>();
builder.Services.AddScoped<IDocumentIngestionJobRepository, DocumentIngestionJobRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// Domain Services
builder.Services.AddScoped<DocumentDomainService>();
builder.Services.AddScoped<ISearchDomainService, SearchDomainService>();
builder.Services.AddScoped<IDocumentParserService, DocumentParserService>();

// Background Workers
builder.Services.AddHostedService<IngestionWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<DocRetrievalDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DatabaseInitializer.InitializeAsync(dbContext, logger);
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

// Initialize Qdrant collection
using (var initScope = app.Services.CreateScope())
{
    var qdrantService = initScope.ServiceProvider.GetRequiredService<IQdrantService>();
    var initLogger = initScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await qdrantService.EnsureCollectionAsync();
        initLogger.LogInformation("Qdrant collection initialization completed");
    }
    catch (Exception ex)
    {
        initLogger.LogWarning(ex, "Qdrant collection initialization failed, semantic search will be unavailable");
    }
}

app.MapGrpcService<DocumentRetrievalServiceImpl>();

app.UseDefaultFiles();
app.UseStaticFiles();

// Web Admin API endpoints
app.MapDocumentAdminEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));
app.MapFallbackToFile("index.html");

app.Run();
