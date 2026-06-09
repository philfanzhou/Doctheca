using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Domain.Tests;

public class DocumentListTests
{
    private readonly Mock<IDocumentRepository> _mockDocRepo;
    private readonly Mock<IDocumentPageRepository> _mockPageRepo;
    private readonly Mock<IDocumentSegmentRepository> _mockSegRepo;
    private readonly Mock<IQuestionSegmentRepository> _mockQRepo;
    private readonly Mock<IDocumentOccurrenceRepository> _mockOccRepo;
    private readonly Mock<IDocumentIngestionJobRepository> _mockJobRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<ILogger<DocumentDomainService>> _mockLogger;
    private readonly DocumentDomainService _service;

    public DocumentListTests()
    {
        _mockDocRepo = new Mock<IDocumentRepository>();
        _mockPageRepo = new Mock<IDocumentPageRepository>();
        _mockSegRepo = new Mock<IDocumentSegmentRepository>();
        _mockQRepo = new Mock<IQuestionSegmentRepository>();
        _mockOccRepo = new Mock<IDocumentOccurrenceRepository>();
        _mockJobRepo = new Mock<IDocumentIngestionJobRepository>();
        _mockUow = new Mock<IUnitOfWork>();
        _mockLogger = new Mock<ILogger<DocumentDomainService>>();

        _service = new DocumentDomainService(
            _mockDocRepo.Object,
            _mockPageRepo.Object,
            _mockSegRepo.Object,
            _mockQRepo.Object,
            _mockOccRepo.Object,
            _mockJobRepo.Object,
            _mockUow.Object,
            _mockLogger.Object);
    }

    // TC-01: 无筛选条件返回全部文档
    [Fact]
    public async Task GetDocumentList_NoFilter_ReturnsAllDocuments()
    {
        var docs = new List<DocumentModel> { new(), new() };
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, null, null, null, null, null))
            .ReturnsAsync((docs, 2));

        var (items, total) = await _service.GetDocumentListAsync(1, 20);

        Assert.Equal(2, items.Count);
        Assert.Equal(2, total);
    }

    // TC-07: page≤0 修正为 1
    [Fact]
    public async Task GetDocumentList_PageZero_CorrectedToOne()
    {
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, null, null, null, null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(page: 0, size: 20);

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, null, null, null, null, null), Times.Once);
    }

    // TC-08: size≤0 修正为 20
    [Fact]
    public async Task GetDocumentList_SizeZero_CorrectedToTwenty()
    {
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, null, null, null, null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(page: 1, size: 0);

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, null, null, null, null, null), Times.Once);
    }

    // TC-09: size>100 修正为 100
    [Fact]
    public async Task GetDocumentList_SizeOver100_CorrectedTo100()
    {
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 100, null, null, null, null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(page: 1, size: 200);

        _mockDocRepo.Verify(r => r.GetListAsync(1, 100, null, null, null, null, null), Times.Once);
    }

    // TC-02: 按 status 筛选
    [Fact]
    public async Task GetDocumentList_WithStatusFilter_PassesToRepository()
    {
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, "ready", null, null, null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(1, 20, status: "ready");

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, "ready", null, null, null, null), Times.Once);
    }

    // TC-03: 按 subject 筛选
    [Fact]
    public async Task GetDocumentList_WithSubjectFilter_PassesToRepository()
    {
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, null, "英语", null, null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(1, 20, subject: "英语");

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, null, "英语", null, null, null), Times.Once);
    }

    // TC-04: 按 grade 筛选
    [Fact]
    public async Task GetDocumentList_WithGradeFilter_PassesToRepository()
    {
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, null, null, "G5", null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(1, 20, grade: "G5");

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, null, null, "G5", null, null), Times.Once);
    }

    // TC-05: 按 year 筛选
    [Fact]
    public async Task GetDocumentList_WithYearFilter_PassesToRepository()
    {
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, null, null, null, null, "2025"))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(1, 20, year: "2025");

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, null, null, null, null, "2025"), Times.Once);
    }

    // TC-06: 按 keyword 筛选
    [Fact]
    public async Task GetDocumentList_WithKeywordFilter_PassesToRepository()
    {
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, null, null, null, "数学", null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(1, 20, keyword: "数学");

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, null, null, null, "数学", null), Times.Once);
    }

    // TC-10: 多条件组合筛选
    [Fact]
    public async Task GetDocumentList_CombinedFilters_PassesAllToRepository()
    {
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, "ready", "英语", "G5", null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(1, 20, status: "ready", subject: "英语", grade: "G5");

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, "ready", "英语", "G5", null, null), Times.Once);
    }
}