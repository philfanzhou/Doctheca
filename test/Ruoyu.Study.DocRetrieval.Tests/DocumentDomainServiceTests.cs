using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
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
        _documentRepoMock.Setup(r => r.GetByFileHashAndStatusAsync(document.FileHash, "ready")).ReturnsAsync((DocumentModel?)null);

        // Act
        var result = await _service.CreateDocumentAsync(document);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("pending", result.Status);
        _documentRepoMock.Verify(r => r.AddAsync(It.IsAny<DocumentModel>()), Times.Once);
        _jobRepoMock.Verify(r => r.AddAsync(It.IsAny<DocumentIngestionJobModel>()), Times.Once);
        _unitOfWorkMock.Verify(u => u.SaveChangesAsync(), Times.Once);
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
        _documentRepoMock.Setup(r => r.GetByFileHashAndStatusAsync(document.FileHash, "ready")).ReturnsAsync(new DocumentModel());

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(() => _service.CreateDocumentAsync(document));
        Assert.Contains("File already imported", ex.Message);
    }

    [Theory]
    [InlineData("", "English", "G1", "2023", "Document title cannot be empty")]
    [InlineData("test", "", "G1", "2023", "Subject cannot be empty")]
    [InlineData("test", "English", "", "2023", "Grade cannot be empty")]
    [InlineData("test", "English", "G1", "", "Year cannot be empty")]
    [InlineData("test", "Invalid", "G1", "2023", "Subject only supports")]
    [InlineData("test", "English", "Invalid", "2023", "Invalid grade value")]
    [InlineData("empty-hash", "English", "G1", "2023", "File hash cannot be empty")]
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
            Subject = "English",
            Grade = "G1",
            Year = "2023",
            Status = "ready"
        };
        _documentRepoMock.Setup(r => r.GetByTitleAsync(document.Title)).ReturnsAsync(document);
        _documentRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentModel>())).ReturnsAsync(true);
        _searchIndexServiceMock.Setup(s => s.UpdateDocumentMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.UpdateMetadataAsync("test-doc", "English", "G2", "2024", "[\"tag1\"]");

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
            _service.UpdateMetadataAsync("nonexistent", "English", "G1", "2023", null));
        Assert.Contains("Document not found", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadataAsync_DocumentNotReady_ThrowsValidationException()
    {
        // Arrange
        var document = new DocumentModel { Title = "pending-doc", Status = "pending" };
        _documentRepoMock.Setup(r => r.GetByTitleAsync("pending-doc")).ReturnsAsync(document);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(() =>
            _service.UpdateMetadataAsync("pending-doc", "English", "G1", "2023", null));
        Assert.Contains("Document not ready", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadataAsync_InvalidSubject_ThrowsValidationException()
    {
        // Arrange
        var document = new DocumentModel { Title = "ready-doc", Status = "ready", Subject = "English", Grade = "G1", Year = "2023" };
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
        var document = new DocumentModel { Title = "ready-doc", Status = "ready", Subject = "English", Grade = "G1", Year = "2023" };
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
            Status = "ready",
            Subject = "English",
            Grade = "G1",
            Year = "2023"
        };
        _documentRepoMock.Setup(r => r.GetByTitleAsync("ready-doc")).ReturnsAsync(document);
        _documentRepoMock.Setup(r => r.UpdateAsync(It.IsAny<DocumentModel>())).ReturnsAsync(true);

        // Act - only update grade
        var result = await _service.UpdateMetadataAsync("ready-doc", null, "G3", null, null);

        // Assert
        Assert.Equal("English", result.Subject); // unchanged
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
    public async Task DeleteDocumentAsync_ProcessingDocument_CancelsJob()
    {
        // Arrange
        var document = CreateValidDocument();
        document.Id = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = document.Id, Status = "processing" };
        _documentRepoMock.Setup(r => r.GetByTitleAsync(document.Title)).ReturnsAsync(document);
        _documentRepoMock.Setup(r => r.DeleteAsync(document.Id)).ReturnsAsync(true);
        _jobRepoMock.Setup(r => r.GetByDocumentIdAsync(document.Id)).ReturnsAsync(job);

        // Act
        var result = await _service.DeleteDocumentAsync(document.Title);

        // Assert
        Assert.True(result);
        // Verify job was cancelled
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Status == "cancelled")), Times.Once);
    }

    #endregion

    #region Job Management Tests

    [Fact]
    public async Task StartIngestionJobAsync_UpdatesJobAndDocumentStatus()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = "pending" };
        var document = new DocumentModel { Id = documentId, Status = "pending" };

        _jobRepoMock.Setup(r => r.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        // Act
        await _service.StartIngestionJobAsync(job.Id, "v1.0", "5.0");

        // Assert
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Status == "processing")), Times.Once);
        _documentRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentModel>(d => d.Status == "processing")), Times.Once);
    }

    [Fact]
    public async Task CompleteIngestionJobAsync_UpdatesJobAndDocumentToReady()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = "processing" };
        var document = new DocumentModel { Id = documentId, Status = "processing" };

        _jobRepoMock.Setup(r => r.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        // Act
        await _service.CompleteIngestionJobAsync(job.Id);

        // Assert
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Status == "success")), Times.Once);
        _documentRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentModel>(d => d.Status == "ready")), Times.Once);
    }

    [Fact]
    public async Task FailIngestionJobAsync_UpdatesJobAndDocumentToFailed()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = "processing" };
        var document = new DocumentModel { Id = documentId, Status = "processing" };

        _jobRepoMock.Setup(r => r.GetByIdAsync(job.Id)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        // Act
        await _service.FailIngestionJobAsync(job.Id, "解析失败");

        // Assert
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Status == "failed" && j.ErrorMessage == "解析失败")), Times.Once);
        _documentRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentModel>(d => d.Status == "failed")), Times.Once);
    }

    [Fact]
    public async Task CancelIngestionJobAsync_PendingJob_CancelsSuccessfully()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = "pending" };
        _jobRepoMock.Setup(r => r.GetByDocumentIdAsync(documentId)).ReturnsAsync(job);

        // Act
        await _service.CancelIngestionJobAsync(documentId);

        // Assert
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Status == "cancelled")), Times.Once);
    }

    [Fact]
    public async Task CancelIngestionJobAsync_ProcessingJob_CancelsSuccessfully()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = "processing" };
        _jobRepoMock.Setup(r => r.GetByDocumentIdAsync(documentId)).ReturnsAsync(job);

        // Act
        await _service.CancelIngestionJobAsync(documentId);

        // Assert
        _jobRepoMock.Verify(r => r.UpdateAsync(It.Is<DocumentIngestionJobModel>(j => j.Status == "cancelled")), Times.Once);
    }

    [Fact]
    public async Task CancelIngestionJobAsync_SuccessJob_DoesNotCancel()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = Guid.NewGuid(), DocumentId = documentId, Status = "success" };
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
            new() { Id = Guid.NewGuid(), Status = "pending" },
            new() { Id = Guid.NewGuid(), Status = "pending" }
        };
        _jobRepoMock.Setup(r => r.GetByStatusAsync("pending")).ReturnsAsync(pendingJobs);

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
        _documentRepoMock.Setup(r => r.GetListAsync(1, 20, "ready", "English", "G1", "keyword", "2023"))
            .ReturnsAsync((items, 1));

        // Act
        var (result, totalCount) = await _service.GetDocumentListAsync(1, 20, "ready", "English", "G1", "keyword", "2023");

        // Assert
        Assert.Single(result);
        Assert.Equal(1, totalCount);
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
        Subject = "English",
        Year = "2023",
        Tags = "[\"高考\"]"
    };

    #endregion
}
