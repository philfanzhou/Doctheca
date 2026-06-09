using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Domain.Tests;

public class DocumentDeletionDomainTests
{
    private readonly Mock<IDocumentRepository> _documentRepository;
    private readonly Mock<IDocumentPageRepository> _pageRepository;
    private readonly Mock<IDocumentSegmentRepository> _segmentRepository;
    private readonly Mock<IQuestionSegmentRepository> _questionRepository;
    private readonly Mock<IDocumentOccurrenceRepository> _occurrenceRepository;
    private readonly Mock<IDocumentIngestionJobRepository> _jobRepository;
    private readonly Mock<IUnitOfWork> _unitOfWork;
    private readonly Mock<ISearchIndexService> _searchIndexService;
    private readonly Mock<IQdrantService> _qdrantService;
    private readonly Mock<ILogger<DocumentDomainService>> _logger;
    private readonly DocumentDomainService _sut;

    public DocumentDeletionDomainTests()
    {
        _documentRepository = new Mock<IDocumentRepository>();
        _pageRepository = new Mock<IDocumentPageRepository>();
        _segmentRepository = new Mock<IDocumentSegmentRepository>();
        _questionRepository = new Mock<IQuestionSegmentRepository>();
        _occurrenceRepository = new Mock<IDocumentOccurrenceRepository>();
        _jobRepository = new Mock<IDocumentIngestionJobRepository>();
        _unitOfWork = new Mock<IUnitOfWork>();
        _searchIndexService = new Mock<ISearchIndexService>();
        _qdrantService = new Mock<IQdrantService>();
        _logger = new Mock<ILogger<DocumentDomainService>>();

        _sut = new DocumentDomainService(
            _documentRepository.Object,
            _pageRepository.Object,
            _segmentRepository.Object,
            _questionRepository.Object,
            _occurrenceRepository.Object,
            _jobRepository.Object,
            _unitOfWork.Object,
            _logger.Object,
            _searchIndexService.Object,
            _qdrantService.Object);
    }

    private DocumentModel CreateTestDocument()
    {
        return new DocumentModel
        {
            Id = Guid.NewGuid(),
            Title = "英语试卷2024",
            Subject = "英语",
            Grade = "G5",
            Year = "2024",
            Status = "ready"
        };
    }

    // UT-01: 正常删除
    [Fact]
    public async Task DeleteDocumentAsync_NormalDeletion_ReturnsTrue()
    {
        var doc = CreateTestDocument();
        _documentRepository.Setup(r => r.GetByTitleAsync("英语试卷2024")).ReturnsAsync(doc);

        var result = await _sut.DeleteDocumentAsync("英语试卷2024");

        Assert.True(result);
        _occurrenceRepository.Verify(r => r.DeleteByDocumentIdAsync(doc.Id), Times.Once);
        _questionRepository.Verify(r => r.DeleteByDocumentIdAsync(doc.Id), Times.Once);
        _segmentRepository.Verify(r => r.DeleteByDocumentIdAsync(doc.Id), Times.Once);
        _pageRepository.Verify(r => r.DeleteByDocumentIdAsync(doc.Id), Times.Once);
        _documentRepository.Verify(r => r.DeleteAsync(doc.Id), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
        _searchIndexService.Verify(s => s.DeleteDocumentIndexAsync(doc.Id), Times.Once);
        _qdrantService.Verify(q => q.DeleteDocumentVectorsAsync(doc.Id), Times.Once);
    }

    // UT-02: 文档不存在（幂等）
    [Fact]
    public async Task DeleteDocumentAsync_DocumentNotExists_ReturnsTrue()
    {
        _documentRepository.Setup(r => r.GetByTitleAsync("不存在的文档")).ReturnsAsync((DocumentModel?)null);

        var result = await _sut.DeleteDocumentAsync("不存在的文档");

        Assert.True(result);
        _occurrenceRepository.Verify(r => r.DeleteByDocumentIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    // UT-03: 搜索索引清理失败
    [Fact]
    public async Task DeleteDocumentAsync_SearchIndexFails_StillReturnsTrue()
    {
        var doc = CreateTestDocument();
        _documentRepository.Setup(r => r.GetByTitleAsync("英语试卷2024")).ReturnsAsync(doc);
        _searchIndexService.Setup(s => s.DeleteDocumentIndexAsync(doc.Id))
            .ThrowsAsync(new Exception("OpenSearch error"));

        var result = await _sut.DeleteDocumentAsync("英语试卷2024");

        Assert.True(result);
    }

    // UT-04: 向量索引清理失败
    [Fact]
    public async Task DeleteDocumentAsync_QdrantIndexFails_StillReturnsTrue()
    {
        var doc = CreateTestDocument();
        _documentRepository.Setup(r => r.GetByTitleAsync("英语试卷2024")).ReturnsAsync(doc);
        _qdrantService.Setup(q => q.DeleteDocumentVectorsAsync(doc.Id))
            .ThrowsAsync(new Exception("Qdrant error"));

        var result = await _sut.DeleteDocumentAsync("英语试卷2024");

        Assert.True(result);
    }

    // UT-05: 搜索索引服务为 null
    [Fact]
    public async Task DeleteDocumentAsync_SearchServiceNull()
    {
        var doc = CreateTestDocument();
        _documentRepository.Setup(r => r.GetByTitleAsync("英语试卷2024")).ReturnsAsync(doc);
        var service = new DocumentDomainService(
            _documentRepository.Object, _pageRepository.Object, _segmentRepository.Object,
            _questionRepository.Object, _occurrenceRepository.Object, _jobRepository.Object,
            _unitOfWork.Object, _logger.Object, searchIndexService: null);

        var result = await service.DeleteDocumentAsync("英语试卷2024");

        Assert.True(result);
    }

    // UT-06: 向量索引服务为 null
    [Fact]
    public async Task DeleteDocumentAsync_QdrantServiceNull()
    {
        var doc = CreateTestDocument();
        _documentRepository.Setup(r => r.GetByTitleAsync("英语试卷2024")).ReturnsAsync(doc);
        var service = new DocumentDomainService(
            _documentRepository.Object, _pageRepository.Object, _segmentRepository.Object,
            _questionRepository.Object, _occurrenceRepository.Object, _jobRepository.Object,
            _unitOfWork.Object, _logger.Object, qdrantService: null);

        var result = await service.DeleteDocumentAsync("英语试卷2024");

        Assert.True(result);
    }

    // UT-07: 级联删除顺序验证
    [Fact]
    public async Task DeleteDocumentAsync_CascadingOrder()
    {
        var doc = CreateTestDocument();
        _documentRepository.Setup(r => r.GetByTitleAsync("英语试卷2024")).ReturnsAsync(doc);

        var sequence = 0;

        _occurrenceRepository.Setup(r => r.DeleteByDocumentIdAsync(doc.Id))
            .Callback(() => Assert.Equal(0, sequence++));
        _questionRepository.Setup(r => r.DeleteByDocumentIdAsync(doc.Id))
            .Callback(() => Assert.Equal(1, sequence++));
        _segmentRepository.Setup(r => r.DeleteByDocumentIdAsync(doc.Id))
            .Callback(() => Assert.Equal(2, sequence++));
        _pageRepository.Setup(r => r.DeleteByDocumentIdAsync(doc.Id))
            .Callback(() => Assert.Equal(3, sequence++));
        _documentRepository.Setup(r => r.DeleteAsync(doc.Id))
            .Callback(() => Assert.Equal(4, sequence++));

        await _sut.DeleteDocumentAsync("英语试卷2024");

        Assert.Equal(5, sequence);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    // EX-01: SaveChangesAsync 抛异常
    [Fact]
    public async Task DeleteDocumentAsync_SaveThrows_ShouldPropagate()
    {
        var doc = CreateTestDocument();
        _documentRepository.Setup(r => r.GetByTitleAsync("英语试卷2024")).ReturnsAsync(doc);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).ThrowsAsync(new InvalidOperationException("DB error"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.DeleteDocumentAsync("英语试卷2024"));
    }

    // EX-02: 搜索索引和向量索引同时失败
    [Fact]
    public async Task DeleteDocumentAsync_BothIndexServicesFail_StillReturnsTrue()
    {
        var doc = CreateTestDocument();
        _documentRepository.Setup(r => r.GetByTitleAsync("英语试卷2024")).ReturnsAsync(doc);
        _searchIndexService.Setup(s => s.DeleteDocumentIndexAsync(doc.Id))
            .ThrowsAsync(new Exception("Search down"));
        _qdrantService.Setup(q => q.DeleteDocumentVectorsAsync(doc.Id))
            .ThrowsAsync(new Exception("Qdrant down"));

        var result = await _sut.DeleteDocumentAsync("英语试卷2024");

        Assert.True(result);
    }

    // EX-03: 标题含特殊字符
    [Fact]
    public async Task DeleteDocumentAsync_SpecialCharsTitle()
    {
        var doc = new DocumentModel { Id = Guid.NewGuid(), Title = "英语/试卷\\2024" };
        _documentRepository.Setup(r => r.GetByTitleAsync("英语/试卷\\2024")).ReturnsAsync(doc);

        var result = await _sut.DeleteDocumentAsync("英语/试卷\\2024");

        Assert.True(result);
    }

    // EX-04: 空标题
    [Fact]
    public async Task DeleteDocumentAsync_EmptyTitle_ReturnsTrue()
    {
        _documentRepository.Setup(r => r.GetByTitleAsync("")).ReturnsAsync((DocumentModel?)null);

        var result = await _sut.DeleteDocumentAsync("");

        Assert.True(result);
    }
}