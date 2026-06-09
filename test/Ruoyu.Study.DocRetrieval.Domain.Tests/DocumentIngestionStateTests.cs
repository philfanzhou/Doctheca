using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;

namespace Ruoyu.Study.DocRetrieval.Domain.Tests;

public class DocumentIngestionStateTests
{
    private readonly Mock<IDocumentRepository> _documentRepoMock;
    private readonly Mock<IDocumentIngestionJobRepository> _jobRepoMock;
    private readonly Mock<IUnitOfWork> _unitOfWorkMock;
    private readonly Mock<ILogger<DocumentDomainService>> _loggerMock;
    private readonly DocumentDomainService _service;

    public DocumentIngestionStateTests()
    {
        _documentRepoMock = new Mock<IDocumentRepository>();
        _jobRepoMock = new Mock<IDocumentIngestionJobRepository>();
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _loggerMock = new Mock<ILogger<DocumentDomainService>>();

        _service = new DocumentDomainService(
            _documentRepoMock.Object,
            Mock.Of<IDocumentPageRepository>(),
            Mock.Of<IDocumentSegmentRepository>(),
            Mock.Of<IQuestionSegmentRepository>(),
            Mock.Of<IDocumentOccurrenceRepository>(),
            _jobRepoMock.Object,
            _unitOfWorkMock.Object,
            _loggerMock.Object);
    }

    // UT-D-01: StartIngestionJobAsync 设置 processing
    [Fact]
    public async Task StartIngestionJobAsync_ShouldSetProcessingStatus()
    {
        var jobId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = "pending" };
        var document = new DocumentModel { Id = documentId, Status = "pending" };

        _jobRepoMock.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        await _service.StartIngestionJobAsync(jobId, "v1.0", null);

        Assert.Equal("processing", job.Status);
        Assert.NotNull(job.StartedAt);
        Assert.Equal("v1.0", job.ParserVersion);
        Assert.Equal("processing", document.Status);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    // UT-D-02: CompleteIngestionJobAsync 设置 success
    [Fact]
    public async Task CompleteIngestionJobAsync_ShouldSetSuccessStatus()
    {
        var jobId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = "processing" };
        var document = new DocumentModel { Id = documentId, Status = "processing" };

        _jobRepoMock.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        await _service.CompleteIngestionJobAsync(jobId);

        Assert.Equal("success", job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Equal("ready", document.Status);
    }

    // UT-D-03: FailIngestionJobAsync 设置 failed + ErrorMessage
    [Fact]
    public async Task FailIngestionJobAsync_ShouldSetFailedStatus()
    {
        var jobId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = "processing" };
        var document = new DocumentModel { Id = documentId, Status = "processing" };

        _jobRepoMock.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        await _service.FailIngestionJobAsync(jobId, "解析失败");

        Assert.Equal("failed", job.Status);
        Assert.Equal("解析失败", job.ErrorMessage);
        Assert.NotNull(job.FinishedAt);
        Assert.Equal("failed", document.Status);
    }

    // UT-D-04: StartIngestionJobAsync 记录 ParserVersion
    [Fact]
    public async Task StartIngestionJobAsync_ShouldStoreParserVersionAndOcrVersion()
    {
        var jobId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = "pending" };
        var document = new DocumentModel { Id = documentId, Status = "pending" };

        _jobRepoMock.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        await _service.StartIngestionJobAsync(jobId, "v2.0", "ocr-1.0");

        Assert.Equal("v2.0", job.ParserVersion);
        Assert.Equal("ocr-1.0", job.OcrVersion);
    }

    // UT-D-05: GetPendingJobsAsync 返回 pending 任务
    [Fact]
    public async Task GetPendingJobsAsync_ShouldReturnPendingJobs()
    {
        var jobs = new List<DocumentIngestionJobModel>
        {
            new() { Id = Guid.NewGuid(), Status = "pending" },
            new() { Id = Guid.NewGuid(), Status = "pending" }
        };

        _jobRepoMock.Setup(r => r.GetByStatusAsync("pending")).ReturnsAsync(jobs);

        var result = await _service.GetPendingJobsAsync();
        Assert.Equal(2, result.Count);
        Assert.All(result, j => Assert.Equal("pending", j.Status));
    }

    // UT-D-06: StartIngestionJobAsync 对不存在的 job 静默返回
    [Fact]
    public async Task StartIngestionJobAsync_NonexistentJob_ShouldReturnSilently()
    {
        var jobId = Guid.NewGuid();
        _jobRepoMock.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync((DocumentIngestionJobModel?)null);

        // 不应抛异常，也不调用 SaveChanges（因为 job 不存在）
        await _service.StartIngestionJobAsync(jobId, "v1.0", null);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(), Times.Never);
    }

    // AC-V-01: GetDocumentAsync 查询已有文档
    [Fact]
    public async Task GetDocumentAsync_ShouldReturnDocument()
    {
        var docId = Guid.NewGuid();
        var document = new DocumentModel { Id = docId, Title = "测试" };
        _documentRepoMock.Setup(r => r.GetByIdAsync(docId)).ReturnsAsync(document);

        var result = await _service.GetDocumentAsync(docId);
        Assert.NotNull(result);
        Assert.Equal("测试", result.Title);
    }

    // AC-V-02: GetDocumentAsync 查询不存在的文档
    [Fact]
    public async Task GetDocumentAsync_NonexistentDocument_ShouldReturnNull()
    {
        _documentRepoMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((DocumentModel?)null);
        var result = await _service.GetDocumentAsync(Guid.NewGuid());
        Assert.Null(result);
    }
}