using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using global::Ruoyu.Study.DocRetrieval.Domain.Exceptions;
using global::Ruoyu.Study.DocRetrieval.Domain.Models;
using global::Ruoyu.Study.DocRetrieval.Domain.Repositories;
using global::Ruoyu.Study.DocRetrieval.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests;

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
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(() => _service.CreateDocumentAsync(document));
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
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(() => _service.CreateDocumentAsync(document));
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
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(() => _service.CreateDocumentAsync(document));
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

    #region UpdateMetadataAsync Tests

    [Fact]
    public async Task UpdateMetadataAsync_ValidUpdate_ReturnsUpdatedDocument()
    {
        // Arrange
        var document = new DocumentModel
        {
            Id = Guid.NewGuid(),
            Title = "test-doc",
            Subject = "英语",
            Grade = "G1",
            Year = "2023",
            Status = DocumentStatus.Ready
        };
        _documentRepoMock.Setup(r => r.GetByTitleAsync(document.Title)).ReturnsAsync(document);
        _documentRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentModel>())).ReturnsAsync(true);
        _searchIndexServiceMock.Setup(s => s.UpdateDocumentMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.UpdateMetadataAsync("test-doc", "英语", "G2", "2024", "[\"tag1\"]");

        // Assert
        Assert.Equal("G2", result.Grade);
        Assert.Equal("2024", result.Year);
        Assert.Equal("[\"tag1\"]", result.Tags);
        _searchIndexServiceMock.Verify(s => s.UpdateDocumentMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task UpdateMetadataAsync_DocumentNotFound_ThrowsValidationException()
    {
        // Arrange
        _documentRepoMock.Setup(r => r.GetByTitleAsync("nonexistent")).ReturnsAsync((DocumentModel?)null);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(() =>
            _service.UpdateMetadataAsync("nonexistent", "英语", "G1", "2023", null));
        Assert.Contains("Document not found", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadataAsync_DocumentNotReady_ThrowsValidationException()
    {
        // Arrange
        var document = new DocumentModel { Title = "pending-doc", Status = DocumentStatus.Pending };
        _documentRepoMock.Setup(r => r.GetByTitleAsync("pending-doc")).ReturnsAsync(document);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(() =>
            _service.UpdateMetadataAsync("pending-doc", "英语", "G1", "2023", null));
        Assert.Contains("Document not ready", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadataAsync_InvalidSubject_ThrowsValidationException()
    {
        // Arrange
        var document = new DocumentModel { Title = "ready-doc", Status = DocumentStatus.Ready, Subject = "英语", Grade = "G1", Year = "2023" };
        _documentRepoMock.Setup(r => r.GetByTitleAsync("ready-doc")).ReturnsAsync(document);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(() =>
            _service.UpdateMetadataAsync("ready-doc", "数学", null, null, null));
        Assert.Contains("Subject only supports", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadataAsync_InvalidGrade_ThrowsValidationException()
    {
        // Arrange
        var document = new DocumentModel { Title = "ready-doc", Status = DocumentStatus.Ready, Subject = "英语", Grade = "G1", Year = "2023" };
        _documentRepoMock.Setup(r => r.GetByTitleAsync("ready-doc")).ReturnsAsync(document);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(() =>
            _service.UpdateMetadataAsync("ready-doc", null, "Invalid", null, null));
        Assert.Contains("Invalid grade value", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadataAsync_PartialUpdate_OnlyUpdatesProvidedFields()
    {
        // Arrange
        var document = new DocumentModel
        {
            Title = "ready-doc",
            Status = DocumentStatus.Ready,
            Subject = "英语",
            Grade = "G1",
            Year = "2023"
        };
        _documentRepoMock.Setup(r => r.GetByTitleAsync("ready-doc")).ReturnsAsync(document);
        _documentRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentModel>())).ReturnsAsync(true);

        // Act - only update grade
        var result = await _service.UpdateMetadataAsync("ready-doc", null, "G3", null, null);

        // Assert
        Assert.Equal("英语", result.Subject); // unchanged
        Assert.Equal("G3", result.Grade);     // changed
        Assert.Equal("2023", result.Year);    // unchanged
    }

    #endregion

    #region DeleteDocumentAsync Tests

    [Fact]
    public async Task DeleteDocumentAsync_ExistingDocument_ReturnsTrue()
    {
        // Arrange
        var document = CreateValidDocument();
        document.Id = Guid.NewGuid();
        _documentRepoMock.Setup(r => r.GetByTitleAsync(document.Title)).ReturnsAsync(document);
        _documentRepoMock.Setup(r => r.DeleteAsync(document.Id)).ReturnsAsync(true);

        // Act
        var result = await _service.DeleteDocumentAsync(document.Title);

        // Assert
        Assert.True(result);
        _occurrenceRepoMock.Verify(r => r.DeleteByDocumentIdAsync(document.Id), Times.Once);
        _questionRepoMock.Verify(r => r.DeleteByDocumentIdAsync(document.Id), Times.Once);
        _segmentRepoMock.Verify(r => r.DeleteByDocumentIdAsync(document.Id), Times.Once);
        _pageRepoMock.Verify(r => r.DeleteByDocumentIdAsync(document.Id), Times.Once);
        _documentRepoMock.Verify(r => r.DeleteAsync(document.Id), Times.Once);
        _searchIndexServiceMock.Verify(s => s.DeleteDocumentIndexAsync(document.Id), Times.Once);
    }

    [Fact]
    public async Task DeleteDocumentAsync_NonExistentDocument_ReturnsTrue_Idempotent()
    {
        // Arrange
        _documentRepoMock.Setup(r => r.GetByTitleAsync("nonexistent")).ReturnsAsync((DocumentModel?)null);

        // Act
        var result = await _service.DeleteDocumentAsync("nonexistent");

        // Assert
        Assert.True(result);
        _documentRepoMock.Verify(r => r.DeleteAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task DeleteDocumentAsync_ProcessingDocument_DeletesJob()
    {
        // Arrange
        var document = CreateValidDocument();
        document.Id = Guid.NewGuid();
        _documentRepoMock.Setup(r => r.GetByTitleAsync(document.Title)).ReturnsAsync(document);
        _documentRepoMock.Setup(r => r.DeleteAsync(document.Id)).ReturnsAsync(true);

        // Act
        var result = await _service.DeleteDocumentAsync(document.Title);

        // Assert
        Assert.True(result);
        _jobRepoMock.Verify(r => r.DeleteByDocumentIdAsync(document.Id), Times.Once);
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
    public async Task CancelIngestionJobAsync_PendingJob_CancelsSuccessfully()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = DocumentStatus.Pending };
        _jobRepoMock.Setup(r => r.GetByDocumentIdAsync(documentId)).ReturnsAsync(job);

        // Act
        await _service.CancelIngestionJobAsync(documentId);

        // Assert
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Status == DocumentStatus.Cancelled)), Times.Once);
    }

    [Fact]
    public async Task CancelIngestionJobAsync_ProcessingJob_CancelsSuccessfully()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = DocumentStatus.Processing };
        _jobRepoMock.Setup(r => r.GetByDocumentIdAsync(documentId)).ReturnsAsync(job);

        // Act
        await _service.CancelIngestionJobAsync(documentId);

        // Assert
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Status == DocumentStatus.Cancelled)), Times.Once);
    }

    [Fact]
    public async Task CancelIngestionJobAsync_SuccessJob_DoesNotCancel()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = DocumentStatus.Success };
        _jobRepoMock.Setup(r => r.GetByDocumentIdAsync(documentId)).ReturnsAsync(job);

        // Act
        await _service.CancelIngestionJobAsync(documentId);

        // Assert
        _jobRepoMock.Verify(r => r.UpdateAsync(It.IsAny<DocumentIngestionJobModel>()), Times.Never);
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
