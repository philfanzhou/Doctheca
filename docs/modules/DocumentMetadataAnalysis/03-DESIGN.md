# DocumentMetadataAnalysis — 设计说明

## 本功能在项目中的目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentMetadataAnalysis.cs        # LLM 分析结果 record (Subject/Grade/Year)
│   │   │   ├── DocumentAnalysisOptions.cs          # LLM 配置选项 (继承 AiClientOptions)
│   │   │   ├── DocumentFileModel.cs                # 文件模型 (含 Subject/Grade/Year)
│   │   │   └── DocLibraryConstants.cs              # 学科/年级常量 (ValidSubjects/ValidGrades,本功能未强制校验)
│   │   ├── Services/
│   │   │   ├── IDocumentAnalysisService.cs         # LLM 分析接口
│   │   │   ├── IDocumentFileService.cs             # 文件服务接口(含 UpdateMetadataAsync)
│   │   │   ├── DocumentFileService.cs              # 文件服务实现
│   │   │   └── ISearchDomainService.cs             # 搜索领域服务
│   │   └── Repositories/
│   │       ├── IDocumentFileRepository.cs           # 文件仓储接口(含 UpdateMetadataAsync)
│   │       ├── ISearchIndexService.cs               # 搜索索引接口(含 UpdateDocumentFileMetadataAsync)
│   │       └── (DocumentFileRepository.cs)           # 文件仓储实现 (private MapToEntity/MapToModel)
│   ├── Service/
│   │   ├── DocumentAnalysisService.cs               # LLM 分析实现 (OpenAiCompatibleClient 流式 SSE)
│   │   ├── Analysis/
│   │   │   ├── DocumentAnalysisPromptBuilder.cs     # prompt 构建 (纯静态逻辑)
│   │   │   └── DocumentAnalysisResponseParser.cs    # 响应解析 (含 response records、ExtractJson)
│   │   ├── MinerUFileParseWorker.cs                 # 后台解析 Worker (含 AnalyzeMetadataIfMissingAsync)
│   │   ├── OpenSearchIndexService.cs                # OpenSearch 索引服务 (含 UpdateDocumentFileMetadataAsync)
│   │   └── Endpoints/
│   │       └── DocumentFileEndpoints.cs             # PUT /{id}/metadata 端点 + UpdateMetadataRequest record
│   ├── Database/
│   │   ├── DatabaseInitializer.cs                   # SQL-based 初始化 (CREATE TABLE IF NOT EXISTS / ADD COLUMN IF NOT EXISTS)
│   │   ├── DocLibraryDbContext.cs                   # EF Core DbContext (Fluent API)
│   │   └── Entities/
│   │       └── DocumentFileEntity.cs                 # 含 Subject/Grade/Year 列
│   └── Host/
│       ├── Program.cs                               # DI 注册 + 启动初始化
│       └── appsettings.json                         # LlmDocumentAnalysis 配置节
└── docs/modules/DocumentMetadataAnalysis/
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    ├── 03-DESIGN.md  (本文档)
    ├── 04-TASKS.md
    ├── 05-TESTS.md
    └── 06-CONVENTIONS.md
```

## 关键接口签名

### IDocumentAnalysisService

```csharp
Task<DocumentMetadataAnalysis?> AnalyzeMetadataAsync(string textPreview, CancellationToken cancellationToken = default);
```

### IDocumentFileService（新增方法）

```csharp
Task<DocumentFileModel?> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year);
```

### ISearchIndexService（新增方法）

```csharp
Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year);
```

### HTTP 端点

```
PUT /admin/document-files/{id}/metadata
Content-Type: application/json

Request: { "subject": "English", "grade": "G10", "year": "2024" }  // 全部可选
Response 200: { "id": "...", "fileName": "...", "subject": "English", "grade": "G10", "year": "2024", ... }
Response 404: { "success": false, "message": "File not found", "errorCode": "DOCLIBRARY_FILE_NOT_FOUND" }
```

## 数据流

### PUT /{id}/metadata 手动更新流程

1. HTTP 层接收请求，解析 `UpdateMetadataRequest` body
2. 调用 `DocumentFileService.UpdateMetadataAsync(id, subject, grade, year)`
3. Repository 层仅更新非 null 字段（null = 不修改）
4. Best-effort：调用 `ISearchIndexService.UpdateDocumentFileMetadataAsync` 刷新 OpenSearch 索引
5. 返回更新后的文档信息

### 解析完成后自动 LLM 分析流程

1. `MinerUFileParseWorker` 在 `PersistParseResultAsync`(单文件)与 `PersistMergedChunkResultsAsync`(大文件分块合并且 status=`Parsed`)末尾调用 `AnalyzeMetadataIfMissingAsync` — 失败/部分失败(analyze)时不调用
2. 检查 `document_files` 的 subject/grade/year：
   - `!IsNullOrWhitespace` 全部满足 → 跳过 LLM（Info 日志）
   - `IDocumentAnalysisService` 未注册(`GetService` → null)→ 跳过（Info 日志）
   - Markdown 为 null/whitespace → 跳过
3. 提取 Markdown 前 2000 字符(Worker 层截断,服务内二次截断为冗余保护),调用 `DocumentAnalysisService.AnalyzeMetadataAsync`
4. LLM 返回 `DocumentMetadataAnalysis` record(字段可能为 null)
5. 仅填充当前为空/空白的字段(`IsNullOrWhitespace(file.Subject)` 时取 `analysis.Subject`),已有值绝不覆盖
6. 无实际变化时跳过 DB 写入(避免空事务)
7. 调用 `DocumentFileService.UpdateMetadataAsync(id, newSubject, newGrade, newYear)` 更新 DB
8. Best-effort:inner try/catch 调用 `ISearchIndexService.UpdateDocumentFileMetadataAsync` 刷新 OpenSearch(失败仅记 Warning)

## LLM 调用详情

### DocumentAnalysisService.AnalyzeMetadataAsync

- **初始化**：`InitializeAsync`(启动时调用 + 首次 `AnalyzeMetadataAsync` 时懒加载)。解析 `ContextLength` 配置(支持 `"128K"` / `"1M"` / 纯数字),若未配置则调用 `TryFetchContextLengthAsync`(GET `/v1/models/{Model}`)动态获取。根据 context 计算内部字段 `_chunkSize`(`(contextLength - 200 - _maxTokensValue) × 0.8 × 1.5`,cap `2500`)与 `_maxTokensValue`(默认 `4096`)。无 context 时禁用 LLM(`_chunkSize = _maxTokensValue = 0`)。服务不再修改注入的 `DocumentAnalysisOptions` POCO。
- **输入截断**：取 Markdown 前 2000 字符(`textPreview[..2000]`)。注: `MinerUFileParseWorker.AnalyzeMetadataIfMissingAsync` 在调用前已截断一次,此处为冗余保护。
- **请求体**：`BuildRequestBody` 组装 — system prompt `"直接返回JSON，不要解释。"` + user prompt(`DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt`)、`max_tokens = _maxTokensValue`、`temperature = 0.1`(只读属性)、`stream = true`。
- **流式 SSE 调用**：通过共享的 `OpenAiCompatibleClient.CallStreamingAsync` (`Ruoyu.Study.Common.Ai` 命名空间,底层 `OpenAiSseReader`),per-attempt timeout + SSE idle timeout(`StreamIdleTimeoutSeconds` 默认 60s)由客户端保障。
- **响应解析**：`DocumentAnalysisResponseParser.ParseMetadataAnalysis` — `DocumentAnalysisResponseParser.ExtractJson` 去 ```json``` / ```` ``` 包装;`JsonSerializer.Deserialize` 使用 `SnakeCaseLower` + `WhenWritingNull`;空字符串/空白/`"null"` 字面量一律视为 null。
- **失败处理**：任何异常 catch 后 `LogWarning` 并返回 null,Worker 层按 best-effort 处理。

### 关键 internal 测试入口

以下 `internal` 方法为纯逻辑测试入口(需 `InternalsVisibleTo` 测试项目):

- `DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt(string textPreview)` — 静态,返回 user prompt
- `DocumentAnalysisResponseParser.ParseMetadataAnalysis(string response, ILogger<DocumentAnalysisService> logger, JsonSerializerOptions jsonOptions)` — 解析 LLM 原始响应
- `DocumentAnalysisService.ParseTokenCount(string? value)` — 解析 `"128K"` / `"1M"` / 纯数字

## 错误处理

| 场景 | 处理 |
|------|------|
| LLM 服务未注册 | 跳过分析，记 Info 日志 |
| LLM 调用超时/网络错误 | 跳过分析，记 Warning 日志 |
| LLM 返回非 JSON | 跳过分析，记 Warning 日志 |
| LLM 返回 null 字段 | 仅更新非 null 字段 |
| OpenSearch 更新失败 | 记 Warning 日志，不阻塞 |
| 文档不存在 | 记 Warning 日志，不阻塞 |

## 外部依赖

| 接口 | 提供能力 | 所在模块 |
|------|---------|---------|
| `IDocumentAnalysisService` | LLM 元数据分析 | `Ruoyu.Study.DocLibrary.Service` |
| `ISearchIndexService` | OpenSearch 元数据同步 | `Ruoyu.Study.DocLibrary.Service` |
| `IDocumentFileService` | 文件元数据 CRUD | `Ruoyu.Study.DocLibrary.Domain.Services` |

## DI 注册

```csharp
// Program.cs — LLM 服务仅在 LlmDocumentAnalysis 配置节存在且 ApiKey 非空时注册
var llmSection = builder.Configuration.GetSection(DocumentAnalysisOptions.SectionName);
if (llmSection.Exists() && !string.IsNullOrEmpty(llmSection["ApiKey"]))
{
    builder.Services.Configure<DocumentAnalysisOptions>(llmSection);
    builder.Services.AddHttpClient(nameof(OpenAiCompatibleClient));
    builder.Services.AddSingleton<OpenAiCompatibleClient>(sp => /* 命名 HttpClient + DocumentAnalysisOptions + StreamIdleTimeoutSeconds */);
    builder.Services.AddTransient<IDocumentAnalysisService, DocumentAnalysisService>();
}
// IDocumentFileService / ISearchIndexService / ISearchDomainService 始终注册
// ISearchIndexService → OpenSearchIndexService (Singleton)
// IDocumentFileService → DocumentFileService (Scoped)
```

> `IDocumentAnalysisService` 使用 `AddTransient`。Worker 通过 `GetService<IDocumentAnalysisService>()` 解析(可空),未注册时返回 null 后 skip。
