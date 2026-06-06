using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocRetrieval.Database;
using Ruoyu.Study.DocRetrieval.Database.Repositories;
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
builder.Services.AddScoped<SearchDomainService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<DocRetrievalDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DatabaseInitializer.InitializeAsync(dbContext, logger);
}

app.MapGrpcService<DocumentRetrievalServiceImpl>();

// Web Admin API endpoints
app.MapDocumentAdminEndpoints();

app.MapGet("/", () => "DocRetrieval gRPC host is running.");

app.Run();
