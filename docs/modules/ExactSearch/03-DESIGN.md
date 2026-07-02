# ExactSearch — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   └── SearchConfig.cs                    # SearchResultModel, SearchFilterModel, OpenSearchOptions 等
│   │   ├── Services/
│   │   │   └── SearchDomainService.cs             # 领域服务 (ExactSearchAsync, DatabaseSearchAsync)
│   │   └── Repositories/
│   │       ├── ISearchIndexService.cs             # 搜索索引接口 (ExactSearchAsync)
│   │       └── IRepositories.cs                   # 各仓储接口定义 (IDocumentRepository, IDocumentSegmentRepository, IQuestionSegmentRepository, IDocumentPageRepository 等)
│   ├── Service/
│   │   ├── DocumentAdminEndpoints.cs              # HTTP 端点实现 (Search)
│   │   └── OpenSearchIndexService.cs              # OpenSearch 索引服务实现
│   ├── Database/
│   │   └── Repositories/                          # 仓储实现
│   └── Middleware/
│       └── CorrelationIdMiddleware.cs             # HTTP 相关 ID 中间件
├── test/
│   └── Ruoyu.Study.DocLibrary.Tests/
│       └── SearchDomainServiceTests.cs            # 单元测试
└── docs/modules/ExactSearch/                      # 本文档所在目录
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## 关键接口签名和数据结构定义

### HTTP 搜索端点

```
GET /admin/documents/search
```

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `query` | string | (必填) | 搜索关键词 |
| `phrase` | bool | `false` | 是否短语匹配 |
| `pageSize` | int | `20` | 每页结果数（1-100，超过 100 静默截断） |
| `pageToken` | string? | `null` | 游标分页 token |
| `subject` | string? | `null` | 按学科筛选 |
| `grade` | string? | `null` | 按年级筛选 |
| `year` | string? | `null` | 按年份筛选 |
| `documentTitle` | string? | `null` | 按文档标题筛选 |

成功响应 (200 OK)：
```json
{
  "success": true,
  "results": [...],
  "total_count": 42,
  "next_page_token": "..."
}
```

校验失败响应 (400 Bad Request)：
```json
{
  "success": false,
  "message": "DOCLIBRARY_QUERY_REQUIRED: 查询词不能为空",
  "errorCode": "DOCLIBRARY_QUERY_REQUIRED"
}
```

### 领域服务方法签名

```csharp
// src/Domain/Services/SearchDomainService.cs
public async Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
    string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken)

private async Task<(List<SearchResultModel>, int, string?)> DatabaseSearchAsync(
    string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken)
```

### 搜索索引接口

```csharp
// src/Domain/Repositories/ISearchIndexService.cs
Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(
    string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken);
```

### 领域模型

```csharp
// src/Domain/Models/SearchConfig.cs
public class SearchResultModel
{
    public string DocumentName { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string AssociatedText { get; set; } = string.Empty;
    public double Score { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public string SegmentId { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
}

public class SearchFilterModel
{
    public string? DocumentTitle { get; set; }
    public string? Subject { get; set; }
    public string? Grade { get; set; }
    public string? Year { get; set; }
}
```

### HTTP 端点实现

```csharp
// src/Service/DocumentAdminEndpoints.cs
private static async Task<IResult> Search(
    ISearchDomainService searchService,
    [FromQuery] string query,
    [FromQuery] bool phrase = false,
    [FromQuery] int pageSize = 20,
    [FromQuery] string? pageToken = null,
    [FromQuery] string? subject = null,
    [FromQuery] string? grade = null,
    [FromQuery] string? year = null,
    [FromQuery] string? documentTitle = null)
```

### 注入的外部依赖

```csharp
// SearchDomainService 构造函数
public SearchDomainService(
    ISearchIndexService? searchIndexService,
    IDocumentRepository documentRepository,
    IDocumentSegmentRepository segmentRepository,
    IQuestionSegmentRepository questionRepository,
    IDocumentPageRepository pageRepository,
    ILogger<SearchDomainService> logger)
```

- `ISearchIndexService?`：通过构造函数可选注入（nullable），提供 OpenSearch BM25 搜索能力；为 `null` 时直接走数据库回退路径。
- `IDocumentRepository`：文档仓储，提供 `GetListAsync` 用于获取文档列表。
- `IDocumentSegmentRepository`：文档片段仓储，提供 `GetByDocumentIdAsync` 和 `SearchByTextAsync(query, pageSize, skip, status?)` 用于数据库级 LIKE 搜索与分页。
- `IQuestionSegmentRepository`：题目片段仓储，提供 `GetByDocumentIdAsync` 和 `SearchByStemAsync(query, pageSize, skip, status?)` 用于数据库级 LIKE 搜索与分页。
- `IDocumentPageRepository`：文档页面仓储，提供 `GetByDocumentIdAsync` 用于页码映射。

## 数据流描述（步骤序列）

### HTTP 搜索端点流程

1. HTTP 层接收 `GET /admin/documents/search` 请求，`DocumentAdminEndpoints.Search` 处理。
2. 参数校验：`query` 为空 → 返回 HTTP 400 `{ success: false, errorCode: "DOCLIBRARY_QUERY_REQUIRED" }`；`query` 长度 > 200 → 返回 HTTP 400 `{ success: false, errorCode: "DOCLIBRARY_QUERY_TOO_LONG" }`。
3. 当 `subject`、`grade`、`year`、`documentTitle` 任一非空时，构造 `SearchFilterModel`；否则 `filter` 为 `null`。
4. `pageSize` 修正：`pageSize = Math.Min(Math.Max(pageSize, 1), 100)`；`pageSize <= 0` 时使用默认值 20。
5. 调用 `SearchDomainService.ExactSearchAsync(query, phrase, filter, pageSize, pageToken)`。

### ExactSearch 完整流程

1. 领域服务检查 `_searchIndexService` 是否可用：
   - **可用**：调用 `ISearchIndexService.ExactSearchAsync`，返回 OpenSearch BM25 结果。
   - **不可用或异常**：捕获异常，LogWarning 记录，回退 `DatabaseSearchAsync`。
2. 返回 `(Results, TotalCount, NextToken)`。
3. HTTP 层构建 JSON 响应：填充 `results`、`total_count`、`next_page_token`。

### 数据库回退搜索流程 (DatabaseSearchAsync)

1. 调用 `IDocumentSegmentRepository.SearchByTextAsync(query, pageSize, skip, status?)` 执行数据库级 `LIKE` 查询，返回匹配的文档片段。
2. 调用 `IQuestionSegmentRepository.SearchByStemAsync(query, pageSize, skip, status?)` 执行数据库级 `LIKE` 查询，返回匹配的题目片段。
3. 合并两批结果，对每条匹配记录获取页面列表，构建 `PageId → PageNumber` 映射以解析页码。
4. 结果去重：按 `$"{DocumentName}|{PageNumber}|{SegmentId}"` 分组，每组取首条。
5. 按 `Score` 降序排列。
6. 游标分页：解码 `page_token` 获取 `skip` 值，`Skip(skip).Take(pageSize)` 分页。
7. 计算下一页 token：`Base64(JSON({ skip = skip + pagedResults.Count }))`，无更多结果时返回 `null`。

### OpenSearch BM25 搜索流程 (OpenSearchIndexService.ExactSearchAsync)

1. 根据 `phrase` 参数构建查询：
   - `phrase = true`：使用 `match_phrase` 查询 `text.exact` 字段（english_phrase 分析器）。
   - `phrase = false`：使用 `match` 查询 `text` 字段（english_custom 分析器，含词干提取）。
2. 根据 `filter` 构建 `term` 过滤子句（subject/grade/year/document_title）。
3. 使用 `search_after` 游标分页，排序按 `_score desc, document_id asc, segment_type asc`。
4. 启用 `highlight` 高亮匹配文本。
5. 解析响应，构建 `SearchResultModel` 列表。
6. 返回结果及下一页 token。

## 错误处理策略

- **参数校验**：在 HTTP 端点层前置校验，无效参数直接返回 HTTP 400 Bad Request，响应体为 `{ success: false, message: "...", errorCode: "..." }`。
- **OpenSearch 回退**：`SearchDomainService` 捕获 OpenSearch 异常，LogWarning 后回退数据库搜索。
- **page_token 解码失败**：`DatabaseSearchAsync` 中 catch 解码异常，`skip` 默认为 0。
- **空结果**：返回空 `results` 列表，`total_count = 0`，`next_page_token = ""`。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `ISearchIndexService` | OpenSearch BM25 精确搜索 | `Ruoyu.Study.DocLibrary.Domain.Repositories` |
| `IDocumentRepository` | 文档列表查询（含筛选） | `Ruoyu.Study.DocLibrary.Domain.Repositories` |
| `IDocumentSegmentRepository` | 文档片段按文档 ID 查询；数据库级 LIKE 搜索与分页（`SearchByTextAsync`） | `Ruoyu.Study.DocLibrary.Domain.Repositories` |
| `IQuestionSegmentRepository` | 题目片段按文档 ID 查询；数据库级 LIKE 搜索与分页（`SearchByStemAsync`） | `Ruoyu.Study.DocLibrary.Domain.Repositories` |
| `IDocumentPageRepository` | 文档页面按文档 ID 查询 | `Ruoyu.Study.DocLibrary.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：`ISearchIndexService` 通过构造函数可选注入（nullable），可直接传 `null` 模拟不可用场景。
- **回退逻辑可验证**：Mock `ISearchIndexService.ExactSearchAsync` 抛异常，验证回退到 `DatabaseSearchAsync`。
- **分页逻辑可验证**：通过 mock 仓储返回预设数据，验证游标分页的编码/解码和 skip/take 行为。
- **去重逻辑可验证**：构造重复 `DocumentName+PageNumber+SegmentId` 的数据，验证去重保留首条。
- **数据库搜索可验证**：Mock `SearchByTextAsync` 和 `SearchByStemAsync` 返回预设匹配结果，验证数据库回退搜索流程。
