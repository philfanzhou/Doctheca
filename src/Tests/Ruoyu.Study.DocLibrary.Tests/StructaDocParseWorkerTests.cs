using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Ruoyu.Study.DocLibrary.Service;
using Ruoyu.Study.DocLibrary.Service.Parsing;
using Ruoyu.Study.DocLibrary.Service.StructaDoc;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class StructaDocParseWorkerTests
{
    private readonly Mock<IDocumentParseService> _parseService = new();
    private readonly Mock<IDocumentFileService> _fileService = new();
    private readonly Mock<IStructaDocClient> _client = new();
    private readonly Mock<IStructaDocParseResultSync> _sync = new();
    private readonly Mock<ISearchIndexService> _searchIndex = new();
    private readonly Mock<IOssService> _ossService = new();
    private readonly StructaDocOptions _options = new()
    {
        BaseUrl = "http://structadoc.test",
        ApiKey = "sd1.test.key",
    };

    [Fact]
    public async Task SubmitAsync_StructaDocFile_CreatesRunAndMarksParsing()
    {
        var documentId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var job = CreateJob(DocumentParseStatus.Pending);
        _fileService.Setup(x => x.GetByIdAsync(job.DocumentFileId))
            .ReturnsAsync(CreateFile(documentId: documentId));
        _client.Setup(x => x.CreateParseRunAsync(
                documentId, job.Id.ToString("N"), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRun(runId, StructaDocParseRunStatus.Queued));

        await Run((worker, provider) => worker.SubmitAsync(job, provider, CancellationToken.None));

        _parseService.Verify(x => x.UpdateStatusAsync(
            job.Id,
            DocumentParseStatus.Parsing,
            null, null,
            runId.ToString("D"),
            null, null, null, null, null,
            runId), Times.Once);
    }

    [Fact]
    public async Task SubmitAsync_ModelMapping_PassesProviderConfigId()
    {
        var documentId = Guid.NewGuid();
        var providerConfigId = Guid.NewGuid();
        _options.ProviderConfigIdByModel["vlm"] = providerConfigId;
        var job = CreateJob(DocumentParseStatus.Pending, "vlm");
        _fileService.Setup(x => x.GetByIdAsync(job.DocumentFileId))
            .ReturnsAsync(CreateFile(documentId: documentId));
        _client.Setup(x => x.CreateParseRunAsync(
                documentId, It.IsAny<string>(), providerConfigId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRun(Guid.NewGuid(), StructaDocParseRunStatus.Queued));

        await Run((worker, provider) => worker.SubmitAsync(job, provider, CancellationToken.None));

        _client.Verify(x => x.CreateParseRunAsync(
            documentId, job.Id.ToString("N"), providerConfigId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitAsync_LegacyFile_UploadsToStructaDocAndAttaches()
    {
        var documentId = Guid.NewGuid();
        var job = CreateJob(DocumentParseStatus.Pending);
        var file = CreateFile(documentId: null, filePath: "documents/legacy/paper.pdf", id: job.DocumentFileId);
        _fileService.Setup(x => x.GetByIdAsync(job.DocumentFileId)).ReturnsAsync(file);
        _fileService.Setup(x => x.AttachStructaDocDocumentAsync(job.DocumentFileId, documentId))
            .ReturnsAsync(file);
        _ossService.Setup(x => x.DownloadAsync("documents/legacy/paper.pdf"))
            .ReturnsAsync(new MemoryStream([1, 2]));
        _client.Setup(x => x.UploadDocumentAsync(
                file.FileName, file.ContentType, It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StructaDocDocumentResponse { Id = documentId, MediaType = "application/pdf" });
        _client.Setup(x => x.CreateParseRunAsync(
                documentId, It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRun(Guid.NewGuid(), StructaDocParseRunStatus.Queued));

        await Run((worker, provider) => worker.SubmitAsync(job, provider, CancellationToken.None));

        _fileService.Verify(x => x.AttachStructaDocDocumentAsync(job.DocumentFileId, documentId), Times.Once);
        _parseService.Verify(x => x.UpdateStatusAsync(
            job.Id, DocumentParseStatus.Parsing,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task SubmitAsync_TransientFailure_KeepsJobForRetry()
    {
        var job = CreateJob(DocumentParseStatus.Pending);
        _fileService.Setup(x => x.GetByIdAsync(job.DocumentFileId))
            .ReturnsAsync(CreateFile(documentId: Guid.NewGuid()));
        _client.Setup(x => x.CreateParseRunAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StructaDocException("timeout", new TaskCanceledException()));

        var act = () => Run((worker, provider) => worker.SubmitAsync(job, provider, CancellationToken.None));

        await act.Should().ThrowAsync<StructaDocException>();
        _parseService.Verify(x => x.UpdateStatusAsync(
            job.Id, DocumentParseStatus.Failed, It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task PollAsync_Succeeded_SyncsResultAndIndexesBlocks()
    {
        var runId = Guid.NewGuid();
        var job = CreateJob(DocumentParseStatus.Parsing, runId: runId);
        var file = CreateFile(documentId: Guid.NewGuid());
        _fileService.Setup(x => x.GetByIdAsync(job.DocumentFileId)).ReturnsAsync(file);
        _client.Setup(x => x.GetParseRunAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRun(runId, StructaDocParseRunStatus.Succeeded));
        _sync.Setup(x => x.SyncAsync(job, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("# markdown");

        await Run((worker, provider) => worker.PollAsync(job, provider, CancellationToken.None));

        _sync.Verify(x => x.SyncAsync(job, runId, It.IsAny<CancellationToken>()), Times.Once);
        _searchIndex.Verify(x => x.IndexParseBlocksAsync(
            job.Id, file.Id, file.FileName, file.Subject, file.Grade, file.Year), Times.Once);
    }

    [Fact]
    public async Task PollAsync_FailedRun_MarksParseFailedWithReason()
    {
        var runId = Guid.NewGuid();
        var job = CreateJob(DocumentParseStatus.Parsing, runId: runId);
        _client.Setup(x => x.GetParseRunAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StructaDocParseRunResponse
            {
                Id = runId,
                Status = StructaDocParseRunStatus.Failed,
                ErrorCode = "provider-task-failed",
                ErrorMessage = "MinerU rejected the file",
            });

        await Run((worker, provider) => worker.PollAsync(job, provider, CancellationToken.None));

        _parseService.Verify(x => x.UpdateStatusAsync(
            job.Id,
            DocumentParseStatus.Failed,
            "StructaDoc parse run failed (provider-task-failed: MinerU rejected the file)",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task PollAsync_NonTerminalRun_KeepsWaiting()
    {
        var runId = Guid.NewGuid();
        var job = CreateJob(DocumentParseStatus.Parsing, runId: runId);
        _client.Setup(x => x.GetParseRunAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRun(runId, StructaDocParseRunStatus.Running));

        await Run((worker, provider) => worker.PollAsync(job, provider, CancellationToken.None));

        _parseService.Verify(x => x.UpdateStatusAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<Guid?>()), Times.Never);
        _sync.Verify(x => x.SyncAsync(
            It.IsAny<DocumentParseModel>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollAsync_LegacyOrphanParsing_MarksFailedWithMigrationMessage()
    {
        var job = CreateJob(DocumentParseStatus.Parsing, runId: null);

        await Run((worker, provider) => worker.PollAsync(job, provider, CancellationToken.None));

        _parseService.Verify(x => x.UpdateStatusAsync(
            job.Id,
            DocumentParseStatus.Failed,
            It.Is<string>(m => m.Contains("StructaDoc pipeline migration")),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Once);
        _client.Verify(x => x.GetParseRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollAsync_RunDisappeared_MarksFailed()
    {
        var runId = Guid.NewGuid();
        var job = CreateJob(DocumentParseStatus.Parsing, runId: runId);
        _client.Setup(x => x.GetParseRunAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StructaDocParseRunResponse?)null);

        await Run((worker, provider) => worker.PollAsync(job, provider, CancellationToken.None));

        _parseService.Verify(x => x.UpdateStatusAsync(
            job.Id, DocumentParseStatus.Failed,
            It.Is<string>(m => m.Contains("no longer exists")),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Once);
    }

    // ── helpers ──

    private static DocumentParseModel CreateJob(string status, string modelVersion = "vlm", Guid? runId = null) => new()
    {
        Id = Guid.NewGuid(),
        DocumentFileId = Guid.NewGuid(),
        ModelVersion = modelVersion,
        Status = status,
        StructaDocParseRunId = runId,
    };

    private static DocumentFileModel CreateFile(Guid? documentId, string? filePath = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        FileName = "paper.pdf",
        ContentType = "application/pdf",
        FilePath = filePath,
        StructaDocDocumentId = documentId,
    };

    private static StructaDocParseRunResponse CreateRun(Guid runId, string status) => new()
    {
        Id = runId,
        Status = status,
    };

    private async Task Run(Func<StructaDocParseWorker, IServiceProvider, Task> action)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_parseService.Object);
        services.AddSingleton(_fileService.Object);
        services.AddSingleton(_client.Object);
        services.AddSingleton(_sync.Object);
        services.AddSingleton(_searchIndex.Object);
        services.AddSingleton(_ossService.Object);
        services.AddSingleton(Options.Create(_options));
        var provider = services.BuildServiceProvider();

        var worker = new StructaDocParseWorker(provider, NullLogger<StructaDocParseWorker>.Instance);
        await action(worker, provider);
    }
}
