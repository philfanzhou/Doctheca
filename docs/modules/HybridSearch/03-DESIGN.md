# HybridSearch — 设计说明 (DESIGN)

## 本功能在项目中的目录与文件结构

```
backend/ruoyu.docretrieval/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   └── SearchConfig.cs                    # SearchResultModel, SearchFilterModel, OpenSearchOptions, QdrantOptions 等
│   │   ├── Services/
│   │   │   └── SearchDomainService.cs             # 领域服务 (HybridSearchAsync, DatabaseSearchAsync)
│   │   └── Repositories/
│   │       ├── ISearchIndexService.cs             # 搜索索引接口 (HybridSearchAsync)
│   │       ├── IQdrantService.cs                  # 向量搜索接口 (SemanticSearchAsync)
│   │       ├── IDocumentRepository.cs             # 文档仓储接口
│   │       ├── IDocumentSegmentRepository.cs      # 文档片段仓储接口
│   │       ├── IQuestionSegmentRepository.cs      # 题目片段仓储接口
│   │       └── IDocumentPageRepository.cs         # 文档页面仓储接口
│   ├── Service/
│   │   ├── DocumentRetrievalServiceImpl.cs        # gRPC 实现 (HybridSearch)
│   │   ├── OpenSearchIndexService.cs              # OpenSearch 索引服务实现 (HybridSearchAsync)
│   │   └── QdrantService.cs                       # Qdrant 向量搜索实现
│   ├── Database/
│   │   └── Repositories/                          # 仓储实现
│   └── Contract/Protos/
│       ├── docretrieval.proto                     # gRPC 服务定义
│       └── docretrieval.common.proto              # 公共消息定义
├── test/
│   └── Services/
│       └── SearchDomainServiceTests.cs            # 单元测试
└── docs/modules/HybridSearch/                     # 本文档所在目录
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
rpc HybridSearch(HybridSearchRequest) returns (SearchResponse);

// src/Contract/Protos/docretrieval.common.proto
message HybridSearchRequest {
  string query = 1;
  bool phrase = 2;
  int32 exact_top_k = 3;
  int32 semantic_top_k = 4;
  SearchFilter filter = 5;
  int32 page_size = 6;
  string page_token = 7;
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
public async Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> HybridSearchAsync(
    string query, bool phrase, int exactTopK, int semanticTopK,
    SearchFilterModel? filter, int pageSize, string? pageToken)

private async Task<(List<SearchResultModel>, int, string?)> DatabaseSearchAsync(
    string query, bool phrase, SearchFilterModel? filter, int pageSize, string? pageToken)
```

### 搜索索引接口

```csharp
// src/Domain/Repositories/ISearchIndexService.cs
Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> HybridSearchAsync(
    string query, bool phrase, int exactTopK, int semanticTopK,
    SearchFilterModel? filter, int pageSize, string? pageToken);
```

### 向量搜索接口

```csharp
// src/Domain/Repositories/IQdrantService.cs
Task<List<SearchResultModel>> SemanticSearchAsync(string query, int topK, SearchFilterModel? filter);
```

### gRPC 服务实现

```csharp
// src/Service/DocumentRetrievalServiceImpl.cs
public DocumentRetrievalServiceImpl(
    SearchDomainService searchService,
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

- `ISearchIndexService?`：通过 `IServiceProvider.GetService` 可选获取，提供 OpenSearch 混合搜索能力。
- `IDocumentRepository`：文档仓储，提供 `GetListAsync` 用于获取文档列表。
- `IDocumentSegmentRepository`：文档片段仓储，提供 `GetByDocumentIdAsync`。
- `IQuestionSegmentRepository`：题目片段仓储，提供 `GetByDocumentIdAsync`。
- `IDocumentPageRepository`：文档页面仓储，提供 `GetByDocumentIdAsync` 用于页码映射。

### OpenSearchIndexService 额外依赖

```csharp
// OpenSearchIndexService 构造函数
public OpenSearchIndexService(
    IOptions<OpenSearchOptions> options,
    IServiceProvider serviceProvider,
    ILogger<OpenSearchIndexService> logger)
```

- `IQdrantService?`：通过 `IServiceProvider.GetService` 可选获取，提供语义搜索能力。

## 数据流描述（步骤序列）

### HybridSearch 完整流程

1. gRPC 层接收 `HybridSearchRequest`，调用 `ValidateSearchRequest` 校验参数（与 ExactSearch 共用校验逻辑）。
2. 参数修正：`pageSize = request.PageSize > 0 ? Math.Min(request.PageSize, 100) : 50`；`exactTopK = request.ExactTopK > 0 ? Math.Min(request.ExactTopK, 200) : 50`；`semanticTopK = request.SemanticTopK > 0 ? Math.Min(request.SemanticTopK, 100) : 20`。
3. `MapFilter` 将 `SearchFilter` 中空字符串字段转为 `null`。
4. 调用 `SearchDomainService.HybridSearchAsync(query, phrase, exactTopK, semanticTopK, filter, pageSize, pageToken)`。
5. 领域服务检查 `_searchIndexService` 是否可用：
   - **可用**：调用 `ISearchIndexService.HybridSearchAsync`，返回 OpenSearch 混合结果。
   - **不可用或异常**：捕获异常，LogWarning 记录，回退 `DatabaseSearchAsync` + MatchType 降级。
6. 返回 `(Results, TotalCount, NextToken)`。
7. gRPC 层构建 `SearchResponse`：填充 `results`、`total_count`、`next_page_token`。

### OpenSearch 混合搜索流程 (OpenSearchIndexService.HybridSearchAsync)

1. 调用 `ExactSearchAsync(query, phrase, filter, pageSize, pageToken)` 获取精确搜索结果。
2. 获取 `IQdrantService` 实例，调用 `SemanticSearchAsync(query, semanticTopK, filter)` 获取语义搜索结果。
3. Qdrant 不可用或异常时，语义结果为空列表，LogWarning 记录。
4. 合并去重：按 `$"{DocumentName}|{PageNumber}|{SegmentId}"` 去重，精确结果优先保留。
5. 排序：按匹配类型优先级（`exact_phrase(0) > exact_word(1) > stemmed(2) > semantic(3)`），同优先级按 `Score` 降序。
6. 返回合并结果。

### 数据库回退搜索流程 (SearchDomainService.HybridSearchAsync 回退路径)

1. 调用 `DatabaseSearchAsync(query, phrase, filter, pageSize, pageToken)` 获取数据库搜索结果。
2. 遍历结果，将 `MatchType == "exact_word"` 的记录降级为 `"stem_match"`。
3. 返回降级后的结果。

### DatabaseSearchAsync 内部流程

与 ExactSearch 的 `DatabaseSearchAsync` 完全相同：
1. 获取符合筛选条件的文档列表，过滤 `status == "ready"`。
2. 对每个文档遍历 segments 和 questions，使用 `IndexOf(query, OrdinalIgnoreCase)` 匹配。
3. 去重、排序、游标分页。

## 错误处理策略

- **参数校验**：在 gRPC 层前置校验（与 ExactSearch 共用 `ValidateSearchRequest`），无效参数直接抛出 `RpcException(StatusCode.InvalidArgument)`。
- **OpenSearch 回退**：`SearchDomainService` 捕获 OpenSearch 异常，LogWarning 后回退数据库搜索 + MatchType 降级。
- **Qdrant 降级**：`OpenSearchIndexService.HybridSearchAsync` 捕获 Qdrant 异常，LogWarning 记录，仅返回精确搜索结果。
- **page_token 解码失败**：`DatabaseSearchAsync` 中 catch 解码异常，`skip` 默认为 0。
- **空结果**：返回空 `results` 列表，`total_count = 0`，`next_page_token = ""`。

## 依赖的外部模块接口

| 接口 | 提供能力 | 所在模块 |
| --- | --- | --- |
| `ISearchIndexService` | OpenSearch 混合搜索 | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IQdrantService` | Qdrant 语义搜索 | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IDocumentRepository` | 文档列表查询（含筛选） | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IDocumentSegmentRepository` | 文档片段按文档 ID 查询 | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IQuestionSegmentRepository` | 题目片段按文档 ID 查询 | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |
| `IDocumentPageRepository` | 文档页面按文档 ID 查询 | `Ruoyu.Study.DocRetrieval.Domain.Repositories` |

## 可测试性设计

- **依赖注入接口化**：`ISearchIndexService` 和 `IQdrantService` 通过 `IServiceProvider` 可选获取，可设为 null 模拟不可用场景。
- **回退逻辑可验证**：Mock `ISearchIndexService.HybridSearchAsync` 抛异常，验证回退到 `DatabaseSearchAsync` + MatchType 降级。
- **Qdrant 降级可验证**：Mock `IQdrantService.SemanticSearchAsync` 抛异常，验证仅返回精确结果。
- **合并去重可验证**：构造精确和语义结果中有重复 `DocumentName+PageNumber+SegmentId` 的数据，验证精确结果优先保留。
- **排序可验证**：构造不同 MatchType 的结果，验证排序优先级正确。
