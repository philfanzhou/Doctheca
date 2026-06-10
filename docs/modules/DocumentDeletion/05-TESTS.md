# DocumentDeletion — 测试计划 (TESTS)

> 测试工具：**xUnit + Moq**
> 目标：对 `DeleteDocumentAsync` 和 `DeleteDocument` 端点达成 100% 分支覆盖。

---

## 1. 单元测试

### 1.1 针对 `DocumentDomainService.DeleteDocumentAsync`（使用 Moq 替换依赖）

| # | 场景 | Given | When | Then | 验证 SPEC |
|---|------|-------|------|------|-----------|
| UT-01 | 正常删除（文档存在） | mock `GetByTitleAsync` 返回文档；mock 各仓储正常 | `DeleteDocumentAsync("英语试卷2024")` | 各 `DeleteByDocumentIdAsync` 按顺序被调用；`DeleteAsync` 被调用；`SaveChangesAsync` 被调用；搜索索引和向量索引清理被调用；返回 `true` | REQ-DEL-01, REQ-DEL-02 |
| UT-02 | 文档不存在（幂等） | mock `GetByTitleAsync` 返回 null | `DeleteDocumentAsync("不存在的文档")` | 无级联删除操作被调用；返回 `true` | REQ-DEL-03 |
| UT-03 | 搜索索引清理失败 | mock `DeleteDocumentIndexAsync` 抛出 `Exception` | `DeleteDocumentAsync("英语试卷2024")` | 异常被捕获；`LogError` 被调用；返回 `true` | REQ-DEL-04 |
| UT-04 | 向量索引清理失败 | mock `DeleteDocumentVectorsAsync` 抛出 `Exception` | `DeleteDocumentAsync("英语试卷2024")` | 异常被捕获；`LogError` 被调用；返回 `true` | REQ-DEL-05 |
| UT-05 | 搜索索引服务为 null | `ISearchIndexService` 传 null | `DeleteDocumentAsync("英语试卷2024")` | 不调用搜索索引清理；返回 `true` | REQ-DEL-04 |
| UT-06 | 向量索引服务为 null | `IQdrantService` 传 null | `DeleteDocumentAsync("英语试卷2024")` | 不调用向量索引清理；返回 `true` | REQ-DEL-05 |
| UT-07 | 级联删除顺序验证 | mock 各仓储，使用 `CallSequence` 验证调用顺序 | `DeleteDocumentAsync("英语试卷2024")` | 调用顺序为 occurrences → questions → segments → pages → document → SaveChanges | REQ-DEL-02 |

### 1.2 针对 `DocumentAdminEndpoints.DeleteDocument`（使用 Moq 替换依赖）

| # | 场景 | Given | When | Then | 验证 SPEC |
|---|------|-------|------|------|-----------|
| UT-08 | 正常删除（含 OSS 文件） | mock `GetDocumentByTitleAsync` 返回文档（FilePath 非空）；mock `DeleteDocumentAsync` 返回 true；mock `DeleteAsync` 正常 | `DeleteDocument("英语试卷2024", ...)` | `DeleteDocumentAsync` 被调用；`ossService.DeleteAsync` 被调用；返回 `{ success: true, data: { title, deleted: true } }` | REQ-DEL-06, REQ-DEL-07 |
| UT-09 | 文档不存在 | mock `GetDocumentByTitleAsync` 返回 null | `DeleteDocument("不存在的文档", ...)` | `DeleteDocumentAsync` 被调用；不调用 `ossService.DeleteAsync`；返回 `{ success: true, data: { title, deleted: false } }` | REQ-DEL-03, REQ-DEL-07 |
| UT-10 | 文档存在但 FilePath 为空 | mock `GetDocumentByTitleAsync` 返回文档（FilePath 为空） | `DeleteDocument("英语试卷2024", ...)` | 不调用 `ossService.DeleteAsync`；返回 `{ success: true, data: { title, deleted: true } }` | REQ-DEL-06 |
| UT-11 | OSS 删除失败 | mock `ossService.DeleteAsync` 抛出 `Exception` | `DeleteDocument("英语试卷2024", ...)` | 异常被捕获；`LogWarning` 被调用；返回 `{ success: true, data: { title, deleted: true } }` | REQ-DEL-06 |

---

## 2. 集成测试

| # | 场景 | Setup | Action | Assertion | 验证 SPEC |
|---|------|-------|--------|-----------|-----------|
| IT-01 | 删除后关联数据被清理 | 构造 DocRetrievalDbContext，预置文档及关联数据 | `DeleteDocumentAsync` | `document_occurrences`、`question_segments`、`document_segments`、`document_pages` 中该 documentId 的记录数为 0 | REQ-DEL-01, REQ-DEL-02 |
| IT-02 | 删除后文档记录消失 | 预置文档记录 | `DeleteDocumentAsync` | `documents` 表中该记录不存在 | REQ-DEL-01 |
| IT-03 | 端到端删除含 OSS | 预置文档和 OSS 文件 | `DELETE /admin/documents/{title}` | 数据库记录消失，OSS 文件被删除 | REQ-DEL-06 |

---

## 3. 边界与异常测试

| # | 场景 | Given | When | Then | 验证 SPEC |
|---|------|-------|------|------|-----------|
| EX-01 | SaveChangesAsync 抛异常 | mock `SaveChangesAsync` 抛 `DbUpdateException` | `DeleteDocumentAsync` | 异常冒泡给调用方 | N/A |
| EX-02 | 搜索索引和向量索引同时失败 | mock 两个索引服务均抛异常 | `DeleteDocumentAsync` | 两个异常均被捕获；返回 `true` | REQ-DEL-04, REQ-DEL-05 |
| EX-03 | 标题含特殊字符 | `title = "英语/试卷\\2024"` | `DeleteDocumentAsync` | 标题原样传递给 `GetByTitleAsync` | N/A |
| EX-04 | 空标题 | `title = ""` | `DeleteDocumentAsync` | `GetByTitleAsync` 返回 null；返回 `true` | REQ-DEL-03 |

---

## 4. 每个测试与 SPEC 的引用汇总

| 测试编号 | 引用的 SPEC 条目 |
|----------|-------------------|
| UT-01 ~ UT-07 | REQ-DEL-01 ~ 05 |
| UT-08 ~ UT-11 | REQ-DEL-03, REQ-DEL-06, REQ-DEL-07 |
| IT-01 ~ IT-03 | REQ-DEL-01, REQ-DEL-02, REQ-DEL-06 |
| EX-01 ~ EX-04 | REQ-DEL-03, REQ-DEL-04, REQ-DEL-05 |

---

## 5. xUnit + Moq 推荐骨架

```csharp
// [当前无测试覆盖] tests/DocumentDeletion/DocumentDeletionTests.cs
public class DocumentDeletionTests
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

    public DocumentDeletionTests()
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

    [Fact]
    public async Task DeleteDocumentAsync_DocumentNotExists_ReturnsTrue()
    {
        // Given
        _documentRepository.Setup(r => r.GetByTitleAsync("不存在的文档"))
                           .ReturnsAsync((DocumentModel?)null);

        // When
        var result = await _sut.DeleteDocumentAsync("不存在的文档");

        // Then
        Assert.True(result);
        _occurrenceRepository.Verify(
            r => r.DeleteByDocumentIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task DeleteDocumentAsync_SearchIndexFails_StillReturnsTrue()
    {
        // Given
        var document = CreateTestDocument();
        _documentRepository.Setup(r => r.GetByTitleAsync("英语试卷2024"))
                           .ReturnsAsync(document);
        _searchIndexService.Setup(s => s.DeleteDocumentIndexAsync(document.Id))
                           .ThrowsAsync(new Exception("OpenSearch error"));

        // When
        var result = await _sut.DeleteDocumentAsync("英语试卷2024");

        // Then
        Assert.True(result);
    }
}
```

---

## 6. 运行命令

```bash
cd backend/ruoyu.docretrieval

# 单元测试
dotnet test --filter "FullyQualifiedName~DocumentDeletion"

# 集成测试
dotnet test --filter "FullyQualifiedName~DocumentDeletionIntegrationTests"
```
