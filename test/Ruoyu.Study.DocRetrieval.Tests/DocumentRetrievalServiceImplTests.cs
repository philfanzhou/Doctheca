using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Contract.Protos;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Ruoyu.Study.DocRetrieval.Service;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests;

public class DocumentRetrievalServiceImplTests
{
    private readonly Mock<ISearchDomainService> _searchServiceMock;
    private readonly Mock<ILogger<DocumentRetrievalServiceImpl>> _loggerMock;
    private readonly DocumentRetrievalServiceImpl _service;

    public DocumentRetrievalServiceImplTests()
    {
        _searchServiceMock = new Mock<ISearchDomainService>();
        _loggerMock = new Mock<ILogger<DocumentRetrievalServiceImpl>>();
        _service = new DocumentRetrievalServiceImpl(_searchServiceMock.Object, _loggerMock.Object);
    }

    #region ExactSearch Tests

    [Fact]
    public async Task ExactSearch_ValidWordQuery_ReturnsResults()
    {
        // Arrange
        var expectedResults = new List<SearchResultModel>
        {
            new()
            {
                DocumentName = "test.pdf",
                PageNumber = 1,
                AssociatedText = "Hello world",
                Score = 0.95,
                MatchType = "exact_word",
                SegmentId = "p1-b1-s1",
                StartOffset = 0,
                EndOffset = 5
            }
        };

        _searchServiceMock
            .Setup(s => s.ExactSearchAsync("hello", false, null, 50, null))
            .ReturnsAsync((expectedResults, 1, null));

        // Act
        var response = await _service.ExactSearch(new ExactSearchRequest
        {
            Query = "hello",
            Phrase = false
        }, CreateTestContext());

        // Assert
        Assert.Single(response.Results);
        Assert.Equal(1, response.TotalCount);
        Assert.Equal("test.pdf", response.Results[0].DocumentName);
        Assert.Equal(1, response.Results[0].PageNumber);
        Assert.Equal("Hello world", response.Results[0].AssociatedText);
    }

    [Fact]
    public async Task ExactSearch_ValidPhraseQuery_ReturnsResults()
    {
        // Arrange
        var expectedResults = new List<SearchResultModel>
        {
            new()
            {
                DocumentName = "test.pdf",
                PageNumber = 1,
                AssociatedText = "Students take notes in class.",
                Score = 1.0,
                MatchType = "exact_phrase",
                SegmentId = "p1-b1-s1",
                StartOffset = 9,
                EndOffset = 19
            }
        };

        _searchServiceMock
            .Setup(s => s.ExactSearchAsync("take notes", true, null, 50, null))
            .ReturnsAsync((expectedResults, 1, null));

        // Act
        var response = await _service.ExactSearch(new ExactSearchRequest
        {
            Query = "take notes",
            Phrase = true
        }, CreateTestContext());

        // Assert
        Assert.Single(response.Results);
        Assert.Equal("exact_phrase", response.Results[0].MatchType);
    }

    [Fact]
    public async Task ExactSearch_EmptyQuery_ThrowsRpcException()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _service.ExactSearch(new ExactSearchRequest { Query = "", Phrase = false }, CreateTestContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("DOCRETRIEVAL_QUERY_REQUIRED", ex.Status.Detail);
    }

    [Fact]
    public async Task ExactSearch_WhitespaceQuery_ThrowsRpcException()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _service.ExactSearch(new ExactSearchRequest { Query = "   ", Phrase = false }, CreateTestContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("DOCRETRIEVAL_QUERY_REQUIRED", ex.Status.Detail);
    }

    [Fact]
    public async Task ExactSearch_QueryTooLong_ThrowsRpcException()
    {
        // Arrange
        var longQuery = new string('a', 201);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _service.ExactSearch(new ExactSearchRequest { Query = longQuery, Phrase = false }, CreateTestContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("DOCRETRIEVAL_QUERY_TOO_LONG", ex.Status.Detail);
    }

    [Fact]
    public async Task ExactSearch_PageSizeExceedsMax_ThrowsRpcException()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _service.ExactSearch(new ExactSearchRequest { Query = "hello", PageSize = 101 }, CreateTestContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("DOCRETRIEVAL_PAGE_SIZE_INVALID", ex.Status.Detail);
    }

    [Fact]
    public async Task ExactSearch_DefaultPageSize_Is50()
    {
        // Arrange
        _searchServiceMock
            .Setup(s => s.ExactSearchAsync("hello", false, null, 50, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        // Act
        var response = await _service.ExactSearch(new ExactSearchRequest
        {
            Query = "hello",
            Phrase = false,
            PageSize = 0 // Should default to 50
        }, CreateTestContext());

        // Assert
        _searchServiceMock.Verify(s => s.ExactSearchAsync("hello", false, null, 50, null), Times.Once);
    }

    [Fact]
    public async Task ExactSearch_PageSizeExceedsMax_RejectsRequest()
    {
        // Act & Assert - page_size > 100 should throw per spec
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _service.ExactSearch(new ExactSearchRequest
            {
                Query = "hello",
                Phrase = false,
                PageSize = 200
            }, CreateTestContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("DOCRETRIEVAL_PAGE_SIZE_INVALID", ex.Status.Detail);
    }

    [Fact]
    public async Task ExactSearch_PageSize100_Accepted()
    {
        // Arrange
        _searchServiceMock
            .Setup(s => s.ExactSearchAsync("hello", false, null, 100, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        // Act - page_size=100 is the max and should be accepted
        var response = await _service.ExactSearch(new ExactSearchRequest
        {
            Query = "hello",
            Phrase = false,
            PageSize = 100
        }, CreateTestContext());

        // Assert
        _searchServiceMock.Verify(s => s.ExactSearchAsync("hello", false, null, 100, null), Times.Once);
    }

    [Fact]
    public async Task ExactSearch_WithFilter_PropagatesFilterCorrectly()
    {
        // Arrange
        _searchServiceMock
            .Setup(s => s.ExactSearchAsync("hello", false, It.IsAny<SearchFilterModel?>(), 50, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        // Act
        var response = await _service.ExactSearch(new ExactSearchRequest
        {
            Query = "hello",
            Phrase = false,
            Filter = new SearchFilter
            {
                Subject = "英语",
                Grade = "G10",
                Year = "2023",
                DocumentTitle = "test.pdf"
            }
        }, CreateTestContext());

        // Assert
        _searchServiceMock.Verify(s => s.ExactSearchAsync(
            "hello", false,
            It.Is<SearchFilterModel>(f =>
                f.Subject == "英语" &&
                f.Grade == "G10" &&
                f.Year == "2023" &&
                f.DocumentTitle == "test.pdf"),
            50, null), Times.Once);
    }

    [Fact]
    public async Task ExactSearch_EmptyFiltersAreNullified()
    {
        // Arrange - when all filter fields are empty, MapFilter returns null (documentTitle/subject/grade/year all null)
        _searchServiceMock
            .Setup(s => s.ExactSearchAsync("hello", false,
                It.Is<SearchFilterModel?>(f =>
                    f != null &&
                    f.Subject == null &&
                    f.Grade == null &&
                    f.Year == null &&
                    f.DocumentTitle == null),
                50, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        // Act
        var response = await _service.ExactSearch(new ExactSearchRequest
        {
            Query = "hello",
            Phrase = false,
            Filter = new SearchFilter
            {
                Subject = "",
                Grade = "",
                Year = "",
                DocumentTitle = ""
            }
        }, CreateTestContext());

        // Assert - empty strings should become null in model
        _searchServiceMock.Verify(s => s.ExactSearchAsync(
            "hello", false,
            It.Is<SearchFilterModel?>(f =>
                f != null &&
                f.Subject == null &&
                f.Grade == null &&
                f.Year == null &&
                f.DocumentTitle == null),
            50, null), Times.Once);
    }

    [Fact]
    public async Task ExactSearch_NullFilter_IsNull()
    {
        // Arrange
        _searchServiceMock
            .Setup(s => s.ExactSearchAsync("hello", false, null, 50, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        // Act
        var response = await _service.ExactSearch(new ExactSearchRequest
        {
            Query = "hello",
            Phrase = false
            // Filter not set - null by default
        }, CreateTestContext());

        // Assert
        _searchServiceMock.Verify(s => s.ExactSearchAsync("hello", false, null, 50, null), Times.Once);
    }

    [Fact]
    public async Task ExactSearch_ReturnsNextPageToken()
    {
        // Arrange
        _searchServiceMock
            .Setup(s => s.ExactSearchAsync("hello", false, null, 50, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, "next-page-token"));

        // Act
        var response = await _service.ExactSearch(new ExactSearchRequest
        {
            Query = "hello",
            Phrase = false
        }, CreateTestContext());

        // Assert
        Assert.Equal("next-page-token", response.NextPageToken);
    }

    [Fact]
    public async Task ExactSearch_LastPage_HasEmptyNextToken()
    {
        // Arrange
        _searchServiceMock
            .Setup(s => s.ExactSearchAsync("hello", false, null, 50, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        // Act
        var response = await _service.ExactSearch(new ExactSearchRequest
        {
            Query = "hello",
            Phrase = false
        }, CreateTestContext());

        // Assert
        Assert.Equal(string.Empty, response.NextPageToken);
    }

    [Fact]
    public async Task ExactSearch_MultipleResults_ReturnsAll()
    {
        // Arrange
        var results = new List<SearchResultModel>
        {
            new() { DocumentName = "doc1.pdf", PageNumber = 1, AssociatedText = "Result 1", Score = 1.0, MatchType = "exact_phrase", SegmentId = "s1" },
            new() { DocumentName = "doc2.pdf", PageNumber = 2, AssociatedText = "Result 2", Score = 0.9, MatchType = "exact_word", SegmentId = "s2" },
            new() { DocumentName = "doc3.pdf", PageNumber = 3, AssociatedText = "Result 3", Score = 0.8, MatchType = "stem_match", SegmentId = "s3" }
        };

        _searchServiceMock
            .Setup(s => s.ExactSearchAsync("test", false, null, 50, null))
            .ReturnsAsync((results, 3, null));

        // Act
        var response = await _service.ExactSearch(new ExactSearchRequest
        {
            Query = "test",
            Phrase = false
        }, CreateTestContext());

        // Assert
        Assert.Equal(3, response.Results.Count);
    }

    #endregion

    #region HybridSearch Tests

    [Fact]
    public async Task HybridSearch_ValidRequest_ReturnsResults()
    {
        // Arrange
        var expectedResults = new List<SearchResultModel>
        {
            new()
            {
                DocumentName = "test.pdf",
                PageNumber = 1,
                AssociatedText = "Hello world",
                Score = 0.95,
                MatchType = "exact_word",
                SegmentId = "p1-b1-s1",
                StartOffset = 0,
                EndOffset = 5
            }
        };

        _searchServiceMock
            .Setup(s => s.HybridSearchAsync("hello", false, 50, 20, null, 50, null))
            .ReturnsAsync((expectedResults, 1, null));

        // Act
        var response = await _service.HybridSearch(new HybridSearchRequest
        {
            Query = "hello",
            Phrase = false
        }, CreateTestContext());

        // Assert
        Assert.Single(response.Results);
        Assert.Equal(1, response.TotalCount);
    }

    [Fact]
    public async Task HybridSearch_EmptyQuery_ThrowsRpcException()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _service.HybridSearch(new HybridSearchRequest { Query = "" }, CreateTestContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("DOCRETRIEVAL_QUERY_REQUIRED", ex.Status.Detail);
    }

    [Fact]
    public async Task HybridSearch_PageSizeExceedsMax_ThrowsRpcException()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            _service.HybridSearch(new HybridSearchRequest { Query = "hello", PageSize = 101 }, CreateTestContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("DOCRETRIEVAL_PAGE_SIZE_INVALID", ex.Status.Detail);
    }

    [Fact]
    public async Task HybridSearch_DefaultTopK_Is50And20()
    {
        // Arrange
        _searchServiceMock
            .Setup(s => s.HybridSearchAsync("hello", false, 50, 20, null, 50, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        // Act
        var response = await _service.HybridSearch(new HybridSearchRequest
        {
            Query = "hello",
            Phrase = false
        }, CreateTestContext());

        // Assert
        _searchServiceMock.Verify(s => s.HybridSearchAsync("hello", false, 50, 20, null, 50, null), Times.Once);
    }

    [Fact]
    public async Task HybridSearch_TopKClamped()
    {
        // Arrange
        _searchServiceMock
            .Setup(s => s.HybridSearchAsync("hello", false, 200, 100, null, 50, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        // Act
        var response = await _service.HybridSearch(new HybridSearchRequest
        {
            Query = "hello",
            Phrase = false,
            ExactTopK = 300,   // Should be clamped to 200
            SemanticTopK = 200 // Should be clamped to 100
        }, CreateTestContext());

        // Assert
        _searchServiceMock.Verify(s => s.HybridSearchAsync("hello", false, 200, 100, null, 50, null), Times.Once);
    }

    [Fact]
    public async Task HybridSearch_WithFilter_PropagatesCorrectly()
    {
        // Arrange
        _searchServiceMock
            .Setup(s => s.HybridSearchAsync("hello", false, 50, 20, It.IsAny<SearchFilterModel?>(), 50, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        var filter = new SearchFilter { Subject = "英语", Grade = "G10" };

        // Act
        var response = await _service.HybridSearch(new HybridSearchRequest
        {
            Query = "hello",
            Phrase = false,
            Filter = filter
        }, CreateTestContext());

        // Assert
        _searchServiceMock.Verify(s => s.HybridSearchAsync(
            "hello", false, 50, 20,
            It.Is<SearchFilterModel>(f => f.Subject == "英语" && f.Grade == "G10"),
            50, null), Times.Once);
    }

    #endregion

    #region Search Integration Tests (both endpoints)

    [Fact]
    public async Task BothSearchEndpoints_ValidateQueryLength()
    {
        var longQuery = new string('x', 201);
        var validQuery = new string('x', 200);

        // ExactSearch with 200 chars is valid
        _searchServiceMock
            .Setup(s => s.ExactSearchAsync(validQuery, false, null, 50, null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        var exactResponse = await _service.ExactSearch(new ExactSearchRequest { Query = validQuery }, CreateTestContext());
        Assert.NotNull(exactResponse);

        // HybridSearch with 200 chars is valid
        _searchServiceMock
            .Setup(s => s.HybridSearchAsync(validQuery, false, It.IsAny<int>(), It.IsAny<int>(), null, It.IsAny<int>(), null))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));

        var hybridResponse = await _service.HybridSearch(new HybridSearchRequest { Query = validQuery }, CreateTestContext());
        Assert.NotNull(hybridResponse);

        // ExactSearch with 201 chars should throw
        await Assert.ThrowsAsync<RpcException>(() =>
            _service.ExactSearch(new ExactSearchRequest { Query = longQuery }, CreateTestContext()));

        // HybridSearch with 201 chars should throw
        await Assert.ThrowsAsync<RpcException>(() =>
            _service.HybridSearch(new HybridSearchRequest { Query = longQuery }, CreateTestContext()));
    }

    #endregion

    #region Helpers

    private static ServerCallContext CreateTestContext()
    {
        return new TestServerCallContext();
    }

    /// <summary>
    /// Minimal ServerCallContext implementation for unit testing
    /// </summary>
    private class TestServerCallContext : ServerCallContext
    {
        public TestServerCallContext() { }

        protected override string MethodCore => "/ruoyu.study.docretrieval.v1.DocumentRetrievalService/Test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
        protected override Metadata RequestHeadersCore => new Metadata();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => new Metadata();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new AuthContext(string.Empty, new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }

    #endregion
}
