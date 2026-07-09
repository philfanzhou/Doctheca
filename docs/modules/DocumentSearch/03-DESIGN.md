# DocumentSearch — 设计说明 (DESIGN)

## 设计决策

### 索引与搜索为何同服务

索引写入与精确搜索共享同一套 OpenSearch mapping（`BuildIndexBody`），强耦合：
- 查询字段（`text` / `text.exact` / `file_name` / `subject` / `grade` / `year`）与索引字段必须严格一致。
- `ParseSearchResponse` 读取的字段名（`block_id` / `file_name` / `page_number`）与 `IndexParseBlocksAsync` 写入的字段名同源。
- 拆分两个服务会导致 mapping 变更时需跨服务同步，增加不一致风险。

因此合并到同一模块文档（本模块），映射与查询逻辑集中维护。

### best-effort 索引策略

所有 OpenSearch 写操作（索引/删除/元数据同步）均为 best-effort：
- 失败仅记录 Warning 日志，不抛异常给外层。
- **理由**：搜索是辅助能力，不应阻塞/破坏核心解析链路。解析结果持久化到数据库后，即使 OpenSearch 索引失败，数据不丢，仅搜索暂时不可用。
- Worker 与端点层统一用 `try/catch + LogWarning` 包裹。

### 搜索降级策略

`SearchDomainService` 作为 `ISearchIndexService` 的薄封装层，统一处理异常：
- 捕获**任意**异常（不限特定类型），LogWarning 后返回空结果。
- **理由**：HTTP 搜索请求不应因 OpenSearch 故障而返回 500；调用方（前端/下游）收到空结果可正常渲染"无结果"。
- 降级逻辑在领域层（`SearchDomainService`），而非 HTTP 端点层，保证复用。

### 查询构建可测试性

`OpenSearchIndexService` 的 `OpenSearchLowLevelClient` 在构造函数中 `new` 创建，难以注入 Mock。因此将纯逻辑提取为 `internal static` 方法：
- `BuildIndexBody()` — 索引 mapping/settings
- `BuildSearchBody(query, phrase, filter, pageSize, pageToken, logger)` — 搜索请求体
- `ParseSearchResponse(responseJson, phrase, pageSize)` — 响应解析

这些方法无外部依赖，单元测试可直接调用验证字段/逻辑正确性（通过 `InternalsVisibleTo` 暴露给测试程序集）。

## 目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── SearchResultModel.cs               # 搜索结果模型
│   │   │   ├── SearchFilterModel.cs               # 搜索过滤模型
│   │   │   ├── SearchMatchType.cs                 # 匹配类型常量
│   │   │   └── OpenSearchOptions.cs               # OpenSearch 配置节
│   │   ├── Services/
│   │   │   ├── ISearchDomainService.cs            # 搜索领域服务接口
│   │   │   └── SearchDomainService.cs             # 搜索领域服务实现（降级封装）
│   │   └── Repositories/
│   │       ├── ISearchIndexService.cs             # 搜索索引接口（6 个方法）
│   │       └── IDocumentParseBlockRepository.cs   # block 仓储接口（索引数据源）
│   ├── Service/
│   │   ├── Endpoints/
│   │   │   └── DocumentSearchEndpoints.cs         # GET /admin/documents/search
│   │   ├── OpenSearchIndexService.cs              # OpenSearch 索引服务实现
│   │   └── MinerUFileParseWorker.cs               # 解析完成后索引（best-effort）
│   └── Host/
│       ├── appsettings.json                       # OpenSearch 配置（Url / IndexName）
│       └── Program.cs                             # DI 注册 + 启动 EnsureIndexAsync
└── src/Tests/Ruoyu.Study.DocLibrary.Tests/
    ├── OpenSearchIndexServiceTests.cs             # Build/Parse 纯逻辑单元测试
    └── SearchDomainServiceTests.cs                # 领域服务降级测试
```

## 关键接口签名

### ISearchIndexService

见 [02-SPEC.md §1.1](./02-SPEC.md#11-isearchindexservice)。

### 注入的依赖

```csharp
// OpenSearchIndexService 构造函数
public OpenSearchIndexService(
    IOptions<OpenSearchOptions> options,
    IServiceProvider serviceProvider,
    ILogger<OpenSearchIndexService> logger)
```

- `IOptions<OpenSearchOptions>`：OpenSearch 配置。
- `IServiceProvider`：用于创建 scope 解析 `IDocumentParseBlockRepository`（scoped 生命周期）。
- `ILogger<OpenSearchIndexService>`：结构化日志。

```csharp
// SearchDomainService 构造函数
public SearchDomainService(
    ISearchIndexService searchIndexService,
    ILogger<SearchDomainService> logger)
```

- `ISearchIndexService`：**非 nullable**，由 DI 容器保证注入（注册为 Singleton）。
- `ILogger<SearchDomainService>`：降级时 LogWarning。

### DI 注册（`Program.cs`）

```csharp
builder.Services.Configure<OpenSearchOptions>(builder.Configuration.GetSection("OpenSearch"));
builder.Services.AddSingleton<ISearchIndexService, OpenSearchIndexService>();
builder.Services.AddScoped<ISearchDomainService, SearchDomainService>();
```

## 数据流

### 索引写入流程

```
MinerUFileParseWorker.PersistParseResultAsync
  → parseService.UpdateStatusAsync(Parsed)
  → IndexBlocksToSearchAsync (best-effort, try/catch LogWarning)
      → scopeProvider.GetRequiredService<ISearchIndexService>()
      → searchIndexService.IndexParseBlocksAsync(parseId, file.Id, file.FileName, file.Subject, file.Grade, file.Year)
          → 创建 scope → 解析 IDocumentParseBlockRepository
          → GetByParseIdAsync(parseId)
          → 过滤空 text_content → 构建 bulk → BulkAsync → 记日志
  → AnalyzeMetadataIfMissingAsync (best-effort)
      → LLM 分析 → fileService.UpdateMetadataAsync
      → searchIndexService.UpdateDocumentFileMetadataAsync (同步索引)
```

- 大文件分块解析（`PersistMergedChunkResultsAsync`）仅在 `status == Parsed` 时索引。
- 失败状态（`Failed`）不索引。

### 索引删除流程

```
DELETE /admin/document-parses/{parseId}
  → DocumentParseEndpoints.DeleteDocumentParse
  → parseService.DeleteParseAsync(parseId)
  → searchIndexService.DeleteParseIndexAsync(parseId) (best-effort)

DELETE /admin/document-files/{id}
  → DocumentFileEndpoints.DeleteDocumentFile
  → fileService.DeleteAsync(id)
  → OSS 清理 (best-effort)
  → searchIndexService.DeleteDocumentFileIndexAsync(id) (best-effort)
```

### 元数据同步流程

```
PUT /admin/document-files/{id}/metadata
  → DocumentFileEndpoints.UpdateDocumentFileMetadata
  → fileService.UpdateMetadataAsync(id, subject, grade, year)
  → searchIndexService.UpdateDocumentFileMetadataAsync (best-effort)

LLM 自动分析（AnalyzeMetadataIfMissingAsync）
  → fileService.UpdateMetadataAsync
  → searchIndexService.UpdateDocumentFileMetadataAsync (best-effort)
```

### 搜索流程

```
GET /admin/documents/search
  → DocumentSearchEndpoints.Search
  → 参数校验（query 空/过长 → 400）
  → pageSize = Math.Min(Math.Max(pageSize, 1), 100)
  → 构造 SearchFilterModel（仅当 subject/grade/year/documentTitle 任一非空）
  → searchService.ExactSearchAsync(query, phrase, filter, pageSize, pageToken)
      → _searchIndexService.ExactSearchAsync(...)
          → BuildSearchBody → _client.SearchAsync → ParseSearchResponse
      → 异常时：LogWarning → 返回空结果
  → 构建 JSON 响应（results / totalCount / nextPageToken）
```

### 启动初始化流程（`Program.cs`）

```
1. DatabaseInitializer.InitializeAsync(dbContext, loggerFactory)  — 数据库初始化（无 EF Core Migration）
2. searchIndexService.EnsureIndexAsync() (best-effort, 失败不阻塞启动)
   → EnsureIndexExistsAsync → 检查索引存在 → 不存在则 CreateAsync（使用 BuildIndexBody）
```

## 错误处理策略

| 场景 | 处理 |
|------|------|
| OpenSearch 索引失败 | Worker 捕获异常 → LogWarning，解析流程继续 |
| OpenSearch 删除失败 | 端点捕获异常 → LogWarning，删除流程继续 |
| OpenSearch 搜索失败 | `SearchDomainService` 捕获异常 → LogWarning，返回空结果 |
| OpenSearch 元数据同步失败 | 调用方捕获异常 → LogWarning，不阻塞 |
| blocks 为空 | LogWarning 返回，不抛异常 |
| `text_content` 为空 | 跳过该 block（不索引空文本） |
| `pageToken` 非法 Base64 | LogDebug，从第一页开始 |
| HTTP 参数校验失败 | 返回 400 + 错误码 |

## 依赖的外部模块接口

| 依赖 | 提供能力 | 所在模块 |
|------|---------|---------|
| `IDocumentParseBlockRepository` | 读取 parse 的 blocks（索引数据源） | `Ruoyu.Study.DocLibrary.Domain.Repositories` |
| `IOptions<OpenSearchOptions>` | OpenSearch 配置（Url / IndexName） | `Ruoyu.Study.DocLibrary.Domain.Models` |
| OpenSearch 2.x | 索引存储与搜索 | 外部服务（HTTP） |

## 可测试性设计

- **纯逻辑提取**：`BuildIndexBody` / `BuildSearchBody` / `ParseSearchResponse` 为 `internal static`，无 IO 依赖，可直接单元测试。
- **接口隔离**：`ISearchIndexService` / `ISearchDomainService` 通过构造函数注入，`SearchDomainService` 可用 Mock 验证。
- **降级可验证**：Mock `ISearchIndexService.ExactSearchAsync` 抛异常，验证 `SearchDomainService` 返回空结果 + LogWarning。
- **启动初始化 best-effort**：`EnsureIndexAsync` 失败不阻塞服务启动，仅记日志。
