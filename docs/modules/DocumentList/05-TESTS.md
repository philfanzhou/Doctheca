# 文档列表查询与筛选 — 测试方案与代码骨架（TESTS）

> 对应测试文件：`test/Ruoyu.Study.DocLibrary.Tests/DocumentDomainServiceTests.cs`（列表相关测试方法）
> 运行命令：`dotnet test --filter "FullyQualifiedName~DocumentListTests"`

---

## 1. 测试场景总览（Given-When-Then）

### TC-01 无筛选条件返回全部文档（SPEC-FR-1, FR-3, FR-4）

**Given**
- Mock `IDocumentRepository.GetListAsync` 返回 `(Items: [doc1, doc2], TotalCount: 2)`

**When**
- 调用 `documentService.GetDocumentListAsync(1, 20)`

**Then**
- 返回 `Items.Count == 2`
- 返回 `TotalCount == 2`

---

### TC-02 按 status 筛选（SPEC-FR-1）

**Given**
- Mock `IDocumentRepository.GetListAsync` 当 status="ready" 时返回匹配结果

**When**
- 调用 `documentService.GetDocumentListAsync(1, 20, status: "ready")`

**Then**
- Repository 的 `GetListAsync` 被调用时 status 参数为 "ready"

---

### TC-03 按 subject 筛选（SPEC-FR-1）

**Given**
- Mock `IDocumentRepository.GetListAsync` 当 subject="英语" 时返回匹配结果

**When**
- 调用 `documentService.GetDocumentListAsync(1, 20, subject: "英语")`

**Then**
- Repository 的 `GetListAsync` 被调用时 subject 参数为 "英语"

---

### TC-04 按 grade 筛选（SPEC-FR-1）

**Given**
- Mock `IDocumentRepository.GetListAsync` 当 grade="G5" 时返回匹配结果

**When**
- 调用 `documentService.GetDocumentListAsync(1, 20, grade: "G5")`

**Then**
- Repository 的 `GetListAsync` 被调用时 grade 参数为 "G5"

---

### TC-05 按 year 筛选（SPEC-FR-1）

**Given**
- Mock `IDocumentRepository.GetListAsync` 当 year="2025" 时返回匹配结果

**When**
- 调用 `documentService.GetDocumentListAsync(1, 20, year: "2025")`

**Then**
- Repository 的 `GetListAsync` 被调用时 year 参数为 "2025"

---

### TC-06 按 keyword 筛选（SPEC-FR-1）

**Given**
- Mock `IDocumentRepository.GetListAsync` 当 keyword="数学" 时返回匹配结果

**When**
- 调用 `documentService.GetDocumentListAsync(1, 20, keyword: "数学")`

**Then**
- Repository 的 `GetListAsync` 被调用时 keyword 参数为 "数学"

---

### TC-07 page≤0 修正为 1（SPEC-FR-2）

**Given**
- Mock `IDocumentRepository.GetListAsync` 返回任意结果

**When**
- 调用 `documentService.GetDocumentListAsync(page: -1, size: 20)`

**Then**
- Repository 的 `GetListAsync` 被调用时 page 参数为 1

---

### TC-08 size≤0 修正为 20（SPEC-FR-2）

**Given**
- Mock `IDocumentRepository.GetListAsync` 返回任意结果

**When**
- 调用 `documentService.GetDocumentListAsync(page: 1, size: -5)`

**Then**
- Repository 的 `GetListAsync` 被调用时 size 参数为 20

---

### TC-09 size>100 修正为 100（SPEC-FR-2）

**Given**
- Mock `IDocumentRepository.GetListAsync` 返回任意结果

**When**
- 调用 `documentService.GetDocumentListAsync(page: 1, size: 200)`

**Then**
- Repository 的 `GetListAsync` 被调用时 size 参数为 100

---

### TC-10 多条件组合筛选（SPEC-FR-1）

**Given**
- Mock `IDocumentRepository.GetListAsync` 返回匹配结果

**When**
- 调用 `documentService.GetDocumentListAsync(1, 20, status: "ready", subject: "英语", grade: "G5")`

**Then**
- Repository 的 `GetListAsync` 被调用时三个筛选条件均被传入

---

## 2. 测试代码骨架（xUnit + Moq）

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

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

    [Fact]
    public async Task GetDocumentList_NoFilter_ReturnsAllDocuments()
    {
        // TC-01
        var docs = new List<DocumentModel> { new(), new() };
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, null, null, null, null, null))
            .ReturnsAsync((docs, 2));

        var (items, total) = await _service.GetDocumentListAsync(1, 20);

        Assert.Equal(2, items.Count);
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task GetDocumentList_PageZero_CorrectedToOne()
    {
        // TC-07
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, null, null, null, null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(page: 0, size: 20);

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, null, null, null, null, null), Times.Once);
    }

    [Fact]
    public async Task GetDocumentList_SizeZero_CorrectedToTwenty()
    {
        // TC-08
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, null, null, null, null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(page: 1, size: 0);

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, null, null, null, null, null), Times.Once);
    }

    [Fact]
    public async Task GetDocumentList_SizeOver100_CorrectedTo100()
    {
        // TC-09
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 100, null, null, null, null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(page: 1, size: 200);

        _mockDocRepo.Verify(r => r.GetListAsync(1, 100, null, null, null, null, null), Times.Once);
    }

    [Fact]
    public async Task GetDocumentList_WithStatusFilter_PassesToRepository()
    {
        // TC-02
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, "ready", null, null, null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(1, 20, status: "ready");

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, "ready", null, null, null, null), Times.Once);
    }

    [Fact]
    public async Task GetDocumentList_CombinedFilters_PassesAllToRepository()
    {
        // TC-10
        _mockDocRepo
            .Setup(r => r.GetListAsync(1, 20, "ready", "英语", "G5", null, null))
            .ReturnsAsync((new List<DocumentModel>(), 0));

        await _service.GetDocumentListAsync(1, 20, status: "ready", subject: "英语", grade: "G5");

        _mockDocRepo.Verify(r => r.GetListAsync(1, 20, "ready", "英语", "G5", null, null), Times.Once);
    }
}
```

---

## 3. 测试数据准备要点

1. **Mock 优先**：列表查询测试主要验证参数修正和透传逻辑，使用 Mock `IDocumentRepository` 即可，无需真实数据库。
2. **参数匹配**：使用 Moq 的 `It.IsAny<T>()` 或精确值匹配验证参数透传。
3. **返回值验证**：验证返回元组的 `Items` 和 `TotalCount` 与 Mock 设置一致。

## 4. 运行方式

```bash
cd src/services/ruoyu.doclibrary
dotnet test --filter "FullyQualifiedName~DocumentListTests" -v normal
```

预期退出码：`0`，所有 `[Fact]` 通过。
