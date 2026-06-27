using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using global::Ruoyu.Study.DocLibrary.Domain.Models;
using global::Ruoyu.Study.DocLibrary.Domain.Repositories;
using global::Ruoyu.Study.DocLibrary.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class SearchDomainServiceTests
{
    private readonly Mock<ISearchIndexService> _searchIndexServiceMock;
    private readonly Mock<IDocumentRepository> _documentRepoMock;
    private readonly Mock<IDocumentSegmentRepository> _segmentRepoMock;
    private readonly Mock<IQuestionSegmentRepository> _questionRepoMock;
    private readonly Mock<IDocumentPageRepository> _pageRepoMock;
    private readonly SearchDomainService _service;

    public SearchDomainServiceTests()
    {
        _searchIndexServiceMock = new Mock<ISearchIndexService>();
        _documentRepoMock = new Mock<IDocumentRepository>();
        _segmentRepoMock = new Mock<IDocumentSegmentRepository>();
        _questionRepoMock = new Mock<IQuestionSegmentRepository>();
        _pageRepoMock = new Mock<IDocumentPageRepository>();

        var loggerMock = new Mock<ILogger<SearchDomainService>>();

        _service = new SearchDomainService(
            _searchIndexServiceMock.Object,
            _documentRepoMock.Object,
            _segmentRepoMock.Object,
            _questionRepoMock.Object,
            _pageRepoMock.Object,
            loggerMock.Object);
    }

    #region ExactSearchAsync Tests

    [Fact]
    public async Task ExactSearchAsync_UsesOpenSearch_WhenAvailable()
    {
        // Arrange
        var expectedResults = new List<SearchResultModel>
        {
            new() { DocumentName = "test.pdf", PageNumber = 1, AssociatedText = "Hello world", Score = 1.0, MatchType = "exact_phrase", SegmentId = "p1-s1" }
        };
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync("hello", true, null, 50, null))
            .ReturnsAsync((expectedResults, 1, null));

        // Act
        var (results, totalCount, nextToken) = await _service.ExactSearchAsync("hello", true, null, 50, null);

        // Assert
        Assert.Single(results);
        Assert.Equal(1, totalCount);
        Assert.Equal("test.pdf", results[0].DocumentName);
    }

    [Fact]
    public async Task ExactSearchAsync_FallsBackToDatabase_WhenOpenSearchFails()
    {
        // Arrange
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ThrowsAsync(new Exception("OpenSearch unavailable"));

        var docId = Guid.NewGuid();
        var pageId = Guid.NewGuid();

        var segment = new DocumentSegmentModel
        {
            Id = Guid.NewGuid(),
            DocumentId = docId,
            PageId = pageId,
            SentenceId = "p1-s1",
            Text = "Hello world",
            SegmentType = "sentence"
        };
        _segmentRepoMock.Setup(r => r.SearchByTextAsync("hello", 50, 0, DocumentStatus.Ready, null, null, null))
            .ReturnsAsync((new List<DocumentSegmentModel> { segment }, 1));

        _questionRepoMock.Setup(r => r.SearchByStemAsync("hello", 50, 0, DocumentStatus.Ready, null, null, null))
            .ReturnsAsync((new List<QuestionSegmentModel>(), 0));

        _documentRepoMock.Setup(r => r.GetByIdAsync(docId))
            .ReturnsAsync(new DocumentModel { Id = docId, Title = "test.pdf", Status = DocumentStatus.Ready });

        var page = new DocumentPageModel { Id = pageId, PageNumber = 1 };
        _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(docId)).ReturnsAsync(new List<DocumentPageModel> { page });

        // Act
        var (results, totalCount, nextToken) = await _service.ExactSearchAsync("hello", true, null, 50, null);

        // Assert
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task ExactSearchAsync_OnlySearchesReadyDocuments()
    {
        // Arrange
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ThrowsAsync(new Exception("OpenSearch unavailable"));

        var readyDocId = Guid.NewGuid();
        var pageId = Guid.NewGuid();

        // Only ready document's segment will be returned by SearchByTextAsync (DB filters by status)
        var segment = new DocumentSegmentModel
        {
            Id = Guid.NewGuid(),
            DocumentId = readyDocId,
            PageId = pageId,
            SentenceId = "s1",
            Text = "Hello world",
            SegmentType = "sentence"
        };
        _segmentRepoMock.Setup(r => r.SearchByTextAsync("hello", 50, 0, DocumentStatus.Ready, null, null, null))
            .ReturnsAsync((new List<DocumentSegmentModel> { segment }, 1));

        _questionRepoMock.Setup(r => r.SearchByStemAsync("hello", 50, 0, DocumentStatus.Ready, null, null, null))
            .ReturnsAsync((new List<QuestionSegmentModel>(), 0));

        _documentRepoMock.Setup(r => r.GetByIdAsync(readyDocId))
            .ReturnsAsync(new DocumentModel { Id = readyDocId, Title = "ready-doc", Status = DocumentStatus.Ready });

        _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(readyDocId)).ReturnsAsync(new List<DocumentPageModel>
        {
            new() { Id = pageId, PageNumber = 1 }
        });

        // Act
        var (results, _, _) = await _service.ExactSearchAsync("hello", false, null, 50, null);

        // Assert - only ready doc should appear in results
        Assert.All(results, r => Assert.Equal("ready-doc", r.DocumentName));
    }

    #endregion

    #region Filter Tests

    [Fact]
    public async Task ExactSearchAsync_WithFilter_PassesFilterCorrectly()
    {
        // Arrange - OpenSearch returns results, so no fallback to database
        var filter = new SearchFilterModel
        {
            Subject = "英语",
            Grade = "G10",
            Year = "2023",
            DocumentTitle = "test.pdf"
        };
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync("hello", false, filter, 20, null))
            .ReturnsAsync((new List<SearchResultModel> { new() { DocumentName = "test.pdf", Score = 1.0 } }, 1, null));

        // Act
        var (results, totalCount, _) = await _service.ExactSearchAsync("hello", false, filter, 20, null);

        // Assert
        Assert.Single(results);
        Assert.Equal(1, totalCount);
    }

    #endregion

    #region Pagination Tests

    [Fact]
    public async Task ExactSearchAsync_WithPagination_ReturnsNextToken()
    {
        // Arrange
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ThrowsAsync(new Exception("OpenSearch unavailable"));

        var segments = new List<DocumentSegmentModel>();
        var docIds = new List<Guid>();
        for (int i = 0; i < 10; i++)
        {
            var docId = Guid.NewGuid();
            var pageId = Guid.NewGuid();
            docIds.Add(docId);
            segments.Add(new DocumentSegmentModel
            {
                Id = Guid.NewGuid(),
                DocumentId = docId,
                PageId = pageId,
                SentenceId = $"s{i}",
                Text = "Hello world",
                SegmentType = "sentence"
            });

            _documentRepoMock.Setup(r => r.GetByIdAsync(docId))
                .ReturnsAsync(new DocumentModel { Id = docId, Title = $"doc{i}.pdf", Status = DocumentStatus.Ready });
            _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(docId)).ReturnsAsync(new List<DocumentPageModel>
            {
                new() { Id = pageId, PageNumber = 1 }
            });
        }

        // Page 1: first 5 segments
        _segmentRepoMock.Setup(r => r.SearchByTextAsync("hello", 5, 0, DocumentStatus.Ready, null, null, null))
            .ReturnsAsync((segments.Take(5).ToList(), 10));
        // Page 2: next 5 segments
        _segmentRepoMock.Setup(r => r.SearchByTextAsync("hello", 5, 5, DocumentStatus.Ready, null, null, null))
            .ReturnsAsync((segments.Skip(5).Take(5).ToList(), 10));

        _questionRepoMock.Setup(r => r.SearchByStemAsync("hello", It.IsAny<int>(), It.IsAny<int>(), DocumentStatus.Ready, null, null, null))
            .ReturnsAsync((new List<QuestionSegmentModel>(), 0));

        // Act - request 5 per page
        var (page1, totalCount1, nextToken) = await _service.ExactSearchAsync("hello", false, null, 5, null);

        // Assert
        Assert.Equal(5, page1.Count);
        Assert.Equal(10, totalCount1);
        Assert.NotNull(nextToken);

        // Page 2
        var (page2, totalCount2, nextToken2) = await _service.ExactSearchAsync("hello", false, null, 5, nextToken);
        Assert.Equal(5, page2.Count);
        Assert.Equal(10, totalCount2);
        Assert.Null(nextToken2); // Last page
    }

    #endregion

    #region Question Search Tests

    [Fact]
    public async Task ExactSearchAsync_SearchesQuestionSegments()
    {
        // Arrange (database fallback)
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ThrowsAsync(new Exception("OpenSearch unavailable"));

        var docId = Guid.NewGuid();
        var pageId = Guid.NewGuid();

        _segmentRepoMock.Setup(r => r.SearchByTextAsync("capital", 50, 0, DocumentStatus.Ready, null, null, null))
            .ReturnsAsync((new List<DocumentSegmentModel>(), 0));

        _questionRepoMock.Setup(r => r.SearchByStemAsync("capital", 50, 0, DocumentStatus.Ready, null, null, null))
            .ReturnsAsync((new List<QuestionSegmentModel>
            {
                new() { Id = Guid.NewGuid(), DocumentId = docId, PageId = pageId, QuestionId = "q1", Stem = "What is the capital of France?" }
            }, 1));

        _documentRepoMock.Setup(r => r.GetByIdAsync(docId))
            .ReturnsAsync(new DocumentModel { Id = docId, Title = "exam.pdf", Status = DocumentStatus.Ready });

        _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(docId)).ReturnsAsync(new List<DocumentPageModel>
        {
            new() { Id = pageId, PageNumber = 1 }
        });

        // Act
        var (results, _, _) = await _service.ExactSearchAsync("capital", false, null, 50, null);

        // Assert
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.AssociatedText.Contains("France"));
        Assert.Contains(results, r => r.SegmentId == "q1");
    }

    #endregion
}
