using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests;

public class SearchDomainServiceTests
{
    private readonly Mock<ISearchIndexService> _searchIndexServiceMock;
    private readonly Mock<IDocumentRepository> _documentRepoMock;
    private readonly Mock<IDocumentSegmentRepository> _segmentRepoMock;
    private readonly Mock<IQuestionSegmentRepository> _questionRepoMock;
    private readonly Mock<IDocumentPageRepository> _pageRepoMock;
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly SearchDomainService _service;

    public SearchDomainServiceTests()
    {
        _searchIndexServiceMock = new Mock<ISearchIndexService>();
        _documentRepoMock = new Mock<IDocumentRepository>();
        _segmentRepoMock = new Mock<IDocumentSegmentRepository>();
        _questionRepoMock = new Mock<IQuestionSegmentRepository>();
        _pageRepoMock = new Mock<IDocumentPageRepository>();

        var loggerMock = new Mock<ILogger<SearchDomainService>>();

        // Setup service provider to return ISearchIndexService
        _serviceProviderMock = new Mock<IServiceProvider>();
        _serviceProviderMock
            .Setup(sp => sp.GetService(typeof(ISearchIndexService)))
            .Returns(_searchIndexServiceMock.Object);

        _service = new SearchDomainService(
            _serviceProviderMock.Object,
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
            new() { DocumentName = "test.pdf", PageNumber = 1, AssociatedText = "Hello world", Score = 1.0, MatchType = "exact_phrase", SegmentId = "p1-b1-s1" }
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

        var documents = new List<DocumentModel>
        {
            new() { Id = Guid.NewGuid(), Title = "test.pdf", Status = "ready", Subject = "英语", Grade = "G10", Year = "2023" }
        };
        _documentRepoMock
            .Setup(r => r.GetListAsync(It.IsAny<int>(), It.IsAny<int>(), null, null, null, null, null))
            .ReturnsAsync((documents, 1));

        var segment = new DocumentSegmentModel
        {
            Id = Guid.NewGuid(),
            DocumentId = documents[0].Id,
            PageId = Guid.NewGuid(),
            SentenceId = "p1-b1-s1",
            Text = "Hello world",
            BlockId = "p1-b1",
            SegmentType = "sentence"
        };
        _segmentRepoMock.Setup(r => r.GetByDocumentIdAsync(documents[0].Id)).ReturnsAsync(new List<DocumentSegmentModel> { segment });

        var page = new DocumentPageModel { Id = segment.PageId, PageNumber = 1 };
        _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(documents[0].Id)).ReturnsAsync(new List<DocumentPageModel> { page });

        _questionRepoMock.Setup(r => r.GetByDocumentIdAsync(documents[0].Id)).ReturnsAsync(new List<QuestionSegmentModel>());

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

        var documents = new List<DocumentModel>
        {
            new() { Id = Guid.NewGuid(), Title = "ready-doc", Status = "ready", Subject = "英语", Grade = "G10", Year = "2023" },
            new() { Id = Guid.NewGuid(), Title = "pending-doc", Status = "pending", Subject = "英语", Grade = "G10", Year = "2023" },
            new() { Id = Guid.NewGuid(), Title = "failed-doc", Status = "failed", Subject = "英语", Grade = "G10", Year = "2023" }
        };
        _documentRepoMock
            .Setup(r => r.GetListAsync(It.IsAny<int>(), It.IsAny<int>(), null, null, null, null, null))
            .ReturnsAsync((documents, 3));

        // Setup segments for all documents
        foreach (var doc in documents)
        {
            var pageId = Guid.NewGuid();
            _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(doc.Id)).ReturnsAsync(new List<DocumentPageModel>
            {
                new() { Id = pageId, PageNumber = 1 }
            });
            _segmentRepoMock.Setup(r => r.GetByDocumentIdAsync(doc.Id)).ReturnsAsync(new List<DocumentSegmentModel>
            {
                new() { Id = Guid.NewGuid(), DocumentId = doc.Id, PageId = pageId, SentenceId = "s1", Text = "Hello world", BlockId = "b1", SegmentType = "sentence" }
            });
            _questionRepoMock.Setup(r => r.GetByDocumentIdAsync(doc.Id)).ReturnsAsync(new List<QuestionSegmentModel>());
        }

        // Act
        var (results, _, _) = await _service.ExactSearchAsync("hello", false, null, 50, null);

        // Assert - only ready doc should appear in results
        Assert.All(results, r => Assert.Equal("ready-doc", r.DocumentName));
    }

    #endregion

    #region HybridSearchAsync Tests

    [Fact]
    public async Task HybridSearchAsync_UsesOpenSearch_WhenAvailable()
    {
        // Arrange
        var expectedResults = new List<SearchResultModel>
        {
            new() { DocumentName = "test.pdf", PageNumber = 1, AssociatedText = "Hello world", Score = 1.0, MatchType = "exact_phrase", SegmentId = "p1-b1-s1" }
        };
        _searchIndexServiceMock
            .Setup(s => s.HybridSearchAsync("hello", true, 50, 20, null, 50, null))
            .ReturnsAsync((expectedResults, 1, null));

        // Act
        var (results, totalCount, nextToken) = await _service.HybridSearchAsync("hello", true, 50, 20, null, 50, null);

        // Assert
        Assert.Single(results);
        Assert.Equal(1, totalCount);
    }

    [Fact]
    public async Task HybridSearchAsync_FallsBackToDatabase_WhenOpenSearchFails()
    {
        // Arrange
        _searchIndexServiceMock
            .Setup(s => s.HybridSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ThrowsAsync(new Exception("OpenSearch unavailable"));

        var documents = new List<DocumentModel>
        {
            new() { Id = Guid.NewGuid(), Title = "test.pdf", Status = "ready", Subject = "英语", Grade = "G10", Year = "2023" }
        };
        _documentRepoMock
            .Setup(r => r.GetListAsync(It.IsAny<int>(), It.IsAny<int>(), null, null, null, null, null))
            .ReturnsAsync((documents, 1));

        var pageId = Guid.NewGuid();
        _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(documents[0].Id)).ReturnsAsync(new List<DocumentPageModel>
        {
            new() { Id = pageId, PageNumber = 1 }
        });
        _segmentRepoMock.Setup(r => r.GetByDocumentIdAsync(documents[0].Id)).ReturnsAsync(new List<DocumentSegmentModel>
        {
            new() { Id = Guid.NewGuid(), DocumentId = documents[0].Id, PageId = pageId, SentenceId = "s1", Text = "Hello world", BlockId = "b1", SegmentType = "sentence" }
        });
        _questionRepoMock.Setup(r => r.GetByDocumentIdAsync(documents[0].Id)).ReturnsAsync(new List<QuestionSegmentModel>());

        // Act - use phrase: false so fallback marks results as stem_match
        var (results, _, _) = await _service.HybridSearchAsync("hello", false, 50, 20, null, 50, null);

        // Assert - fallback marks non-phrase results as stem_match
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal("stem_match", r.MatchType));
    }

    #endregion

    #region Filter Tests

    [Fact]
    public async Task ExactSearchAsync_WithFilter_PassesFilterCorrectly()
    {
        // Arrange
        var filter = new SearchFilterModel
        {
            Subject = "英语",
            Grade = "G10",
            Year = "2023",
            DocumentTitle = "test.pdf"
        };
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync("hello", false, filter, 20, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        // Act
        var (results, totalCount, _) = await _service.ExactSearchAsync("hello", false, filter, 20, null);

        // Assert
        Assert.Empty(results);
        Assert.Equal(0, totalCount);
    }

    #endregion

    #region Pagination Tests

    [Fact]
    public async Task ExactSearchAsync_WithPagination_ReturnsNextToken()
    {
        // Arrange
        var results = new List<SearchResultModel>();
        for (int i = 0; i < 10; i++)
        {
            results.Add(new SearchResultModel
            {
                DocumentName = $"doc{i}.pdf",
                PageNumber = 1,
                AssociatedText = $"Result {i}",
                Score = 1.0,
                MatchType = "exact_word",
                SegmentId = $"s{i}"
            });
        }

        // Simulate that OpenSearch is not available - we need database fallback
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ThrowsAsync(new Exception("OpenSearch unavailable"));

        var documents = new List<DocumentModel>();
        for (int i = 0; i < 10; i++)
        {
            documents.Add(new DocumentModel
            {
                Id = Guid.NewGuid(),
                Title = $"doc{i}.pdf",
                Status = "ready",
                Subject = "英语",
                Grade = "G10",
                Year = "2023"
            });
        }
        _documentRepoMock
            .Setup(r => r.GetListAsync(It.IsAny<int>(), It.IsAny<int>(), null, null, null, null, null))
            .ReturnsAsync((documents, 10));

        foreach (var doc in documents)
        {
            var pageId = Guid.NewGuid();
            _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(doc.Id)).ReturnsAsync(new List<DocumentPageModel>
            {
                new() { Id = pageId, PageNumber = 1 }
            });
            _segmentRepoMock.Setup(r => r.GetByDocumentIdAsync(doc.Id)).ReturnsAsync(new List<DocumentSegmentModel>
            {
                new() { Id = Guid.NewGuid(), DocumentId = doc.Id, PageId = pageId, SentenceId = $"s-{doc.Title}", Text = "Hello world", BlockId = "b1", SegmentType = "sentence" }
            });
            _questionRepoMock.Setup(r => r.GetByDocumentIdAsync(doc.Id)).ReturnsAsync(new List<QuestionSegmentModel>());
        }

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

        var doc = new DocumentModel { Id = Guid.NewGuid(), Title = "exam.pdf", Status = "ready", Subject = "英语", Grade = "G10", Year = "2023" };
        _documentRepoMock
            .Setup(r => r.GetListAsync(It.IsAny<int>(), It.IsAny<int>(), null, null, null, null, null))
            .ReturnsAsync((new List<DocumentModel> { doc }, 1));

        var pageId = Guid.NewGuid();
        _pageRepoMock.Setup(r => r.GetByDocumentIdAsync(doc.Id)).ReturnsAsync(new List<DocumentPageModel>
        {
            new() { Id = pageId, PageNumber = 1 }
        });
        _segmentRepoMock.Setup(r => r.GetByDocumentIdAsync(doc.Id)).ReturnsAsync(new List<DocumentSegmentModel>());
        _questionRepoMock.Setup(r => r.GetByDocumentIdAsync(doc.Id)).ReturnsAsync(new List<QuestionSegmentModel>
        {
            new() { Id = Guid.NewGuid(), DocumentId = doc.Id, PageId = pageId, QuestionId = "q1", Stem = "What is the capital of France?" }
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
