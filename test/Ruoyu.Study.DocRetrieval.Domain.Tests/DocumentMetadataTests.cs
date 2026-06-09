using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Domain.Tests;

public class DocumentMetadataTests
{
    private readonly Mock<IDocumentRepository> _mockDocRepo;
    private readonly Mock<IDocumentPageRepository> _mockPageRepo;
    private readonly Mock<IDocumentSegmentRepository> _mockSegRepo;
    private readonly Mock<IQuestionSegmentRepository> _mockQRepo;
    private readonly Mock<IDocumentOccurrenceRepository> _mockOccRepo;
    private readonly Mock<IDocumentIngestionJobRepository> _mockJobRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<ISearchIndexService> _mockSearchService;
    private readonly Mock<ILogger<DocumentDomainService>> _mockLogger;
    private readonly DocumentDomainService _service;

    public DocumentMetadataTests()
    {
        _mockDocRepo = new Mock<IDocumentRepository>();
        _mockPageRepo = new Mock<IDocumentPageRepository>();
        _mockSegRepo = new Mock<IDocumentSegmentRepository>();
        _mockQRepo = new Mock<IQuestionSegmentRepository>();
        _mockOccRepo = new Mock<IDocumentOccurrenceRepository>();
        _mockJobRepo = new Mock<IDocumentIngestionJobRepository>();
        _mockUow = new Mock<IUnitOfWork>();
        _mockSearchService = new Mock<ISearchIndexService>();
        _mockLogger = new Mock<ILogger<DocumentDomainService>>();

        _service = new DocumentDomainService(
            _mockDocRepo.Object,
            _mockPageRepo.Object,
            _mockSegRepo.Object,
            _mockQRepo.Object,
            _mockOccRepo.Object,
            _mockJobRepo.Object,
            _mockUow.Object,
            _mockLogger.Object,
            _mockSearchService.Object);
    }

    private DocumentModel CreateReadyDocument(string title = "TestDoc")
    {
        return new DocumentModel
        {
            Id = Guid.NewGuid(),
            Title = title,
            Status = "ready",
            Subject = "英语",
            Grade = "G5",
            Year = "2025",
            Tags = null
        };
    }

    [Fact]
    public async Task UpdateMetadata_DocumentNotFound_ThrowsValidationException()
    {
        // TC-02
        _mockDocRepo.Setup(r => r.GetByTitleAsync("NotFound")).ReturnsAsync((DocumentModel?)null);

        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.UpdateMetadataAsync("NotFound", "英语", null, null, null));

        Assert.Equal("文档不存在", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadata_DocumentNotReady_ThrowsValidationException()
    {
        // TC-03
        var doc = CreateReadyDocument();
        doc.Status = "pending";
        _mockDocRepo.Setup(r => r.GetByTitleAsync("PendingDoc")).ReturnsAsync(doc);

        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.UpdateMetadataAsync("PendingDoc", "英语", null, null, null));

        Assert.Equal("文档未就绪，不允许修改元数据", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadata_InvalidSubject_ThrowsValidationException()
    {
        // TC-04
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(CreateReadyDocument());

        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.UpdateMetadataAsync("TestDoc", "数学", null, null, null));

        Assert.Equal("学科仅支持：英语", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadata_InvalidGrade_ThrowsValidationException()
    {
        // TC-05
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(CreateReadyDocument());

        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.UpdateMetadataAsync("TestDoc", null, "G99", null, null));

        Assert.Contains("年级取值非法", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadata_NullFields_NotModified()
    {
        // TC-06
        var doc = CreateReadyDocument();
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(doc);

        var result = await _service.UpdateMetadataAsync("TestDoc", null, null, null, null);

        Assert.Equal("英语", result.Subject);
        Assert.Equal("G5", result.Grade);
        Assert.Equal("2025", result.Year);
        Assert.NotNull(result.UpdatedAt);
        _mockDocRepo.Verify(r => r.UpdateAsync(doc), Times.Once);
        _mockUow.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateMetadata_SearchIndexFails_DoesNotThrow()
    {
        // TC-07
        var doc = CreateReadyDocument();
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(doc);
        _mockSearchService
            .Setup(s => s.UpdateDocumentMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("Search down"));

        var result = await _service.UpdateMetadataAsync("TestDoc", "英语", null, null, null);

        _mockDocRepo.Verify(r => r.UpdateAsync(It.IsAny<DocumentModel>()), Times.Once);
        _mockUow.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateMetadata_GradeUpdated()
    {
        // TC-08
        var doc = CreateReadyDocument();
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(doc);

        var result = await _service.UpdateMetadataAsync("TestDoc", null, "G6", null, null);

        Assert.Equal("G6", result.Grade);
    }

    [Fact]
    public async Task UpdateMetadata_TagsUpdated()
    {
        // TC-09
        var doc = CreateReadyDocument();
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(doc);

        var result = await _service.UpdateMetadataAsync("TestDoc", null, null, null, "[\"tag1\",\"tag2\"]");

        Assert.Equal("[\"tag1\",\"tag2\"]", result.Tags);
    }

    [Fact]
    public async Task UpdateMetadata_NoSearchService_DoesNotThrow()
    {
        // TC-10
        var serviceWithoutSearch = new DocumentDomainService(
            _mockDocRepo.Object,
            _mockPageRepo.Object,
            _mockSegRepo.Object,
            _mockQRepo.Object,
            _mockOccRepo.Object,
            _mockJobRepo.Object,
            _mockUow.Object,
            _mockLogger.Object,
            searchIndexService: null);

        var doc = CreateReadyDocument();
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(doc);

        var result = await serviceWithoutSearch.UpdateMetadataAsync("TestDoc", "英语", null, null, null);

        Assert.Equal("英语", result.Subject);
    }

    [Fact]
    public async Task UpdateMetadata_SuccessUpdatesYear()
    {
        // TC-08 extended
        var doc = CreateReadyDocument();
        doc.Year = "2024";
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(doc);

        var result = await _service.UpdateMetadataAsync("TestDoc", null, null, "2025", null);

        Assert.Equal("2025", result.Year);
    }
}