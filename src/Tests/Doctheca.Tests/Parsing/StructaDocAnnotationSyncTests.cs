using System.Net;
using System.Text;
using System.Text.Json;
using Doctheca.Database;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Service;
using Doctheca.Service.Parsing;
using Doctheca.Service.StructaDoc;
using Doctheca.Tests.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ServiceMantle;
using ServiceMantle.Web.Logging;
using Xunit;

namespace Doctheca.Tests.Parsing;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class StructaDocAnnotationSyncTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private const string Caption = "  图题 café\r\n<script>plain text</script>  ";
    private const string Footnote = "  注释 α\n第二行\t  ";
    private readonly Guid _run = Guid.NewGuid();
    private string _database = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        using var context = fixture.CreateContext(_database);
        await new DocthecaMigrationExecutor(context).ExecuteAsync();
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        await context.DocumentParses.ExecuteUpdateAsync(update => update
            .SetProperty(parse => parse.Status, DocumentParseStatus.Parsing)
            .SetProperty(parse => parse.StructaDocParseRunId, _run)
            .SetProperty(parse => parse.ParsedAt, (DateTimeOffset?)null));
    }

    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task RealClientAndSync_RoundTripExactAnnotationsThroughBothRepositoryMappings()
    {
        using var context = fixture.CreateContext(_database);
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var client = Client(loggerFactory, _ => Task.FromResult(Json(BlocksJson())));
        var sync = Sync(context, client, loggerFactory);
        await sync.SyncAsync(Model(), _run, default);
        await sync.SyncAsync(Model(), _run, default);
        context.ChangeTracker.Clear();

        var repository = new DocumentParseBlockRepository(context);
        var all = await repository.GetByParseIdAsync(MigrationGoldenStates.ParseId);
        all.Should().HaveCount(4);
        foreach (var read in new[] { all,
                     await repository.GetByParseAndPageAsync(MigrationGoldenStates.ParseId, 0),
                     await repository.GetPageBySequenceAsync(MigrationGoldenStates.ParseId, null, 100) })
        {
            var image = read.Single(block => block.Sequence == 0);
            image.BlockType.Should().Be("image");
            image.TextContent.Should().BeNull();
            image.Caption.Should().Be(Caption);
            image.Footnote.Should().Be(Footnote);
            using var data = JsonDocument.Parse(image.BlockData);
            data.RootElement.GetProperty("caption").GetString().Should().Be(Caption);
            data.RootElement.GetProperty("footnote").GetString().Should().Be(Footnote);
            read.Single(block => block.Sequence == 1).Caption.Should().Be("");
            read.Single(block => block.Sequence == 1).Footnote.Should().Be("");
            read.Single(block => block.Sequence == 2).Caption.Should().BeNull();
            read.Single(block => block.Sequence == 2).Footnote.Should().BeNull();
            read.Single(block => block.Sequence == 3).Caption.Should().Be(" \n\t");
            read.Single(block => block.Sequence == 3).Footnote.Should().Be("\r\n ");
        }
        var rows = await context.DocumentParseBlocks.AsNoTracking().OrderBy(block => block.Sequence).ToListAsync();
        rows[0].Caption.Should().Be(Caption);
        rows[0].Footnote.Should().Be(Footnote);
        rows[2].Footnote.Should().BeNull();
        (await context.DocumentParses.SingleAsync()).Status.Should().Be(DocumentParseStatus.Parsed);
        string.Join('\n', logs.Messages).Should().NotContain(Caption).And.NotContain(Footnote);
    }

    [Fact]
    public async Task CancelledClientPagination_DoesNotPersistPartialAnnotationsOrPublishParsedStatus()
    {
        using var context = fixture.CreateContext(_database);
        using var cancellation = new CancellationTokenSource();
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var calls = 0;
        var client = Client(loggerFactory, _ =>
        {
            if (++calls == 1) return Task.FromResult(Json(BlocksJson(nextSequence: 3)));
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Sync(context, client, loggerFactory).SyncAsync(Model(), _run, cancellation.Token));
        context.ChangeTracker.Clear();
        var block = await context.DocumentParseBlocks.SingleAsync();
        block.TextContent.Should().Be("legacy text");
        block.Caption.Should().BeNull();
        block.Footnote.Should().BeNull();
        var parse = await context.DocumentParses.SingleAsync();
        parse.Status.Should().Be(DocumentParseStatus.Parsing);
        parse.ParsedAt.Should().BeNull();
        string.Join('\n', logs.Messages).Should().NotContain(Caption).And.NotContain(Footnote);
    }

    [Fact]
    public async Task IndexFailure_AfterActualSync_RetainsParsedAnnotationsAndSafeDiagnostics()
    {
        using var context = fixture.CreateContext(_database);
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var client = Client(loggerFactory, _ => Task.FromResult(Json(BlocksJson())));
        var fileService = new Mock<IDocumentFileService>();
        fileService.Setup(service => service.GetByIdAsync(MigrationGoldenStates.FileId)).ReturnsAsync(new DocumentFileModel
        {
            Id = MigrationGoldenStates.FileId, FileName = "synthetic.pdf", Subject = "math", Grade = "g7", Year = "2025"
        });
        var index = new Mock<ISearchIndexService>();
        index.Setup(service => service.IndexParseBlocksAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("synthetic-provider-canary " + Caption + Footnote));
        using var services = new ServiceCollection().AddSingleton(client).AddSingleton(fileService.Object)
            .AddSingleton<IStructaDocParseResultSync>(Sync(context, client, loggerFactory))
            .AddSingleton(index.Object).AddSingleton<IDocumentParseService>(ParseService(context, loggerFactory))
            .BuildServiceProvider();
        var worker = new StructaDocParseWorker(services, loggerFactory.CreateLogger<StructaDocParseWorker>(),
            new ServiceLogContext(ServiceId.Parse("doctheca"), InstanceId.Parse("annotation-test"), "1.0.0"));
        await worker.PollAsync(Model(), services, default);
        var reindex = new ParseSearchIndexer(ParseService(context, loggerFactory), fileService.Object,
            index.Object, loggerFactory.CreateLogger<ParseSearchIndexer>());
        (await reindex.ReindexAsync(MigrationGoldenStates.ParseId)).Should().BeFalse();
        (await reindex.ReindexAllAsync(default)).Should().Be((1, 0));
        context.ChangeTracker.Clear();
        (await context.DocumentParses.SingleAsync()).Status.Should().Be(DocumentParseStatus.Parsed);
        (await context.DocumentParseBlocks.SingleAsync(block => block.Sequence == 0)).Footnote.Should().Be(Footnote);
        var diagnostics = string.Join('\n', logs.Messages);
        diagnostics.Should().Contain("Failed to index parse").And.Contain("Failed to re-index parse").And.NotContain("synthetic-provider-canary")
            .And.NotContain(Caption).And.NotContain(Footnote);
    }

    private DocumentParseModel Model() => new()
    {
        Id = MigrationGoldenStates.ParseId, DocumentFileId = MigrationGoldenStates.FileId,
        Status = DocumentParseStatus.Parsing, StructaDocParseRunId = _run
    };

    private static DocumentParseService ParseService(DocthecaDbContext context, ILoggerFactory loggerFactory) =>
        new(new DocumentParseRepository(context), new DocumentParseImageRepository(context), loggerFactory.CreateLogger<DocumentParseService>());

    private static StructaDocParseResultSync Sync(DocthecaDbContext context, IStructaDocClient client, ILoggerFactory loggerFactory) =>
        new(client, ParseService(context, loggerFactory), new DocumentParseBlockRepository(context),
            new DocumentParseImageRepository(context), new DocumentParsePageRepository(context), loggerFactory.CreateLogger<StructaDocParseResultSync>());

    private IStructaDocClient Client(ILoggerFactory loggerFactory, Func<CancellationToken, Task<HttpResponseMessage>> blocks) =>
        new StructaDocClient(new HttpClient(new Handler((request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/blocks", StringComparison.Ordinal)) return blocks(token);
            if (path.EndsWith("/assets", StringComparison.Ordinal)) return Task.FromResult(Json("[]"));
            if (path.EndsWith("/pages", StringComparison.Ordinal)) return Task.FromResult(Json("[{\"number\":1}]"));
            if (path.EndsWith("/markdown", StringComparison.Ordinal)) return Task.FromResult(Json("# synthetic"));
            return Task.FromResult(Json(JsonSerializer.Serialize(new { id = _run, status = "succeeded" })));
        })) { BaseAddress = new Uri("http://annotations.test/") }, Options.Create(new StructaDocOptions()), loggerFactory.CreateLogger<StructaDocClient>());

    private static string BlocksJson(int? nextSequence = null) => JsonSerializer.Serialize(new
    {
        items = new object[]
        {
            new { sequence = 0, pageNumber = 1, type = "image", content = (string?)null, caption = Caption, footnote = Footnote },
            new { sequence = 1, pageNumber = 1, type = "table", caption = "", footnote = "" },
            new { sequence = 2, pageNumber = 1, type = "text", content = "older body" },
            new { sequence = 3, pageNumber = 1, type = "image", caption = " \n\t", footnote = "\r\n " }
        }, nextSequence
    });

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
