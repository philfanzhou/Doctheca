# ExactSearch — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.docretrieval/
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
│   │   ├── DocumentRetrievalServiceImpl.cs        # gRPC 实现 (ExactSearch)
│   │   └── OpenSearchIndexService.cs              # OpenSearch 索引服务实现
│   ├── Database/
│   │   └── Repositories/                          # 仓储实现
│   └── Contract/Protos/
│       ├── docretrieval.proto                     # gRPC 服务定义
│       └── docretrieval.common.proto              # 公共消息定义
├── test/
│   └── Ruoyu.Study.DocRetrieval.Tests/
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

### gRPC 接口

```protobuf
// src/Contract/Protos/docretrieval.proto
rpc ExactSearch(ExactSearchRequest) returns (SearchResponse);

// src/Contract/Protos/docretrieval.common.proto
message ExactSearchRequest {
  string query = 1;
  bool phrase = 2;
  SearchFilter filter = 3;
  int32 page_size = 4;
  string page_token = 5;
}

message SearchFilter {
  string document_title = 1;
  string subject = 2;
  string grade = 3;
  string year = 4;
}

message SearchResponse {
  repeated SearchResult results = 1;
  string next_page_token = 2;
  int32 total_count = 3;
}

message SearchResult {
  string document_name = 1;
  int32 page_number = 2;
  string associated_text = 3;
  double score = 4;
  string match_type = 5;
  string segment_id = 6;
  int32 start_offset = 7;
  int32 end_offset = 8;
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

### gRPC 服务实现

```csharp
// src/Service/DocumentRetrievalServiceImpl.cs
public DocumentRetrievalServiceImpl(
    ISearchDomainService searchService,
    ILogger<DocumentRetrievalServiceImpl> logger)
```

### 注入的外部依赖

```csharp
// SearchDomainService 构造函数
public SearchDomainService(
    IServiceProvider serviceProvider,
    IDocumentRepository documentRepository,
    IDocumentSegmentRepository segmentRepository,
    IQuestionSegmentRepository questionRepository,
    IDocumentPageRepository pageRepository,
    ILogger<SearchDomainService> logger)
```

- `ISearchIndexService?`：通过 `IServiceProvider.GetService` 可选获取，提供 OpenSearch BM25 搜索能力。
- `IDocumentRepository`：文档仓储，提供 `GetListAsync` 用于获取文档列表。
- `IDocumentSegmentRepository`：文档片段仓储，提供 `GetByDocumentIdAsync`。
- `IQuestionSegmentRepository`：题目片段仓储，提供 `GetByDocumentIdAsync`。
- `IDocumentPageRepository`：文档页面仓储，提供 `GetByDocumentIdAsync` 用于页码映射。

## 数据流描述（步骤序列）

### ExactSearch 完整流程

1. gRPC 层接收 `ExactSearchRequest`，调用 `ValidateSearchRequest` 校验参数。
2. 参数校验：`query` 为空 → `INVALID_ARGUMENT (DOCRETRIEVAL_QUERY_REQUIRED)`；`query` 长度 > 200 → `INVALID_ARGUMENT (DOCRETRIEVAL_QUERY_TOO_LONG)`；`page_size` > 100 → `INVALID_ARGUMENT (DOCRETRIEVAL_PAGE_SIZE_INVALID)`。
3. `MapFilter` 将 `SearchFilter` 中空字符串字段转为 `null`。
4. `pageSize` 修正：`pageSize = request.PageSize > 0 ? Math.Min(request.PageSize, 100) : 50`。
5. 调用 `SearchDomainService.ExactSearchAsync(query, phrase, filter, pageSize, pageToken)`。
6. 领域服务检查 `_searchIndexService` 是否可用：
   - **可用**：调用 `ISearchIndexService.ExactSearchAsync`，返回 OpenSearch BM25 结果。
   - **不可用或异常**：捕获异常，LogWarning 记录，回退 `DatabaseSearchAsync`。
7. 返回 `(Results, TotalCount, NextToken)`。
8. gRPC 层构建 `SearchResponse`：填充 `results`、`total_count`、`next_page_token`。

### 数据库回退搜索流程 (DatabaseSearchAsync)

1. 调用 `GetFilteredDocumentsAsync(filter)` 获取符合筛选条件的文档列表。
2. 过滤 `status == "ready"` 的文档。
3. 对每个文档：
   a. 获取页面列表，构建 `PageId → PageNumber` 映射。
   b. 遍历 segments，对 `seg.Text` 执行 `IndexOf(query, StringComparison.OrdinalIgnoreCase)` 匹配。
   c. 遍历 questions，对 `q.Stem` 执行 `IndexOf(query, StringComparison.OrdinalIgnoreCase)` 匹配。
   d. 匹配成功时构建 `SearchResultModel`，设置 `Score` 和 `MatchType`。
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

- **参数校验**：在 gRPC 层前置校验，无效参数直接抛出 `RpcException(StatusCode.InvalidArgument)`。
- **OpenSearch 回退**：`SearchDomainService` 捕获 OpenSearch 异常，LogWarning 后回退数据库搜索。
- **page_token 解码失败**：`DatabaseSearchAsync` 中 catch 解码异常，`skip` 默认为 0。
- **空结果**：返回空 `results` 列表，`total_count = 0`，`next_page_token = ""`。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `ISearchIndexService` | OpenSearch BM25 精确搜索 | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IDocumentRepository` | 文档列表查询（含筛选） | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IDocumentSegmentRepository` | 文档片段按文档 ID 查询 | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IQuestionSegmentRepository` | 题目片段按文档 ID 查询 | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IDocumentPageRepository` | 文档页面按文档 ID 查询 | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：`ISearchIndexService` 通过 `IServiceProvider` 可选获取，可设为 null 模拟不可用场景。
- **回退逻辑可验证**：Mock `ISearchIndexService.ExactSearchAsync` 抛异常，验证回退到 `DatabaseSearchAsync`。
- **分页逻辑可验证**：通过 mock 仓储返回预设数据，验证游标分页的编码/解码和 skip/take 行为。
- **去重逻辑可验证**：构造重复 `DocumentName+PageNumber+SegmentId` 的数据，验证去重保留首条。
