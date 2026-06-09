using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Domain.Tests;

public class SearchDomainServiceTests
{
    private readonly Mock<IDocumentRepository> _mockDocRepo;
    private readonly Mock<IDocumentSegmentRepository> _mockSegRepo;
    private readonly Mock<IQuestionSegmentRepository> _mockQRepo;
    private readonly Mock<IDocumentPageRepository> _mockPageRepo;
    private readonly Mock<ISearchIndexService> _mockSearchService;
    private readonly Mock<ILogger<SearchDomainService>> _mockLogger;

    public SearchDomainServiceTests()
    {
        _mockDocRepo = new Mock<IDocumentRepository>();
        _mockSegRepo = new Mock<IDocumentSegmentRepository>();
        _mockQRepo = new Mock<IQuestionSegmentRepository>();
        _mockPageRepo = new Mock<IDocumentPageRepository>();
        _mockSearchService = new Mock<ISearchIndexService>();
        _mockLogger = new Mock<ILogger<SearchDomainService>>();
    }

    private SearchDomainService CreateService(bool withSearchService = true)
    {
        var spMock = new Mock<IServiceProvider>();
        spMock.Setup(p => p.GetService(typeof(ISearchIndexService)))
            .Returns(withSearchService ? _mockSearchService.Object : null);
        return new SearchDomainService(
            spMock.Object, _mockDocRepo.Object, _mockSegRepo.Object,
            _mockQRepo.Object, _mockPageRepo.Object, _mockLogger.Object);
    }

    private DocumentModel CreateDoc(string title = "doc1", string status = "ready") => new()
    {
        Id = Guid.NewGuid(), Title = title, Status = status
    };

    private DocumentSegmentModel CreateSegment(string text = "test") => new()
    {
        Id = Guid.NewGuid(), Text = text, SentenceId = "s1",
        PageId = Guid.NewGuid(), DocumentId = Guid.NewGuid()
    };

    private QuestionSegmentModel CreateQuestion(string stem = "test") => new()
    {
        Id = Guid.NewGuid(), Stem = stem, QuestionId = "q1",
        PageId = Guid.NewGuid(), DocumentId = Guid.NewGuid()
    };

    private DocumentPageModel CreatePage() => new()
    {
        Id = Guid.NewGuid(), DocumentId = Guid.NewGuid(), PageNumber = 1
    };

    private void SetupDbFallback(List<DocumentModel> docs, Dictionary<Guid, List<DocumentSegmentModel>>? segmentsMap = null,
        Dictionary<Guid, List<QuestionSegmentModel>>? questionsMap = null)
    {
        _mockDocRepo.Setup(r => r.GetListAsync(1, 500, null, null, null, null, null))
            .ReturnsAsync((docs, docs.Count));
        _mockPageRepo.Setup(p => p.GetByDocumentIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<DocumentPageModel> { CreatePage() });
        _mockSegRepo.Setup(s => s.GetByDocumentIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<DocumentSegmentModel>());
        _mockQRepo.Setup(q => q.GetByDocumentIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<QuestionSegmentModel>());

        if (segmentsMap != null)
        {
            foreach (var kv in segmentsMap)
                _mockSegRepo.Setup(s => s.GetByDocumentIdAsync(kv.Key)).ReturnsAsync(kv.Value);
        }
        if (questionsMap != null)
        {
            foreach (var kv in questionsMap)
                _mockQRepo.Setup(q => q.GetByDocumentIdAsync(kv.Key)).ReturnsAsync(kv.Value);
        }
    }

    // ============== ExactSearch ==============

    // UT-05 OpenSearch 正常返回
    [Fact]
    public async Task ExactSearchAsync_OpenSearchAvailable_ReturnsDirectly()
    {
        var svc = CreateService(true);
        var expectedResults = new List<SearchResultModel> { new() { DocumentName = "doc1" } };
        _mockSearchService.Setup(s => s.ExactSearchAsync("test", false, null, 10, null))
            .ReturnsAsync((expectedResults, 1, null));

        var (results, total, next) = await svc.ExactSearchAsync(
            "test", false, null, 10, null);

        Assert.Single(results);
        _mockSearchService.Verify(s => s.ExactSearchAsync("test", false, null, 10, null), Times.Once);
    }

    // UT-06 OpenSearch 异常回退
    [Fact]
    public async Task ExactSearchAsync_OpenSearchFallsBackToDatabase()
    {
        var svc = CreateService(true);
        _mockSearchService.Setup(s => s.ExactSearchAsync("test", false, null, 10, null))
            .ThrowsAsync(new InvalidOperationException("OpenSearch down"));
        var doc = CreateDoc("doc1");
        SetupDbFallback(new List<DocumentModel> { doc },
            segmentsMap: new Dictionary<Guid, List<DocumentSegmentModel>>
            {
                [doc.Id] = new() { CreateSegment("test content") }
            });

        var (results, _, _) = await svc.ExactSearchAsync("test", false, null, 10, null);

        Assert.NotEmpty(results);
    }

    // UT-07 数据库搜索 segments 和 questions 匹配
    [Fact]
    public async Task DatabaseSearchAsync_SegmentsAndQuestionsMatch_ReturnsBoth()
    {
        var svc = CreateService(false);
        var doc = CreateDoc("doc1");
        SetupDbFallback(new List<DocumentModel> { doc },
            segmentsMap: new Dictionary<Guid, List<DocumentSegmentModel>>
            {
                [doc.Id] = new() { CreateSegment("hello world") }
            },
            questionsMap: new Dictionary<Guid, List<QuestionSegmentModel>>
            {
                [doc.Id] = new() { CreateQuestion("hello question") }
            });

        var (results, _, _) = await svc.ExactSearchAsync("hello", false, null, 10, null);
        Assert.Equal(2, results.Count);
    }

    // UT-08 仅搜索 ready 文档
    [Fact]
    public async Task DatabaseSearchAsync_OnlyReadyDocuments_ReturnsOnlyReady()
    {
        var svc = CreateService(false);
        var readyDoc = CreateDoc("ready-doc", "ready");
        var processingDoc = CreateDoc("processing-doc", "processing");
        SetupDbFallback(new List<DocumentModel> { readyDoc, processingDoc },
            segmentsMap: new Dictionary<Guid, List<DocumentSegmentModel>>
            {
                [readyDoc.Id] = new() { CreateSegment("test") },
                [processingDoc.Id] = new() { CreateSegment("test") }
            });

        var (results, _, _) = await svc.ExactSearchAsync("test", false, null, 10, null);
        Assert.Single(results);
        Assert.Equal("ready-doc", results[0].DocumentName);
    }

    // UT-09 短语匹配 Score 和 MatchType
    [Fact]
    public async Task DatabaseSearchAsync_PhraseMatch_CorrectScoreAndType()
    {
        var svc = CreateService(false);
        var doc = CreateDoc();
        SetupDbFallback(new List<DocumentModel> { doc },
            segmentsMap: new Dictionary<Guid, List<DocumentSegmentModel>>
            {
                [doc.Id] = new() { CreateSegment("machine learning is fun") }
            });

        var (results, _, _) = await svc.ExactSearchAsync("machine learning", true, null, 10, null);
        Assert.Single(results);
        Assert.Equal(1.0, results[0].Score);
        Assert.Equal("exact_phrase", results[0].MatchType);
    }

    // UT-10 单词匹配 Score 和 MatchType
    [Fact]
    public async Task DatabaseSearchAsync_WordMatch_CorrectScoreAndType()
    {
        var svc = CreateService(false);
        var doc = CreateDoc();
        SetupDbFallback(new List<DocumentModel> { doc },
            segmentsMap: new Dictionary<Guid, List<DocumentSegmentModel>>
            {
                [doc.Id] = new() { CreateSegment("machine learning is fun") }
            });

        var (results, _, _) = await svc.ExactSearchAsync("machine", false, null, 10, null);
        Assert.Single(results);
        Assert.Equal(0.8, results[0].Score);
        Assert.Equal("exact_word", results[0].MatchType);
    }

    // UT-11 去重逻辑
    [Fact]
    public async Task DatabaseSearchAsync_DeduplicationByKey_KeepsOne()
    {
        var svc = CreateService(false);
        var doc = CreateDoc();
        SetupDbFallback(new List<DocumentModel> { doc },
            segmentsMap: new Dictionary<Guid, List<DocumentSegmentModel>>
            {
                [doc.Id] = new() { new DocumentSegmentModel { Text = "hello world", SentenceId = "p1s1" } }
            },
            questionsMap: new Dictionary<Guid, List<QuestionSegmentModel>>
            {
                [doc.Id] = new() { new QuestionSegmentModel { Stem = "hello world", QuestionId = "p1s1" } }
            });

        var (results, _, _) = await svc.ExactSearchAsync("hello", false, null, 10, null);
        Assert.Single(results); // 去重后只保留一条
    }

    // UT-12 游标分页
    [Fact]
    public async Task DatabaseSearchAsync_CursorPagination_WorksCorrectly()
    {
        var svc = CreateService(false);
        var docs = Enumerable.Range(1, 15).Select(i => new DocumentModel
        {
            Id = Guid.NewGuid(), Title = $"doc{i}", Status = "ready"
        }).ToList();

        _mockDocRepo.Setup(r => r.GetListAsync(1, 500, null, null, null, null, null))
            .ReturnsAsync((docs, 15));
        _mockPageRepo.Setup(p => p.GetByDocumentIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<DocumentPageModel> { new() { Id = Guid.NewGuid(), DocumentId = Guid.NewGuid(), PageNumber = 1 } });
        _mockSegRepo.Setup(s => s.GetByDocumentIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<DocumentSegmentModel> { CreateSegment("test") });
        _mockQRepo.Setup(q => q.GetByDocumentIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<QuestionSegmentModel>());

        var (page1, _, nextToken) = await svc.ExactSearchAsync("test", false, null, 10, null);
        Assert.Equal(10, page1.Count);
        Assert.NotNull(nextToken);

        var (page2, _, nextToken2) = await svc.ExactSearchAsync("test", false, null, 10, nextToken);
        Assert.Equal(5, page2.Count);
        Assert.Null(nextToken2);
    }

    // EX-05 page_token 非法 Base64
    [Fact]
    public async Task DatabaseSearchAsync_InvalidPageToken_StartsFromZero()
    {
        var svc = CreateService(false);
        var docs = Enumerable.Range(1, 5).Select(i => CreateDoc($"doc{i}")).ToList();
        SetupDbFallback(docs,
            segmentsMap: docs.ToDictionary(d => d.Id, _ => new List<DocumentSegmentModel> { CreateSegment("test") }));

        var (results, _, _) = await svc.ExactSearchAsync("test", false, null, 10, "not-base64!!!");
        Assert.Equal(5, results.Count);
    }

    // EX-06 _searchIndexService 为 null
    [Fact]
    public async Task ExactSearchAsync_SearchServiceNull_DirectlyDatabaseSearch()
    {
        var svc = CreateService(false);
        var doc = CreateDoc();
        SetupDbFallback(new List<DocumentModel> { doc },
            segmentsMap: new Dictionary<Guid, List<DocumentSegmentModel>> { [doc.Id] = new() { CreateSegment("test") } });

        var (results, _, _) = await svc.ExactSearchAsync("test", false, null, 10, null);
        Assert.Single(results);
    }

    // ============== HybridSearch ==============

    // UT-06 OpenSearch 正常返回
    [Fact]
    public async Task HybridSearchAsync_OpenSearchAvailable_ReturnsDirectly()
    {
        var svc = CreateService(true);
        var expectedResults = new List<SearchResultModel> { new() { DocumentName = "doc1" } };
        _mockSearchService.Setup(s => s.HybridSearchAsync("test", false, 50, 20, null, 10, null))
            .ReturnsAsync((expectedResults, 1, null));

        var (results, _, _) = await svc.HybridSearchAsync("test", false, 50, 20, null, 10, null);
        Assert.Single(results);
        _mockSearchService.Verify(s => s.HybridSearchAsync("test", false, 50, 20, null, 10, null), Times.Once);
    }

    // UT-07 OpenSearch 异常回退 + MatchType 降级
    [Fact]
    public async Task HybridSearchAsync_OpenSearchFallsBack_DowngradesMatchType()
    {
        var svc = CreateService(true);
        _mockSearchService.Setup(s => s.HybridSearchAsync("test", false, 50, 20, null, 10, null))
            .ThrowsAsync(new InvalidOperationException("OpenSearch down"));
        var doc = CreateDoc();
        SetupDbFallback(new List<DocumentModel> { doc },
            segmentsMap: new Dictionary<Guid, List<DocumentSegmentModel>> { [doc.Id] = new() { CreateSegment("test") } });

        var (results, _, _) = await svc.HybridSearchAsync("test", false, 50, 20, null, 10, null);
        Assert.Single(results);
        Assert.Equal("stem_match", results[0].MatchType);
    }

    // EX-07 _searchIndexService 为 null → 回退 + 降级
    [Fact]
    public async Task HybridSearchAsync_SearchServiceNull_FallbackDowngrade()
    {
        var svc = CreateService(false);
        var doc = CreateDoc();
        SetupDbFallback(new List<DocumentModel> { doc },
            segmentsMap: new Dictionary<Guid, List<DocumentSegmentModel>> { [doc.Id] = new() { CreateSegment("test") } });

        var (results, _, _) = await svc.HybridSearchAsync("test", false, 50, 20, null, 10, null);
        Assert.Single(results);
        Assert.Equal("stem_match", results[0].MatchType);
    }
}