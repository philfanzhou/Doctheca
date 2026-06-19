using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocRetrieval.Domain.Exceptions;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests;

public class DocumentRefinementTests
{
    private readonly Mock<IDocumentRepository> _documentRepoMock = new();
    private readonly Mock<IDocumentPageRepository> _pageRepoMock = new();
    private readonly Mock<IDocumentSegmentRepository> _segmentRepoMock = new();
    private readonly Mock<IQuestionSegmentRepository> _questionRepoMock = new();
    private readonly Mock<IDocumentOccurrenceRepository> _occurrenceRepoMock = new();
    private readonly Mock<IDocumentIngestionJobRepository> _jobRepoMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ISearchIndexService> _searchIndexServiceMock = new();
    private readonly Mock<IOssService> _ossServiceMock = new();
    private readonly Mock<ILlmSegmentationService> _llmSegmentationMock = new();
    private readonly Mock<ILogger<DocumentDomainService>> _loggerMock = new();
    private readonly DocumentDomainService _service;

    public DocumentRefinementTests()
    {
        _service = new DocumentDomainService(
            _documentRepoMock.Object,
            _pageRepoMock.Object,
            _segmentRepoMock.Object,
            _questionRepoMock.Object,
            _occurrenceRepoMock.Object,
            _jobRepoMock.Object,
            _unitOfWorkMock.Object,
            _loggerMock.Object,
            _searchIndexServiceMock.Object,
            _ossServiceMock.Object,
            _llmSegmentationMock.Object);
    }

    #region GetSegmentsAsync Tests

    [Fact]
    public async Task GetSegmentsAsync_ValidDocument_ReturnsSegments()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var document = CreateReadyDocument(documentId);
        document.LlmProfileJson = JsonSerializer.Serialize(new DocumentProfile
        {
            Subject = "English",
            DocType = "教材",
            SegmentStrategy = SegmentTypes.Sentence
        });

        var pages = new List<DocumentPageModel>
        {
            new() { Id = pageId, DocumentId = documentId, PageNumber = 1 }
        };
        var segments = new List<DocumentSegmentModel>
        {
            new() { Id = Guid.NewGuid(), DocumentId = documentId, PageId = pageId, SentenceId = "p1-b1-s1", SegmentType = "sentence", Text = "Hello world.", StartOffset = 0, EndOffset = 12 },
            new() { Id = Guid.NewGuid(), DocumentId = documentId, PageId = pageId, SentenceId = "p1-b1-s2", SegmentType = "sentence", Text = "How are you?", StartOffset = 13, EndOffset = 25 }
        };

        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);
        _segmentRepoMock.Setup(r => r.GetByDocumentIdAsync(documentId)).ReturnsAsync(segments);
        _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(documentId)).ReturnsAsync(pages);

        // Act
        var result = await _service.GetSegmentsAsync(documentId);

        // Assert
        Assert.Equal(documentId, result.DocumentId);
        Assert.Equal("Test Document", result.Title);
        Assert.Equal(2, result.TotalCount);
        Assert.NotNull(result.Profile);
        Assert.Equal("English", result.Profile!.Subject);
        Assert.Equal("sentence", result.Profile.SegmentStrategy);
    }

    [Fact]
    public async Task GetSegmentsAsync_NoProfile_ReturnsNullProfile()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var document = CreateReadyDocument(documentId);
        document.LlmProfileJson = null;

        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);
        _segmentRepoMock.Setup(r => r.GetByDocumentIdAsync(documentId)).ReturnsAsync([]);
        _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(documentId)).ReturnsAsync([]);

        // Act
        var result = await _service.GetSegmentsAsync(documentId);

        // Assert
        Assert.Null(result.Profile);
    }

    [Fact]
    public async Task GetSegmentsAsync_DocumentNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync((DocumentModel?)null);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.GetSegmentsAsync(documentId));
    }

    #endregion

    #region RefineSegmentsAsync Tests

    [Fact]
    public async Task RefineSegmentsAsync_MergeCorrection_Success()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var pageId = Guid.NewGuid();
        var document = CreateReadyDocument(documentId);
        document.LlmProfileJson = JsonSerializer.Serialize(new DocumentProfile { Subject = "English", SegmentStrategy = SegmentTypes.Sentence });

        var segments = new List<DocumentSegmentModel>
        {
            new() { Id = Guid.NewGuid(), DocumentId = documentId, PageId = pageId, SentenceId = "p1-b1-s1", SegmentType = "sentence", Text = "Hello", StartOffset = 0, EndOffset = 5 },
            new() { Id = Guid.NewGuid(), DocumentId = documentId, PageId = pageId, SentenceId = "p1-b1-s2", SegmentType = "sentence", Text = "world.", StartOffset = 6, EndOffset = 12 }
        };

        SetupMocksForRefinement(documentId, document, pageId, segments);

        var newProfile = new DocumentProfile { Subject = "English", SegmentStrategy = SegmentTypes.Sentence };
        var newSegments = new List<SegmentResult>
        {
            new() { Text = "Hello world.", StartOffset = 0, EndOffset = 12, SegmentType = "sentence" }
        };

        _llmSegmentationMock.Setup(s => s.RefineProfileAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<List<SegmentCorrection>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newProfile);
        _llmSegmentationMock.Setup(s => s.RefineSegmentTextAsync(It.IsAny<string>(), It.IsAny<DocumentProfile>(), It.IsAny<List<SegmentCorrection>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newSegments);

        var corrections = new List<SegmentCorrection>
        {
            new() { OriginalSentenceIds = ["p1-b1-s1", "p1-b1-s2"], Action = "merge", NewText = "Hello world.", NewSegmentType = "sentence" }
        };

        // Act
        var result = await _service.RefineSegmentsAsync(documentId, corrections);

        // Assert
        Assert.Equal(documentId, result.DocumentId);
        Assert.Equal(1, result.CorrectionCount);
        Assert.Contains("completed", result.Message, StringComparison.OrdinalIgnoreCase);
        _segmentRepoMock.Verify(r => r.DeleteByDocumentIdAsync(documentId), Times.Once);
        _segmentRepoMock.Verify(r => r.AddRangeAsync(It.IsAny<IEnumerable<DocumentSegmentModel>>()), Times.Once);
        _occurrenceRepoMock.Verify(r => r.DeleteByDocumentIdAsync(documentId), Times.Once);
        _searchIndexServiceMock.Verify(s => s.DeleteDocumentIndexAsync(documentId), Times.Once);
        _searchIndexServiceMock.Verify(s => s.IndexDocumentSegmentsAsync(documentId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RefineSegmentsAsync_TooManyCorrections_ThrowsValidationException()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var corrections = Enumerable.Range(0, 21)
            .Select(i => new SegmentCorrection { OriginalSentenceIds = [$"s{i}"], Action = "retype", NewSegmentType = "concept" })
            .ToList();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.RefineSegmentsAsync(documentId, corrections));
        Assert.Contains("Too many", ex.Message);
    }

    [Fact]
    public async Task RefineSegmentsAsync_EmptyCorrections_ThrowsValidationException()
    {
        // Arrange
        var documentId = Guid.NewGuid();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.RefineSegmentsAsync(documentId, []));
        Assert.Contains("cannot be empty", ex.Message);
    }

    [Fact]
    public async Task RefineSegmentsAsync_DocumentNotReady_ThrowsValidationException()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var document = CreateReadyDocument(documentId);
        document.Status = DocumentStatus.Processing;

        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        var corrections = new List<SegmentCorrection>
        {
            new() { OriginalSentenceIds = ["s1"], Action = "retype", NewSegmentType = "concept" }
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.RefineSegmentsAsync(documentId, corrections));
        Assert.Contains("not ready", ex.Message);
    }

    [Fact]
    public async Task RefineSegmentsAsync_LlmNotConfigured_ThrowsValidationException()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var document = CreateReadyDocument(documentId);

        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        // Create service without LLM
        var serviceWithoutLlm = new DocumentDomainService(
            _documentRepoMock.Object,
            _pageRepoMock.Object,
            _segmentRepoMock.Object,
            _questionRepoMock.Object,
            _occurrenceRepoMock.Object,
            _jobRepoMock.Object,
            _unitOfWorkMock.Object,
            _loggerMock.Object,
            _searchIndexServiceMock.Object,
            _ossServiceMock.Object,
            llmSegmentation: null);

        var corrections = new List<SegmentCorrection>
        {
            new() { OriginalSentenceIds = ["s1"], Action = "retype", NewSegmentType = "concept" }
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => serviceWithoutLlm.RefineSegmentsAsync(documentId, corrections));
        Assert.Contains("not configured", ex.Message);
    }

    #endregion

    #region Helper Methods

    private static DocumentModel CreateReadyDocument(Guid id)
    {
        return new DocumentModel
        {
            Id = id,
            Title = "Test Document",
            SourceType = "pdf",
            FileHash = "abc123",
            FilePath = "docretrieval/test.pdf",
            FileSize = 1024,
            Language = "en",
            Grade = "G10",
            Subject = "English",
            Year = "2024",
            Status = DocumentStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private void SetupMocksForRefinement(Guid documentId, DocumentModel document, Guid pageId, List<DocumentSegmentModel> segments)
    {
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);
        _segmentRepoMock.Setup(r => r.GetByDocumentIdAsync(documentId)).ReturnsAsync(segments);
        _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(documentId)).ReturnsAsync(
            [new DocumentPageModel { Id = pageId, DocumentId = documentId, PageNumber = 1 }]);

        var fileContent = "Hello world. This is a test document."u8.ToArray();
        var fileStream = new MemoryStream(fileContent);
        _ossServiceMock.Setup(s => s.DownloadAsync(document.FilePath)).ReturnsAsync(fileStream);
    }

    #endregion
}
