# 文档元数据更新 — 测试方案与代码骨架（TESTS）

> 对应测试文件：`test/Ruoyu.Study.DocRetrieval.Tests/DocumentDomainServiceTests.cs`（元数据更新相关测试方法）
> 运行命令：`dotnet test --filter "FullyQualifiedName~DocumentMetadataTests"`

---

## 1. 测试场景总览（Given-When-Then）

### TC-01 成功更新学科（SPEC-FR-1, FR-4, FR-6, FR-7, FR-8）

**Given**
- Mock `IDocumentRepository.GetByTitleAsync` 返回 `Status="ready"`, `Subject="英语"` 的文档
- Mock `ISearchIndexService.UpdateDocumentMetadataAsync` 正常返回

**When**
- 调用 `UpdateMetadataAsync("TestDoc", subject: "英语", grade: null, year: null, tags: null)`

**Then**
- 返回的文档 `Subject == "英语"`
- `UpdatedAt` 不为 null
- `ISearchIndexService.UpdateDocumentMetadataAsync` 被调用 1 次
- 不抛异常

---

### TC-02 文档不存在（SPEC-FR-2）

**Given**
- Mock `IDocumentRepository.GetByTitleAsync` 返回 null

**When**
- 调用 `UpdateMetadataAsync("NotFound", subject: "英语", grade: null, year: null, tags: null)`

**Then:**
- 抛出 `DocRetrievalValidationException`，消息为 "文档不存在"

---

### TC-03 文档未就绪（SPEC-FR-1, FR-3）

**Given**
- Mock `IDocumentRepository.GetByTitleAsync` 返回 `Status="pending"` 的文档

**When**
- 调用 `UpdateMetadataAsync("PendingDoc", subject: "英语", grade: null, year: null, tags: null)`

**Then**
- 抛出 `DocRetrievalValidationException`，消息为 "文档未就绪，不允许修改元数据"

---

### TC-04 学科无效（SPEC-FR-4）

**Given**
- Mock `IDocumentRepository.GetByTitleAsync` 返回 `Status="ready"` 的文档

**When**
- 调用 `UpdateMetadataAsync("TestDoc", subject: "数学", grade: null, year: null, tags: null)`

**Then**
- 抛出 `DocRetrievalValidationException`，消息为 "学科仅支持：英语"

---

### TC-05 年级无效（SPEC-FR-5）

**Given**
- Mock `IDocumentRepository.GetByTitleAsync` 返回 `Status="ready"` 的文档

**When**
- 调用 `UpdateMetadataAsync("TestDoc", subject: null, grade: "G99", year: null, tags: null)`

**Then**
- 抛出 `DocRetrievalValidationException`，消息包含 "年级取值非法"

---

### TC-06 null 参数不修改字段（SPEC-FR-6）

**Given**
- Mock `IDocumentRepository.GetByTitleAsync` 返回 `Status="ready"`, `Subject="英语"`, `Grade="G5"`, `Year="2025"` 的文档

**When**
- 调用 `UpdateMetadataAsync("TestDoc", subject: null, grade: null, year: null, tags: null)`

**Then**
- 文档的 `Subject` 仍为 "英语"
- 文档的 `Grade` 仍为 "G5"
- 文档的 `Year` 仍为 "2025"
- `UpdatedAt` 被更新

---

### TC-07 搜索索引同步失败不中断主流程（SPEC-FR-8）

**Given**
- Mock `IDocumentRepository.GetByTitleAsync` 返回 `Status="ready"` 的文档
- Mock `ISearchIndexService.UpdateDocumentMetadataAsync` 抛出 `Exception("Search down")`

**When**
- 调用 `UpdateMetadataAsync("TestDoc", subject: "英语", grade: null, year: null, tags: null)`

**Then**
- 不抛出异常
- `IDocumentRepository.UpdateAsync` 被调用
- `IUnitOfWork.SaveChangesAsync` 被调用

---

### TC-08 更新年级成功（SPEC-FR-6, FR-7）

**Given**
- Mock `IDocumentRepository.GetByTitleAsync` 返回 `Status="ready"`, `Grade="G5"` 的文档

**When**
- 调用 `UpdateMetadataAsync("TestDoc", subject: null, grade: "G6", year: null, tags: null)`

**Then**
- 文档的 `Grade` 被更新为 "G6"
- `UpdatedAt` 被更新

---

### TC-09 更新 tags 成功（SPEC-FR-10）

**Given**
- Mock `IDocumentRepository.GetByTitleAsync` 返回 `Status="ready"` 的文档

**When**
- 调用 `UpdateMetadataAsync("TestDoc", subject: null, grade: null, year: null, tags: "[\"tag1\",\"tag2\"]")`

**Then**
- 文档的 `Tags` 被更新为 `["tag1","tag2"]`

---

### TC-10 搜索索引服务为 null 时不报错（SPEC-FR-8）

**Given**
- `DocumentDomainService` 构造时 `searchIndexService` 参数为 null

**When**
- 调用 `UpdateMetadataAsync("TestDoc", subject: "英语", grade: null, year: null, tags: null)`

**Then**
- 不抛出 NullReferenceException
- 文档元数据正常更新

---

## 2. 测试代码骨架（xUnit + Moq）

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

public class DocumentMetadataTests
{
    private readonly Mock<IDocumentRepository> _mockDocRepo;
    private readonly Mock<IDocumentPageRepository> _mockPageRepo;
    private readonly Mock<IDocumentSegmentRepository> _mockSegRepo;
    private readonly Mock<IQuestionSegmentRepository> _mockQRepo;
    private readonly Mock<IDocumentOccurrenceRepository> _mockOccRepo;
    private readonly Mock<IDocumentIngestionJobRepository> _mockJobRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<ISearchIndexService> _mockSearchService;
    private readonly Mock<ILogger<DocumentDomainService>> _mockLogger;
    private readonly DocumentDomainService _service;

    public DocumentMetadataTests()
    {
        _mockDocRepo = new Mock<IDocumentRepository>();
        _mockPageRepo = new Mock<IDocumentPageRepository>();
        _mockSegRepo = new Mock<IDocumentSegmentRepository>();
        _mockQRepo = new Mock<IQuestionSegmentRepository>();
        _mockOccRepo = new Mock<IDocumentOccurrenceRepository>();
        _mockJobRepo = new Mock<IDocumentIngestionJobRepository>();
        _mockUow = new Mock<IUnitOfWork>();
        _mockSearchService = new Mock<ISearchIndexService>();
        _mockLogger = new Mock<ILogger<DocumentDomainService>>();

        _service = new DocumentDomainService(
            _mockDocRepo.Object,
            _mockPageRepo.Object,
            _mockSegRepo.Object,
            _mockQRepo.Object,
            _mockOccRepo.Object,
            _mockJobRepo.Object,
            _mockUow.Object,
            _mockLogger.Object,
            _mockSearchService.Object);
    }

    private DocumentModel CreateReadyDocument(string title = "TestDoc")
    {
        return new DocumentModel
        {
            Id = Guid.NewGuid(),
            Title = title,
            Status = "ready",
            Subject = "英语",
            Grade = "G5",
            Year = "2025",
            Tags = null
        };
    }

    [Fact]
    public async Task UpdateMetadata_DocumentNotFound_ThrowsValidationException()
    {
        // TC-02
        _mockDocRepo.Setup(r => r.GetByTitleAsync("NotFound")).ReturnsAsync((DocumentModel?)null);

        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.UpdateMetadataAsync("NotFound", "英语", null, null, null));

        Assert.Equal("文档不存在", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadata_DocumentNotReady_ThrowsValidationException()
    {
        // TC-03
        var doc = CreateReadyDocument();
        doc.Status = "pending";
        _mockDocRepo.Setup(r => r.GetByTitleAsync("PendingDoc")).ReturnsAsync(doc);

        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.UpdateMetadataAsync("PendingDoc", "英语", null, null, null));

        Assert.Equal("文档未就绪，不允许修改元数据", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadata_InvalidSubject_ThrowsValidationException()
    {
        // TC-04
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(CreateReadyDocument());

        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.UpdateMetadataAsync("TestDoc", "数学", null, null, null));

        Assert.Equal("学科仅支持：英语", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadata_InvalidGrade_ThrowsValidationException()
    {
        // TC-05
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(CreateReadyDocument());

        var ex = await Assert.ThrowsAsync<DocRetrievalValidationException>(
            () => _service.UpdateMetadataAsync("TestDoc", null, "G99", null, null));

        Assert.Contains("年级取值非法", ex.Message);
    }

    [Fact]
    public async Task UpdateMetadata_NullFields_NotModified()
    {
        // TC-06
        var doc = CreateReadyDocument();
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(doc);

        var result = await _service.UpdateMetadataAsync("TestDoc", null, null, null, null);

        Assert.Equal("英语", result.Subject);
        Assert.Equal("G5", result.Grade);
        Assert.Equal("2025", result.Year);
        Assert.NotNull(result.UpdatedAt);
    }

    [Fact]
    public async Task UpdateMetadata_SearchIndexFails_DoesNotThrow()
    {
        // TC-07
        var doc = CreateReadyDocument();
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(doc);
        _mockSearchService
            .Setup(s => s.UpdateDocumentMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("Search down"));

        var result = await _service.UpdateMetadataAsync("TestDoc", "英语", null, null, null);

        _mockDocRepo.Verify(r => r.UpdateAsync(It.IsAny<DocumentModel>()), Times.Once);
        _mockUow.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateMetadata_GradeUpdated()
    {
        // TC-08
        var doc = CreateReadyDocument();
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(doc);

        var result = await _service.UpdateMetadataAsync("TestDoc", null, "G6", null, null);

        Assert.Equal("G6", result.Grade);
    }

    [Fact]
    public async Task UpdateMetadata_TagsUpdated()
    {
        // TC-09
        var doc = CreateReadyDocument();
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(doc);

        var result = await _service.UpdateMetadataAsync("TestDoc", null, null, null, "[\"tag1\",\"tag2\"]");

        Assert.Equal("[\"tag1\",\"tag2\"]", result.Tags);
    }

    [Fact]
    public async Task UpdateMetadata_NoSearchService_DoesNotThrow()
    {
        // TC-10
        var serviceWithoutSearch = new DocumentDomainService(
            _mockDocRepo.Object,
            _mockPageRepo.Object,
            _mockSegRepo.Object,
            _mockQRepo.Object,
            _mockOccRepo.Object,
            _mockJobRepo.Object,
            _mockUow.Object,
            _mockLogger.Object,
            searchIndexService: null);

        var doc = CreateReadyDocument();
        _mockDocRepo.Setup(r => r.GetByTitleAsync("TestDoc")).ReturnsAsync(doc);

        var result = await serviceWithoutSearch.UpdateMetadataAsync("TestDoc", "英语", null, null, null);

        Assert.Equal("英语", result.Subject);
    }
}
```

---

## 3. 测试数据准备要点

1. **文档状态**：使用 `CreateReadyDocument()` 辅助方法创建 `Status="ready"` 的文档，按需修改状态。
2. **异常验证**：使用 `Assert.ThrowsAsync<DocRetrievalValidationException>` 捕获异常并验证消息内容。
3. **Mock 验证**：使用 `Verify` 确认 Repository 和 SearchIndexService 的调用次数和参数。
4. **搜索索引隔离**：TC-07 中 Mock 搜索索引服务抛异常，验证主流程不中断。
5. **null 服务测试**：TC-10 中构造不含 `ISearchIndexService` 的服务实例，验证无 NullReferenceException。

## 4. 运行方式

```bash
cd services/ruoyu.docretrieval
dotnet test --filter "FullyQualifiedName~DocumentMetadataTests" -v normal
```

预期退出码：`0`，所有 `[Fact]` 通过。
