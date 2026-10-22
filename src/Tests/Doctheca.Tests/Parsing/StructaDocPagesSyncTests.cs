using System.Net;
using System.Text;
using Doctheca.Database;
using Doctheca.Database.Repositories;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Service.Parsing;
using Doctheca.Service.StructaDoc;
using Doctheca.Tests.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Doctheca.Tests.Parsing;

[Collection(MigrationIntegrationCollection.Name)]
public sealed class StructaDocPagesSyncTests(PostgreSqlMigrationFixture fixture) : IAsyncLifetime
{
    private string _database = null!;
    private static readonly Guid Parse = MigrationGoldenStates.ParseId;
    private readonly Guid _run = Guid.NewGuid();
    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        using var context = fixture.CreateContext(_database);
        await new DocthecaMigrationExecutor(context).ExecuteAsync();
        await MigrationGoldenStates.SeedBusinessRowsAsync(context);
        await context.DocumentParses.Where(parse => parse.Id == Parse)
            .ExecuteUpdateAsync(update => update.SetProperty(parse => parse.Status, DocumentParseStatus.Parsing)
                .SetProperty(parse => parse.StructaDocParseRunId, _run).SetProperty(parse => parse.ParsedAt, (DateTimeOffset?)null));
    }
    public Task DisposeAsync() => fixture.DropDatabaseAsync(_database);

    [Fact]
    public async Task FullSync_PersistsOpaquePagesAndNormalizedNullVersusFirstPageProvenance()
    {
        using var context = fixture.CreateContext(_database);
        var logger = new CaptureLogger();
        var sync = Sync(context, _ => Task.FromResult(Json("""
            [{"number":1,"label":"iv","labelSource":"pdf-page-labels"},
             {"number":2,"label":"iv","labelSource":"pdf-page-labels"},
             {"number":3,"label":"封面\n一","labelSource":"page-number-blocks"},
             {"number":4},{"number":5,"label":"","labelSource":""}]
            """)), logger);
        await sync.SyncAsync(Model(), _run, default);
        await sync.SyncAsync(Model(), _run, default);
        var parse = await new DocumentParseRepository(context).GetByIdAsync(Parse);
        parse!.Status.Should().Be(DocumentParseStatus.Parsed);
        parse.MarkdownContent.Should().Be("# synthetic markdown");
        parse.StructaDocParseRunId.Should().Be(_run);
        var blocks = await new DocumentParseBlockRepository(context).GetByParseIdAsync(Parse);
        blocks.Should().HaveCount(3);
        var first = blocks.Single(block => block.Sequence == 0);
        var unknown = blocks.Single(block => block.Sequence == 1);
        first.PageId.Should().Be(0);
        unknown.PageId.Should().Be(0);
        DocumentParsePageIdentity.FromBlock(parse, first).Should().Be(new DocumentParsePageKey(Parse, 1));
        DocumentParsePageIdentity.FromBlock(parse, unknown).Should().BeNull();
        var pages = await new DocumentParsePageRepository(context).ReadBatchAsync(Enumerable.Range(1, 5).Select(number => new DocumentParsePageKey(Parse, number)).ToArray());
        pages.OrderBy(page => page.Key.PageNumber).Select(page => page.Value.Label).Should().Equal("iv", "iv", "封面\n一", null, "");
        logger.Messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("json")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("negative")]
    [InlineData("overflow")]
    [InlineData("missing-number")]
    public async Task OptionalFetchFailure_AtomicallyClearsStaleLabelsAndStillPublishesCoreResult(string failure)
    {
        using var context = fixture.CreateContext(_database);
        var repository = new DocumentParsePageRepository(context);
        await repository.ReplaceAsync(Parse, [new(Parse, 1, "stale", null), new(Parse, 2, "stale", null)]);
        var logger = new CaptureLogger();
        var sync = Sync(context, _ => failure switch
        {
            "http" => Task.FromResult(Json("synthetic-upstream-canary", HttpStatusCode.ServiceUnavailable)),
            "network" => throw new HttpRequestException("synthetic-upstream-canary"),
            "timeout" => throw new TaskCanceledException("synthetic-upstream-canary"),
            "json" => Task.FromResult(Json("{synthetic-upstream-canary")),
            "null" => Task.FromResult(Json("null")),
            "empty" => Task.FromResult(Json("[]")),
            "duplicate" => Task.FromResult(Json("[{\"number\":1},{\"number\":1}]")),
            "negative" => Task.FromResult(Json("[{\"number\":-1}]")),
            "overflow" => Task.FromResult(Json("[{\"number\":2147483648}]")),
            _ => Task.FromResult(Json("[{\"label\":\"synthetic-upstream-canary\"}]")),
        }, logger);
        await sync.SyncAsync(Model(), _run, default);
        (await repository.ReadBatchAsync([new(Parse, 1), new(Parse, 2)])).Should().BeEmpty();
        (await context.DocumentParseBlocks.CountAsync(block => block.ParseId == Parse)).Should().Be(3);
        (await new DocumentParseRepository(context).GetByIdAsync(Parse))!.Status.Should().Be(DocumentParseStatus.Parsed);
        logger.Messages.Should().ContainSingle().Which.Should().NotContain("synthetic-upstream-canary").And.NotContain("stale");
        logger.Exceptions.Should().OnlyContain(exception => exception == null);
    }

    [Fact]
    public async Task LocalTransactionFailure_RollsBackAndEscapesThroughSafeRetryBoundaryWithoutParsedStatus()
    {
        using var context = fixture.CreateContext(_database);
        var repository = new DocumentParsePageRepository(context);
        await repository.ReplaceAsync(Parse, [new(Parse, 1, "old", null)]);
        await MigrationGoldenStates.ExecuteRawAsync(context, """
            CREATE FUNCTION reject_pages() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'synthetic-provider-canary'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_pages BEFORE INSERT ON document_parse_pages FOR EACH ROW EXECUTE FUNCTION reject_pages();
            """);
        var sync = Sync(context, _ => Task.FromResult(Json("[{\"number\":1,\"label\":\"new\"}]")), new());
        var error = await Assert.ThrowsAsync<StructaDocException>(() => sync.SyncAsync(Model(), _run, default));
        error.IsTransient.Should().BeTrue();
        error.Message.Should().NotContain("synthetic-provider-canary");
        error.InnerException.Should().BeNull();
        (await repository.ReadBatchAsync([new(Parse, 1)])).Values.Single().Label.Should().Be("old");
        (await new DocumentParseRepository(context).GetByIdAsync(Parse))!.Status.Should().Be(DocumentParseStatus.Parsing);
        (await context.DocumentParseBlocks.SingleAsync(block => block.ParseId == Parse)).TextContent.Should().Be("legacy text");
    }

    [Fact]
    public async Task WrappedCallerCancellation_DoesNotClearPagesOrPublishParsedStatus()
    {
        using var context = fixture.CreateContext(_database);
        var repository = new DocumentParsePageRepository(context);
        await repository.ReplaceAsync(Parse, [new(Parse, 1, "old", null)]);
        using var cancellation = new CancellationTokenSource();
        var logger = new CaptureLogger();
        var sync = Sync(context, _ =>
        {
            cancellation.Cancel();
            throw new HttpRequestException("synthetic-provider-canary", new OperationCanceledException(cancellation.Token));
        }, logger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.SyncAsync(Model(), _run, cancellation.Token));
        (await repository.ReadBatchAsync([new(Parse, 1)])).Values.Single().Label.Should().Be("old");
        (await new DocumentParseRepository(context).GetByIdAsync(Parse))!.Status.Should().Be(DocumentParseStatus.Parsing);
        logger.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellationAfterCommittedReplacement_DoesNotPublishParsedStatus()
    {
        using var context = fixture.CreateContext(_database);
        using var cancellation = new CancellationTokenSource();
        var repository = new DocumentParsePageRepository(context);
        var sync = Sync(context, _ => Task.FromResult(Json("[{\"number\":1,\"label\":\"committed\"}]")), new(),
            new CancelAfterReplace(repository, cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.SyncAsync(Model(), _run, cancellation.Token));
        (await repository.ReadBatchAsync([new(Parse, 1)])).Values.Single().Label.Should().Be("committed");
        (await new DocumentParseRepository(context).GetByIdAsync(Parse))!.Status.Should().Be(DocumentParseStatus.Parsing);
    }

    private DocumentParseModel Model() => new() { Id = Parse, DocumentFileId = MigrationGoldenStates.FileId, Status = DocumentParseStatus.Parsing, StructaDocParseRunId = _run };

    private StructaDocParseResultSync Sync(DocthecaDbContext context,
        Func<CancellationToken, Task<HttpResponseMessage>> pages, CaptureLogger logger, IDocumentParsePageRepository? pageRepository = null)
    {
        var client = new StructaDocClient(new HttpClient(new Handler((request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/pages", StringComparison.Ordinal)) return pages(token);
            if (path.EndsWith("/assets", StringComparison.Ordinal)) return Task.FromResult(Json("[]"));
            if (path.EndsWith("/markdown", StringComparison.Ordinal)) return Task.FromResult(Json("# synthetic markdown"));
            return Task.FromResult(Json("""
                {"items":[{"sequence":0,"pageNumber":1,"type":"text","content":"first"},
                          {"sequence":1,"pageNumber":null,"type":"text","content":"unknown"},
                          {"sequence":2,"pageNumber":2,"type":"text","content":"second"}],"nextSequence":null}
                """));
        })) { BaseAddress = new Uri("http://pages.test/") }, Options.Create(new StructaDocOptions()), NullLogger<StructaDocClient>.Instance);
        var imageRepository = new DocumentParseImageRepository(context);
        var parseService = new DocumentParseService(new DocumentParseRepository(context), imageRepository, NullLogger<DocumentParseService>.Instance);
        return new(client, parseService, new DocumentParseBlockRepository(context), imageRepository,
            pageRepository ?? new DocumentParsePageRepository(context), logger);
    }

    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class CancelAfterReplace(IDocumentParsePageRepository inner, CancellationTokenSource cancellation) : IDocumentParsePageRepository
    {
        public async Task ReplaceAsync(Guid parseId, IReadOnlyCollection<DocumentParsePageModel> pages, CancellationToken ct = default)
        { await inner.ReplaceAsync(parseId, pages, ct); cancellation.Cancel(); }
        public Task<IReadOnlyDictionary<DocumentParsePageKey, DocumentParsePageModel>> ReadBatchAsync(IReadOnlyCollection<DocumentParsePageKey> keys, CancellationToken ct = default) => inner.ReadBatchAsync(keys, ct);
    }

    private sealed class CaptureLogger : ILogger<StructaDocParseResultSync>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level >= LogLevel.Warning) { Messages.Add(formatter(state, exception)); Exceptions.Add(exception); } }
    }
}
