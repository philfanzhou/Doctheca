using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using global::Doctheca.Domain.Models;
using global::Doctheca.Domain.Repositories;
using global::Doctheca.Domain.Services;
using Xunit;

namespace Doctheca.Tests;

public class SearchDomainServiceTests
{
    private readonly Mock<ISearchIndexService> _searchIndexServiceMock;
    private readonly SearchDomainService _service;

    public SearchDomainServiceTests()
    {
        _searchIndexServiceMock = new Mock<ISearchIndexService>();
        var loggerMock = new Mock<ILogger<SearchDomainService>>();

        _service = new SearchDomainService(
            _searchIndexServiceMock.Object,
            loggerMock.Object);
    }

    #region ExactSearchAsync Tests

    [Fact]
    public async Task ExactSearchAsync_DelegatesToSearchIndexService()
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
        Assert.Null(nextToken);
    }

    [Fact]
    public async Task ExactSearchAsync_PropagatesFilterToIndexService()
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
            .ReturnsAsync((new List<SearchResultModel> { new() { DocumentName = "test.pdf", Score = 1.0 } }, 1, null));

        // Act
        var (results, totalCount, _) = await _service.ExactSearchAsync("hello", false, filter, 20, null);

        // Assert
        Assert.Single(results);
        Assert.Equal(1, totalCount);
        _searchIndexServiceMock.Verify(s => s.ExactSearchAsync("hello", false, filter, 20, null), Times.Once);
    }

    [Fact]
    public async Task ExactSearchAsync_PropagatesPageToken()
    {
        // Arrange
        var pageToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("[1.0, \"doc1\"]"));
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync("hello", false, null, 5, pageToken))
            .ReturnsAsync((new List<SearchResultModel>(), 10, null));

        // Act
        var (results, _, nextToken) = await _service.ExactSearchAsync("hello", false, null, 5, pageToken);

        // Assert
        Assert.Empty(results);
        Assert.Null(nextToken);
        _searchIndexServiceMock.Verify(s => s.ExactSearchAsync("hello", false, null, 5, pageToken), Times.Once);
    }

    [Fact]
    public async Task ExactSearchAsync_WhenIndexServiceThrows_ReturnsEmptyResults()
    {
        // Arrange
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("OpenSearch unavailable"));

        // Act
        var (results, totalCount, nextToken) = await _service.ExactSearchAsync("hello", false, null, 50, null);

        // Assert — graceful degradation: return empty results instead of throwing
        Assert.Empty(results);
        Assert.Equal(0, totalCount);
        Assert.Null(nextToken);
    }

    [Fact]
    public async Task ExactSearchAsync_ReturnsEmptyResultsWhenNoMatch()
    {
        // Arrange
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync("nomatch", false, null, 50, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        // Act
        var (results, totalCount, nextToken) = await _service.ExactSearchAsync("nomatch", false, null, 50, null);

        // Assert
        Assert.Empty(results);
        Assert.Equal(0, totalCount);
        Assert.Null(nextToken);
    }

    // ==================== [Gen-2] minerU filter propagation ====================

    [Fact]
    public async Task ExactSearchAsync_PropagatesMinerUFilterToIndexService()
    {
        // Arrange
        var filter = new SearchFilterModel
        {
            BlockType = "image",
            PageNumber = 3,
            HasImage = true,
            ParseId = Guid.Parse("12345678-1234-1234-1234-123456789012")
        };
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync("keyword", false, filter, 20, null))
            .ReturnsAsync((new List<SearchResultModel>
            {
                new() { DocumentName = "doc.pdf", Score = 1.0, BlockData = "{\"type\":\"image\"}" }
            }, 1, null));

        // Act
        var (results, totalCount, _) = await _service.ExactSearchAsync("keyword", false, filter, 20, null);

        // Assert — minerU filter passed through to index service
        Assert.Single(results);
        Assert.Equal(1, totalCount);
        _searchIndexServiceMock.Verify(s => s.ExactSearchAsync("keyword", false, filter, 20, null), Times.Once);
    }

    [Fact]
    public async Task ExactSearchAsync_WithMinerUFilterAndIndexServiceThrow_ReturnsEmpty()
    {
        // Arrange — degradation path: index service throws, minerU filter present, still returns empty
        var filter = new SearchFilterModel { BlockType = "text", HasImage = false };
        _searchIndexServiceMock
            .Setup(s => s.ExactSearchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchFilterModel?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("OpenSearch unavailable"));

        // Act
        var (results, totalCount, nextToken) = await _service.ExactSearchAsync("keyword", false, filter, 50, null);

        // Assert — graceful degradation with minerU filter
        Assert.Empty(results);
        Assert.Equal(0, totalCount);
        Assert.Null(nextToken);
    }

    #endregion
}
