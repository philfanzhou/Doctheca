# DocumentUpload — 测试计划 (TESTS)

测试工具：`xUnit + Moq`。所有外部依赖（OSS、仓储、UnitOfWork、日志）通过 Moq 替换，不得在测试中访问真实外部资源。

## 单元测试 — Given-When-Then 格式

### UT-01 正常创建文档（验证 SPEC REQ-UPLOAD-12）

- **Given**：一个有效的 `DocumentModel`（Title="测试文档", Subject="英语", Grade="G3", Year="2025", FileHash="abc123..."），`_documentRepository.GetByTitleAsync` 返回 null，`_documentRepository.GetByFileHashAndStatusAsync` 返回 null。
- **When**：调用 `CreateDocumentAsync(document)`。
- **Then**：返回的 `DocumentModel` 的 `Id != Guid.Empty`，`Status == "pending"`，`CreatedAt` 接近当前时间（±5s）；`_documentRepository.AddAsync` 被调用 1 次；`_jobRepository.AddAsync` 被调用 1 次；`_unitOfWork.SaveChangesAsync` 被调用 1 次。

### UT-02 标题重复拒绝（验证 SPEC REQ-UPLOAD-10）

- **Given**：`_documentRepository.GetByTitleAsync` 返回一个已存在的 `DocumentModel`。
- **When**：调用 `CreateDocumentAsync(document)`。
- **Then**：抛出 `DocRetrievalValidationException`，消息包含"文档名已存在"；`_documentRepository.AddAsync` 未被调用；`_unitOfWork.SaveChangesAsync` 未被调用。

### UT-03 文件哈希重复(ready)拒绝（验证 SPEC REQ-UPLOAD-11）

- **Given**：`_documentRepository.GetByTitleAsync` 返回 null，`_documentRepository.GetByFileHashAndStatusAsync(hash, "ready")` 返回一个已存在的 `DocumentModel`。
- **When**：调用 `CreateDocumentAsync(document)`。
- **Then**：抛出 `DocRetrievalValidationException`，消息包含"文件已被导入"；`_documentRepository.AddAsync` 未被调用。

### UT-04 文件哈希重复(非ready)允许（验证 SPEC AC-D3）

- **Given**：`_documentRepository.GetByTitleAsync` 返回 null，`_documentRepository.GetByFileHashAndStatusAsync(hash, "ready")` 返回 null（即哈希相同但状态为 pending/failed 的文档存在）。
- **When**：调用 `CreateDocumentAsync(document)`。
- **Then**：正常返回 `DocumentModel`，`Status == "pending"`；`_documentRepository.AddAsync` 被调用。

### UT-05 元数据缺失校验（验证 SPEC REQ-UPLOAD-08）

- **Given**：`DocumentModel` 的 `Title` 为空字符串。
- **When**：调用 `CreateDocumentAsync(document)`。
- **Then**：抛出 `DocRetrievalValidationException`，消息包含"文档名不能为空"；`_documentRepository.AddAsync` 未被调用。

### UT-06 学科非法校验（验证 SPEC REQ-UPLOAD-09）

- **Given**：`DocumentModel` 的 `Subject = "数学"`。
- **When**：调用 `CreateDocumentAsync(document)`。
- **Then**：抛出 `DocRetrievalValidationException`，消息包含"学科仅支持：英语"。

### UT-07 年级非法校验（验证 SPEC REQ-UPLOAD-09）

- **Given**：`DocumentModel` 的 `Grade = "大学"`。
- **When**：调用 `CreateDocumentAsync(document)`。
- **Then**：抛出 `DocRetrievalValidationException`，消息包含"年级取值非法"。

### UT-08 标题超长校验（验证 SPEC REQ-UPLOAD-08）

- **Given**：`DocumentModel` 的 `Title` 长度 > 200 字符。
- **When**：调用 `CreateDocumentAsync(document)`。
- **Then**：抛出 `DocRetrievalValidationException`，消息包含"文档名超过200字符"。

### UT-09 FileHash 为空校验

- **Given**：`DocumentModel` 的 `FileHash` 为空字符串。
- **When**：调用 `CreateDocumentAsync(document)`。
- **Then**：抛出 `DocRetrievalValidationException`，消息包含"文件哈希不能为空"。

### UT-10 多项校验同时失败

- **Given**：`DocumentModel` 的 `Title` 为空、`Subject` 为"数学"、`Grade` 为"大学"。
- **When**：调用 `CreateDocumentAsync(document)`。
- **Then**：抛出 `DocRetrievalValidationException`，消息包含所有错误项（以分号分隔）。

### UT-11 加密 PDF 检测（验证 SPEC REQ-UPLOAD-05）

- **Given**：一个包含 `/Encrypt` 标记的 PDF 文件流，`contentType = "application/pdf"`。
- **When**：调用 `IsEncryptedPdf(stream, contentType)`。
- **Then**：返回 `true`；`stream.Position` 恢复为原始值。

### UT-12 非 PDF 不检测加密

- **Given**：一个 Word 文件流，`contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"`。
- **When**：调用 `IsEncryptedPdf(stream, contentType)`。
- **Then**：返回 `false`。

### UT-13 正常 PDF 不误判加密

- **Given**：一个不包含 `/Encrypt` 标记的正常 PDF 文件流，`contentType = "application/pdf"`。
- **When**：调用 `IsEncryptedPdf(stream, contentType)`。
- **Then**：返回 `false`；`stream.Position` 恢复为原始值。

### UT-14 DocRetrievalConstants 校验

- **Given**：`DocRetrievalConstants` 的定义。
- **When**：分别调用 `IsValidSubject("英语")`、`IsValidSubject("数学")`、`IsValidGrade("G3")`、`IsValidGrade("大学")`、`IsValidGrade("K")`。
- **Then**：`IsValidSubject("英语") == true`，`IsValidSubject("数学") == false`，`IsValidGrade("G3") == true`，`IsValidGrade("大学") == false`，`IsValidGrade("K") == true`。

### UT-15 端点错误码映射（验证 SPEC 错误码表）

- **Given**：`DocumentDomainService.CreateDocumentAsync` 抛出不同消息的 `DocRetrievalValidationException`。
- **When**：在 `UploadDocument` 中捕获异常并映射。
- **Then**：消息包含"文档名已存在"→ 409/`DOCRETRIEVAL_TITLE_ALREADY_EXISTS`；消息包含"文件已被导入"→ 409/`DOCRETRIEVAL_FILE_HASH_ALREADY_EXISTS`；消息包含"学科仅支持"→ 400/`DOCRETRIEVAL_SUBJECT_INVALID`；消息包含"年级取值非法"→ 400/`DOCRETRIEVAL_GRADE_INVALID`。

## 集成测试

### IT-01 端到端上传 → 查询 → 确认任务创建

1. 使用 mock OSS 和真实测试数据库上下文。
2. 上传合法 PDF 文件，提供完整元数据。
3. 断言返回 200，`data.documentId` 非空，`data.jobId` 非空，`data.status == "pending"`。
4. 通过 `GetDocumentAsync(id)` 查询文档，断言字段一致。
5. 通过 `GetIngestionJobAsync(id)` 查询任务，断言 `Status == "pending"`，`DocumentId` 一致。

### IT-02 上传重复标题 → 409

1. 上传文档 A（title="重复测试"），成功。
2. 上传文档 B（title="重复测试"，不同文件），返回 409，`errorCode == "DOCRETRIEVAL_TITLE_ALREADY_EXISTS"`。

### IT-03 上传重复哈希(ready) → 409

1. 上传文档 A，手动将其状态设为 `ready`。
2. 上传文档 B（相同文件，不同标题），返回 409，`errorCode == "DOCRETRIEVAL_FILE_HASH_ALREADY_EXISTS"`。

## 边界和异常测试

- EX-01 文件大小恰好 200MB → 应允许上传。
- EX-02 文件大小 200MB + 1 字节 → 应拒绝（400）。
- EX-03 `tags` 字段为空字符串 → 应正常处理（`Tags` 设为 null）。
- EX-04 `title` 恰好 200 字符 → 应允许；201 字符 → 应拒绝。
- EX-05 OSS 上传抛异常 → 端点应返回 500，数据库不应有记录。

## xUnit + Moq 骨架代码

```csharp
using System;
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
            FileHash = "a".PadLeft(64, 'a')
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
            FileHash = "a".PadLeft(64, 'a')
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
            FileHash = "a".PadLeft(64, 'a')
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
    }

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
            FileHash = "a".PadLeft(64, 'a')
        };

        // When
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.CreateDocumentAsync(document));

        // Then
        Assert.Contains("学科仅支持", ex.Message);
    }

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
            FileHash = "a".PadLeft(64, 'a')
        };

        // When
        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.CreateDocumentAsync(document));

        // Then
        Assert.Contains("年级取值非法", ex.Message);
    }

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
```

## 测试映射（到 SPEC 功能要求）

| 测试编号 | 验证 SPEC 项 |
| --- | --- |
| UT-01 | REQ-UPLOAD-12, REQ-UPLOAD-14 |
| UT-02 | REQ-UPLOAD-10 |
| UT-03 | REQ-UPLOAD-11 |
| UT-04 | AC-D3 |
| UT-05 | REQ-UPLOAD-08 |
| UT-06 | REQ-UPLOAD-09 |
| UT-07 | REQ-UPLOAD-09 |
| UT-08 | REQ-UPLOAD-08 |
| UT-09 | REQ-UPLOAD-08 |
| UT-10 | REQ-UPLOAD-08 (多项) |
| UT-11 | REQ-UPLOAD-05 |
| UT-12 | REQ-UPLOAD-05 (非PDF) |
| UT-13 | REQ-UPLOAD-05 (正常PDF) |
| UT-14 | REQ-UPLOAD-09 |
| UT-15 | 错误码映射表 |
| IT-01 | REQ-UPLOAD-12, REQ-UPLOAD-13, REQ-UPLOAD-14 |
| IT-02 | REQ-UPLOAD-10 |
| IT-03 | REQ-UPLOAD-11 |
| EX-01 | REQ-UPLOAD-03 (边界) |
| EX-02 | REQ-UPLOAD-03 (边界) |
| EX-03 | REQ-UPLOAD-01 (tags可选) |
| EX-04 | REQ-UPLOAD-08 (边界) |
| EX-05 | 可靠性 |
