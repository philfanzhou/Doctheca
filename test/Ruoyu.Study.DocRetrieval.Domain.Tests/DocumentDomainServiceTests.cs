using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;

namespace Ruoyu.Study.DocRetrieval.Domain.Tests;

public class DocumentDomainServiceTests
{
    private readonly Mock<IDocumentRepository> _mockDocumentRepository;
    private readonly Mock<IDocumentPageRepository> _mockPageRepository;
    private readonly Mock<IDocumentSegmentRepository> _mockSegmentRepository;
    private readonly Mock<IQuestionSegmentRepository> _mockQuestionRepository;
    private readonly Mock<IDocumentOccurrenceRepository> _mockOccurrenceRepository;
    private readonly Mock<IDocumentIngestionJobRepository> _mockJobRepository;
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<ILogger<DocumentDomainService>> _mockLogger;
    private readonly DocumentDomainService _service;

    public DocumentDomainServiceTests()
    {
        _mockDocumentRepository = new Mock<IDocumentRepository>();
        _mockPageRepository = new Mock<IDocumentPageRepository>();
        _mockSegmentRepository = new Mock<IDocumentSegmentRepository>();
        _mockQuestionRepository = new Mock<IQuestionSegmentRepository>();
        _mockOccurrenceRepository = new Mock<IDocumentOccurrenceRepository>();
        _mockJobRepository = new Mock<IDocumentIngestionJobRepository>();
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockLogger = new Mock<ILogger<DocumentDomainService>>();

        _service = new DocumentDomainService(
            _mockDocumentRepository.Object,
            _mockPageRepository.Object,
            _mockSegmentRepository.Object,
            _mockQuestionRepository.Object,
            _mockOccurrenceRepository.Object,
            _mockJobRepository.Object,
            _mockUnitOfWork.Object,
            _mockLogger.Object);
    }

    // UT-01: 正常创建文档 (验证 SPEC REQ-UPLOAD-12, REQ-UPLOAD-14)
    [Fact]
    public async Task CreateDocumentAsync_ShouldCreateDocumentAndJob()
    {
        // Given
        var document = new DocumentModel
        {
            Title = "测试文档",
            Subject = "英语",
            Grade = "G3",
            Year = "2025",
            FileHash = new string('a', 64)
        };
        _mockDocumentRepository.Setup(r => r.GetByTitleAsync(document.Title))
            .ReturnsAsync((DocumentModel?)null);
        _mockDocumentRepository.Setup(r => r.GetByFileHashAndStatusAsync(document.FileHash, "ready"))
            .ReturnsAsync((DocumentModel?)null);

        // When
        var result = await _service.CreateDocumentAsync(document);

        // Then
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("pending", result.Status);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - result.CreatedAt).TotalSeconds) < 5);
        _mockDocumentRepository.Verify(r => r.AddAsync(It.IsAny<DocumentModel>()), Times.Once);
        _mockJobRepository.Verify(r => r.AddAsync(It.IsAny<DocumentIngestionJobModel>()), Times.Once);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    // UT-02: 标题重复拒绝 (验证 SPEC REQ-UPLOAD-10)
    [Fact]
    public async Task CreateDocumentAsync_WhenTitleExists_ShouldThrow()
    {
        // Given
        var document = new DocumentModel
        {
            Title = "已存在文档",
            Subject = "英语",
            Grade = "G3",
            Year = "2025",
            FileHash = new string('a', 64)
        };
        _mockDocumentRepository.Setup(r => r.GetByTitleAsync(document.Title))
            .ReturnsAsync(new DocumentModel { Title = document.Title });

        // When
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.CreateDocumentAsync(document));

        // Then
        Assert.Contains("文档名已存在", ex.Message);
        _mockDocumentRepository.Verify(r => r.AddAsync(It.IsAny<DocumentModel>()), Times.Never);
    }

    // UT-03: 文件哈希重复(ready)拒绝 (验证 SPEC REQ-UPLOAD-11)
    [Fact]
    public async Task CreateDocumentAsync_WhenFileHashExistsAndReady_ShouldThrow()
    {
        // Given
        var document = new DocumentModel
        {
            Title = "新文档",
            Subject = "英语",
            Grade = "G3",
            Year = "2025",
            FileHash = new string('a', 64)
        };
        _mockDocumentRepository.Setup(r => r.GetByTitleAsync(document.Title))
            .ReturnsAsync((DocumentModel?)null);
        _mockDocumentRepository.Setup(r => r.GetByFileHashAndStatusAsync(document.FileHash, "ready"))
            .ReturnsAsync(new DocumentModel { FileHash = document.FileHash, Status = "ready" });

        // When
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.CreateDocumentAsync(document));

        // Then
        Assert.Contains("文件已被导入", ex.Message);
        _mockDocumentRepository.Verify(r => r.AddAsync(It.IsAny<DocumentModel>()), Times.Never);
    }

    // UT-04: 文件哈希重复(非ready)允许 (验证 SPEC AC-D3)
    [Fact]
    public async Task CreateDocumentAsync_WhenFileHashExistsButNotReady_ShouldAllow()
    {
        // Given
        var document = new DocumentModel
        {
            Title = "新文档",
            Subject = "英语",
            Grade = "G3",
            Year = "2025",
            FileHash = new string('a', 64)
        };
        _mockDocumentRepository.Setup(r => r.GetByTitleAsync(document.Title))
            .ReturnsAsync((DocumentModel?)null);
        // 哈希相同但 status 不是 ready，GetByFileHashAndStatusAsync(hash, "ready") 返回 null
        _mockDocumentRepository.Setup(r => r.GetByFileHashAndStatusAsync(document.FileHash, "ready"))
            .ReturnsAsync((DocumentModel?)null);

        // When
        var result = await _service.CreateDocumentAsync(document);

        // Then
        Assert.NotNull(result);
        Assert.Equal("pending", result.Status);
        _mockDocumentRepository.Verify(r => r.AddAsync(It.IsAny<DocumentModel>()), Times.Once);
    }

    // UT-05: 元数据缺失校验 (验证 SPEC REQ-UPLOAD-08)
    [Fact]
    public async Task CreateDocumentAsync_WhenTitleEmpty_ShouldThrow()
    {
        // Given
        var document = new DocumentModel
        {
            Title = "",
            Subject = "英语",
            Grade = "G3",
            Year = "2025",
            FileHash = new string('a', 64)
        };

        // When
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.CreateDocumentAsync(document));

        // Then
        Assert.Contains("文档名不能为空", ex.Message);
        _mockDocumentRepository.Verify(r => r.AddAsync(It.IsAny<DocumentModel>()), Times.Never);
    }

    // UT-06: 学科非法校验 (验证 SPEC REQ-UPLOAD-09)
    [Fact]
    public async Task CreateDocumentAsync_WhenSubjectInvalid_ShouldThrow()
    {
        // Given
        var document = new DocumentModel
        {
            Title = "测试文档",
            Subject = "数学",
            Grade = "G3",
            Year = "2025",
            FileHash = new string('a', 64)
        };

        // When
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.CreateDocumentAsync(document));

        // Then
        Assert.Contains("学科仅支持", ex.Message);
    }

    // UT-07: 年级非法校验 (验证 SPEC REQ-UPLOAD-09)
    [Fact]
    public async Task CreateDocumentAsync_WhenGradeInvalid_ShouldThrow()
    {
        // Given
        var document = new DocumentModel
        {
            Title = "测试文档",
            Subject = "英语",
            Grade = "大学",
            Year = "2025",
            FileHash = new string('a', 64)
        };

        // When
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.CreateDocumentAsync(document));

        // Then
        Assert.Contains("年级取值非法", ex.Message);
    }

    // UT-08: 标题超长校验 (验证 SPEC REQ-UPLOAD-08)
    [Fact]
    public async Task CreateDocumentAsync_WhenTitleTooLong_ShouldThrow()
    {
        // Given
        var document = new DocumentModel
        {
            Title = new string('x', 201),
            Subject = "英语",
            Grade = "G3",
            Year = "2025",
            FileHash = new string('a', 64)
        };

        // When
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.CreateDocumentAsync(document));

        // Then
        Assert.Contains("文档名超过200字符", ex.Message);
    }

    // UT-09: FileHash 为空校验
    [Fact]
    public async Task CreateDocumentAsync_WhenFileHashEmpty_ShouldThrow()
    {
        // Given
        var document = new DocumentModel
        {
            Title = "测试文档",
            Subject = "英语",
            Grade = "G3",
            Year = "2025",
            FileHash = ""
        };

        // When
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.CreateDocumentAsync(document));

        // Then
        Assert.Contains("文件哈希不能为空", ex.Message);
    }

    // UT-10: 多项校验同时失败
    [Fact]
    public async Task CreateDocumentAsync_WhenMultipleValidationErrors_ShouldThrowWithAllMessages()
    {
        // Given
        var document = new DocumentModel
        {
            Title = "",
            Subject = "数学",
            Grade = "大学",
            Year = "",
            FileHash = ""
        };

        // When
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.CreateDocumentAsync(document));

        // Then
        Assert.Contains("文档名不能为空", ex.Message);
        Assert.Contains("学科仅支持", ex.Message);
        Assert.Contains("年级取值非法", ex.Message);
        Assert.Contains("年份不能为空", ex.Message);
        Assert.Contains("文件哈希不能为空", ex.Message);
    }

    // UT-14: DocRetrievalConstants 校验
    [Fact]
    public void IsValidSubject_ShouldReturnCorrectResults()
    {
        Assert.True(DocRetrievalConstants.IsValidSubject("英语"));
        Assert.False(DocRetrievalConstants.IsValidSubject("数学"));
    }

    [Fact]
    public void IsValidGrade_ShouldReturnCorrectResults()
    {
        Assert.True(DocRetrievalConstants.IsValidGrade("K"));
        Assert.True(DocRetrievalConstants.IsValidGrade("G3"));
        Assert.True(DocRetrievalConstants.IsValidGrade("G12"));
        Assert.False(DocRetrievalConstants.IsValidGrade("大学"));
        Assert.False(DocRetrievalConstants.IsValidGrade("g3")); // 大小写敏感
    }
}