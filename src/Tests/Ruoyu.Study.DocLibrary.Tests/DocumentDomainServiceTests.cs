using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using global::Ruoyu.Study.DocLibrary.Domain.Exceptions;
using global::Ruoyu.Study.DocLibrary.Domain.Models;
using global::Ruoyu.Study.DocLibrary.Domain.Repositories;
using global::Ruoyu.Study.DocLibrary.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class DocumentDomainServiceTests
{
    private readonly Mock<IDocumentRepository> _documentRepoMock;
    private readonly Mock<IDocumentPageRepository> _pageRepoMock;
    private readonly Mock<IDocumentSegmentRepository> _segmentRepoMock;
    private readonly Mock<IQuestionSegmentRepository> _questionRepoMock;
    private readonly Mock<IDocumentOccurrenceRepository> _occurrenceRepoMock;
    private readonly Mock<IDocumentIngestionJobRepository> _jobRepoMock;
    private readonly Mock<IUnitOfWork> _unitOfWorkMock;
    private readonly Mock<ISearchIndexService> _searchIndexServiceMock;
    private readonly Mock<ILogger<DocumentDomainService>> _loggerMock;
    private readonly DocumentDomainService _service;

    public DocumentDomainServiceTests()
    {
        _documentRepoMock = new Mock<IDocumentRepository>();
        _pageRepoMock = new Mock<IDocumentPageRepository>();
        _segmentRepoMock = new Mock<IDocumentSegmentRepository>();
        _questionRepoMock = new Mock<IQuestionSegmentRepository>();
        _occurrenceRepoMock = new Mock<IDocumentOccurrenceRepository>();
        _jobRepoMock = new Mock<IDocumentIngestionJobRepository>();
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _searchIndexServiceMock = new Mock<ISearchIndexService>();
        _loggerMock = new Mock<ILogger<DocumentDomainService>>();

        _service = new DocumentDomainService(
            _documentRepoMock.Object,
            _pageRepoMock.Object,
            _segmentRepoMock.Object,
            _questionRepoMock.Object,
            _occurrenceRepoMock.Object,
            _jobRepoMock.Object,
            _unitOfWorkMock.Object,
            _loggerMock.Object,
            _searchIndexServiceMock.Object);
    }

    #region CreateDocumentAsync Tests

    [Fact]
    public async Task CreateDocumentAsync_ValidDocument_ReturnsCreatedDocument()
    {
        // Arrange
        var document = CreateValidDocument();
        _documentRepoMock.Setup(r => r.GetByTitleAsync(document.Title)).ReturnsAsync((DocumentModel?)null);
        _documentRepoMock.Setup(r => r.GetByFileHashAndStatusAsync(document.FileHash, DocumentStatus.Ready)).ReturnsAsync((DocumentModel?)null);

        // Act
        var result = await _service.CreateDocumentAsync(document);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(DocumentStatus.Pending, result.Status);
        _documentRepoMock.Verify(r => r.AddAsync(It.IsAny<DocumentModel>()), Times.Once);
        _jobRepoMock.Verify(r => r.AddAsync(It.IsAny<DocumentIngestionJobModel>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task CreateDocumentAsync_WithCreatedBy_PreservesValue()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var document = CreateValidDocument();
        document.CreatedBy = userId;
        _documentRepoMock.Setup(r => r.GetByTitleAsync(document.Title)).ReturnsAsync((DocumentModel?)null);
        _documentRepoMock.Setup(r => r.GetByFileHashAndStatusAsync(document.FileHash, DocumentStatus.Ready)).ReturnsAsync((DocumentModel?)null);

        // Act
        var result = await _service.CreateDocumentAsync(document);

        // Assert
        Assert.Equal(userId, result.CreatedBy);
        _documentRepoMock.Verify(r => r.AddAsync(It.Is<DocumentModel>(d => d.CreatedBy == userId)), Times.Once);
    }

    [Fact]
    public async Task CreateDocumentAsync_WithoutCreatedBy_CreatedByIsNull()
    {
        // Arrange
        var document = CreateValidDocument();
        // CreatedBy is null by default
        _documentRepoMock.Setup(r => r.GetByTitleAsync(document.Title)).ReturnsAsync((DocumentModel?)null);
        _documentRepoMock.Setup(r => r.GetByFileHashAndStatusAsync(document.FileHash, DocumentStatus.Ready)).ReturnsAsync((DocumentModel?)null);

        // Act
        var result = await _service.CreateDocumentAsync(document);

        // Assert
        Assert.Null(result.CreatedBy);
    }

    [Fact]
    public async Task CreateDocumentAsync_DuplicateTitle_ThrowsValidationException()
    {
        // Arrange
        var document = CreateValidDocument();
        _documentRepoMock.Setup(r => r.GetByTitleAsync(document.Title)).ReturnsAsync(new DocumentModel { Title = document.Title });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocLibraryValidationException>(() => _service.CreateDocumentAsync(document));
        Assert.Contains("Document title already exists", ex.Message);
    }

    [Fact]
    public async Task CreateDocumentAsync_DuplicateFileHash_ThrowsValidationException()
    {
        // Arrange
        var document = CreateValidDocument();
        _documentRepoMock.Setup(r => r.GetByTitleAsync(document.Title)).ReturnsAsync((DocumentModel?)null);
        _documentRepoMock.Setup(r => r.GetByFileHashAndStatusAsync(document.FileHash, DocumentStatus.Ready)).ReturnsAsync(new DocumentModel());

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocLibraryValidationException>(() => _service.CreateDocumentAsync(document));
        Assert.Contains("File already imported", ex.Message);
    }

    [Theory]
    [InlineData("", "英语", "G1", "2023", "Document title cannot be empty")]
    [InlineData("test", "Invalid", "G1", "2023", "Subject only supports")]
    [InlineData("test", "英语", "Invalid", "2023", "Invalid grade value")]
    [InlineData("empty-hash", "英语", "G1", "2023", "File hash cannot be empty")]
    public async Task CreateDocumentAsync_InvalidMetadata_ThrowsValidationException(
        string title, string subject, string grade, string year, string expectedErrorPart)
    {
        // Arrange
        var document = new DocumentModel
        {
            Title = title,
            Subject = subject,
            Grade = grade,
            Year = year,
            FileHash = title == "empty-hash" ? "" : "abc123",
            SourceType = "pdf",
            FilePath = "/test/path",
            FileSize = 1024
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocLibraryValidationException>(() => _service.CreateDocumentAsync(document));
        Assert.Contains(expectedErrorPart, ex.Message);
    }

    [Fact]
    public async Task CreateDocumentAsync_EmptySubjectAndGrade_Succeeds()
    {
        // Arrange - subject and grade are now optional (AI auto-fills later)
        var document = new DocumentModel
        {
            Title = "test-no-subject-grade",
            Subject = "",
            Grade = "",
            FileHash = "abc123def456",
            SourceType = "pdf",
            FilePath = "/test/path",
            FileSize = 1024
        };
        _documentRepoMock.Setup(r => r.GetByTitleAsync(document.Title)).ReturnsAsync((DocumentModel?)null);
        _documentRepoMock.Setup(r => r.GetByFileHashAndStatusAsync(document.FileHash, DocumentStatus.Ready)).ReturnsAsync((DocumentModel?)null);

        // Act
        var result = await _service.CreateDocumentAsync(document);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DocumentStatus.Pending, result.Status);
    }

    #endregion



    #region Job Management Tests

    [Fact]
    public async Task StartIngestionJobAsync_UpdatesJobAndDocumentStatus()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = DocumentStatus.Pending };
        var document = new DocumentModel { Id = documentId, Status = DocumentStatus.Pending };

        _jobRepoMock.Setup(r => r.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        // Act
        await _service.StartIngestionJobAsync(job.Id, "v1.0", "5.0");

        // Assert
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Status == DocumentStatus.Processing)), Times.Once);
        _documentRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentModel>(d => d.Status == DocumentStatus.Processing)), Times.Once);
    }

    [Fact]
    public async Task CompleteIngestionJobAsync_UpdatesJobAndDocumentToReady()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = DocumentStatus.Processing };
        var document = new DocumentModel { Id = documentId, Status = DocumentStatus.Processing };

        _jobRepoMock.Setup(r => r.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        // Act
        await _service.CompleteIngestionJobAsync(job.Id);

        // Assert
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Status == DocumentStatus.Success)), Times.Once);
        _documentRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentModel>(d => d.Status == DocumentStatus.Ready)), Times.Once);
    }

    [Fact]
    public async Task FailIngestionJobAsync_UpdatesJobAndDocumentToFailed()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = DocumentStatus.Processing };
        var document = new DocumentModel { Id = documentId, Status = DocumentStatus.Processing };

        _jobRepoMock.Setup(r => r.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        // Act
        await _service.FailIngestionJobAsync(job.Id, "解析失败");

        // Assert
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Status == DocumentStatus.Failed && j.ErrorMessage == "解析失败")), Times.Once);
        _documentRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentModel>(d => d.Status == DocumentStatus.Failed)), Times.Once);
    }

    [Fact]
    public async Task GetPendingJobsAsync_ReturnsPendingJobs()
    {
        // Arrange
        var pendingJobs = new List<DocumentIngestionJobModel>
        {
            new() { Id = Guid.NewGuid(), Status = DocumentStatus.Pending },
            new() { Id = Guid.NewGuid(), Status = DocumentStatus.Pending }
        };
        _jobRepoMock.Setup(r => r.GetByStatusAsync(DocumentStatus.Pending)).ReturnsAsync(pendingJobs);

        // Act
        var result = await _service.GetPendingJobsAsync();

        // Assert
        Assert.Equal(2, result.Count);
    }

    #endregion

    #region GetDocumentListAsync Tests

    [Fact]
    public async Task GetDocumentListAsync_InvalidPage_UsesDefaults()
    {
        // Arrange
        var items = new List<DocumentModel>();
        _documentRepoMock.Setup(r => r.GetListAsync(1, 20, null, null, null, null, null)).ReturnsAsync((items, 0));

        // Act
        var (result, _) = await _service.GetDocumentListAsync(-1, 0);

        // Assert
        _documentRepoMock.Verify(r => r.GetListAsync(1, 20, null, null, null, null, null), Times.Once);
    }

    [Fact]
    public async Task GetDocumentListAsync_WithFilters_PassesFiltersCorrectly()
    {
        // Arrange
        var items = new List<DocumentModel> { CreateValidDocument() };
        _documentRepoMock.Setup(r => r.GetListAsync(1, 20, DocumentStatus.Ready, "英语", "G1", "keyword", "2023"))
            .ReturnsAsync((items, 1));

        // Act
        var (result, totalCount) = await _service.GetDocumentListAsync(1, 20, DocumentStatus.Ready, "英语", "G1", "keyword", "2023");

        // Assert
        Assert.Single(result);
        Assert.Equal(1, totalCount);
    }

    #endregion

    #region UpdateJobProgressAsync Tests

    [Fact]
    public async Task UpdateJobProgressAsync_UpdatesJobAndPersists()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel
        {
            Id = jobId,
            DocumentId = Guid.NewGuid(),
            Status = DocumentStatus.Processing,
            Progress = 0,
            ProgressStage = null
        };
        _jobRepoMock.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);

        // Act
        await _service.UpdateJobProgressAsync(jobId, 45, "parsing");

        // Assert
        Assert.Equal(45, job.Progress);
        Assert.Equal("parsing", job.ProgressStage);
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Id == jobId && j.Progress == 45 && j.ProgressStage == "parsing")), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateJobProgressAsync_DbFailure_DoesNotThrow()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _jobRepoMock.Setup(r => r.GetByIdAsync(jobId)).ThrowsAsync(new Exception("DB 不可用"));

        // Act & Assert: 不抛异常
        await _service.UpdateJobProgressAsync(jobId, 45, "parsing");
    }

    #endregion

    #region Helpers

    private static DocumentModel CreateValidDocument() => new()
    {
        Title = "test-document",
        SourceType = "pdf",
        FileHash = "abc123def456",
        FilePath = "/test/path/to/file.pdf",
        FileSize = 102400,
        Language = "en",
        Grade = "G10",
        Subject = "英语",
        Year = "2023",
        Tags = "[\"高考\"]"
    };

    #endregion
}
