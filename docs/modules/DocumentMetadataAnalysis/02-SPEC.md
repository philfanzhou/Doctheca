# 02-SPEC — Document Metadata Analysis 详细规格

## 1. 数据库变更

### 1.1 document_files 表新增列

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `subject` | varchar(50) | nullable | 学科:English/语文/数学/物理/化学/生物/其他 |
| `grade` | varchar(20) | nullable | 年级:K/G1-G12 |
| `year` | varchar(10) | nullable | 年份:4位数字如 2024 |

#### 数据库初始化方式

本服务**不使用 EF Core Migration**。数据库初始化由 `DatabaseInitializer.cs` 通过原生 SQL 完成:

- **新建库路径** — `GetTableCreationSql("document_files")` 返回的 `CREATE TABLE IF NOT EXISTS document_files (...)` 已直接内联 `subject character varying(50) NULL` / `grade character varying(20) NULL` / `year character varying(10) NULL` 三列(位于 `updated_at` 之后、主键约束之前)。
- **存量库路径** — `EnsureColumnsAsync` 中的 `ADD COLUMN IF NOT EXISTS` 兜底:

```sql
ALTER TABLE document_files ADD COLUMN IF NOT EXISTS subject character varying(50) NULL;
ALTER TABLE document_files ADD COLUMN IF NOT EXISTS grade character varying(20) NULL;
ALTER TABLE document_files ADD COLUMN IF NOT EXISTS year character varying(10) NULL;
```

两条路径均带 `IF NOT EXISTS` 幂等保护,可重复执行。`DatabaseInitializer.InitializeAsync` 在 `Program.cs` 启动时调用。

### 1.2 实体与模型映射

```csharp
// DocumentFileEntity.cs
[Column("subject")]
[MaxLength(50)]
public string? Subject { get; set; }

[Column("grade")]
[MaxLength(20)]
public string? Grade { get; set; }

[Column("year")]
[MaxLength(10)]
public string? Year { get; set; }
```

`DocumentFileModel` 同步新增对应字段。`DocumentFileRepository` 内部的 `private static MapToEntity` / `MapToModel` 同步映射(与 Entity 的 Subject/Grade/Year 对应)。

## 2. 接口变更

### 2.1 IDocumentFileRepository

```csharp
/// <summary>
/// Update subject/grade/year metadata. Null parameters are ignored (not cleared).
/// </summary>
Task<bool> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year);
```

### 2.2 IDocumentFileService

```csharp
/// <summary>
/// Update subject/grade/year metadata. Null parameters are ignored (not cleared).
/// Returns the updated model, or null if not found.
/// </summary>
Task<DocumentFileModel?> UpdateMetadataAsync(Guid id, string? subject, string? grade, string? year);
```

### 2.3 IDocumentAnalysisService

```csharp
/// <summary>
/// Maximum text chunk size in characters for LLM calls.
/// Dynamically calculated from model context length at initialization.
/// </summary>
int ChunkSize { get; }

/// <summary>
/// Maximum number of concurrent LLM calls.
/// </summary>
int MaxConcurrency { get; }

/// <summary>
/// Initialize model parameters. Called at startup and lazily on first use.
/// </summary>
Task InitializeAsync(CancellationToken cancellationToken = default);

/// <summary>
/// Analyze document text preview to determine subject, grade, and year metadata.
/// Focused prompt for metadata only (no segmentation strategy).
/// Returns null on failure (caller should treat as best-effort).
/// </summary>
Task<DocumentMetadataAnalysis?> AnalyzeMetadataAsync(string textPreview, CancellationToken cancellationToken = default);
```

### 2.4 ISearchIndexService

```csharp
/// <summary>
/// Update subject/grade/year for all indexed blocks of a document file.
/// Used when metadata is set/updated after initial indexing.
/// </summary>
Task UpdateDocumentFileMetadataAsync(Guid documentFileId, string? subject, string? grade, string? year);
```

### 2.5 HTTP 端点

```
PUT /admin/document-files/{id}/metadata
Content-Type: application/json

Request (UpdateMetadataRequest DTO):
{
  "subject": "English",   // optional, null/omitted = no change
  "grade": "G10",         // optional, null/omitted = no change
  "year": "2024"          // optional, null/omitted = no change
}

Response 200:
{
  "success": true,
  "data": {
    "id": "...",
    "fileName": "...",
    "subject": "English",
    "grade": "G10",
    "year": "2024",
    "updatedAt": "..."
  }
}

Response 404: { "success": false, "message": "File not found", "errorCode": "DOCLIBRARY_FILE_NOT_FOUND" }
```

**DTO 定义** (`DocumentFileEndpoints.cs`):

```csharp
/// <summary>
/// Request body for PUT /admin/document-files/{id}/metadata.
/// All fields optional — null means "no change", empty string means "clear".
/// </summary>
public record UpdateMetadataRequest
{
    public string? Subject { get; init; }
    public string? Grade { get; init; }
    public string? Year { get; init; }
}
```

**空值语义**:`null` 表示"不修改";`""`(空字符串)会被写入 DB(实际为空串,不视为 NULL)。当前实现不在端点层做 MaxLength 校验,由 DB 列约束兜底(subject=50, grade=20, year=10)。

## 3. DocumentMetadataAnalysis 模型

```csharp
namespace Ruoyu.Study.DocLibrary.Domain.Models;

/// <summary>
/// LLM metadata analysis result. Fields are null if LLM could not determine them.
/// </summary>
public record DocumentMetadataAnalysis
{
    public string? Subject { get; init; }
    public string? Grade { get; init; }
    public string? Year { get; init; }
}
```

> 注: 反序列化使用 `JsonNamingPolicy.SnakeCaseLower` + `JsonIgnoreCondition.WhenWritingNull`,响应中字段为空字符串 / 空白 / `"null"` 字面量时均视为 null。
```

## 4. LLM 提示词

`BuildMetadataAnalysisPrompt` 构造 prompt,请求体由 `BuildRequestBody` 组装(含 system + user 双角色、`max_tokens`、`temperature`、`stream = true`)。

**system prompt**:`直接返回JSON，不要解释。`

**user prompt**(`BuildMetadataAnalysisPrompt`,字符串插值):

```
你是一个文档分析专家。分析以下文档内容，识别学科和年级。

学科类型：
- English：英语教材、阅读材料
- 语文：语文教材、文言文、现代文
- 数学：数学教材、习题集
- 物理：物理教材、实验报告
- 化学：化学教材、实验报告
- 生物：生物教材
- 其他：无法明确判断

年级（从标题或内容推断）：
- K：幼儿园/学前
- G1-G12：小学一年级到高三
- 无法判断时返回 null

年份（从标题、页眉、版权页等推断，4位数字）：
- 无法判断时返回 null

请分析以下文档内容并返回 JSON。只返回 JSON，不要有其他文字。

```json
{
  "subject": "学科或null",
  "grade": "年级或null",
  "year": "年份或null"
}
```

文档内容：
---
{{textPreview}}
---
```

## 5. 实现规格

### 5.1 DocumentAnalysisService.AnalyzeMetadataAsync

```
1. await InitializeAsync(ct)  // 首次调用时解析 ContextLength、计算内部字段 _chunkSize / _maxTokensValue，不修改注入的 DocumentAnalysisOptions
2. if textPreview is null/whitespace → return null, log warning "Empty text preview provided for metadata analysis"
3. if ChunkSize <= 0 || MaxTokensValue <= 0 (LLM 初始化失败/未配置)→ return null, log info
4. truncate textPreview to first 2000 chars
5. DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt(preview)
6. BuildRequestBody(prompt) → system="直接返回JSON，不要解释。" + user prompt, stream=true
7. try:
   - CallStreamingAsync(requestBody, ct)
   - DocumentAnalysisResponseParser.ParseMetadataAnalysis(response, _logger, JsonOptions): 去 markdown 代码块包装、反序列化、空字符串/空白/"null"字面量→null
   - return result
8. catch any exception:
   - log warning(ex) "LLM metadata analysis failed, returning null"
   - return null
```

### 5.2 MinerUFileParseWorker 集成

在 `PersistParseResultAsync` 和 `PersistMergedChunkResultsAsync` 末尾（状态变为 Parsed 之后、OpenSearch 索引之后）调用新方法:

```
AnalyzeMetadataIfMissingAsync(parse.DocumentFileId, result.Markdown, scopeProvider, ct)
```

```
AnalyzeMetadataIfMissingAsync(documentFileId, markdownContent, scopeProvider, ct):
1. resolve IDocumentFileService from scope
2. get document file by id
3. if file not found → log warning "Document file not found for metadata analysis: {FileId}", return
4. if file.Subject/file.Grade/file.Year 全部非空(!IsNullOrWhitespace) →
     log info "Metadata already set for file {FileId}, skip LLM analysis (Subject={Subject}, Grade={Grade}, Year={Year})", return
5. resolve IDocumentAnalysisService? from scope (GetService, 可能 null)
6. if llmService is null → log info "LLM not configured, skip metadata analysis for file {FileId}", return
7. if markdownContent is null/whitespace → log info "No markdown content for file {FileId}, skip metadata analysis", return
8. extract first 2000 chars of markdownContent (Worker 层截断,AnalyzeMetadataAsync 内会二次截断作为冗余保护)
9. log info "Starting LLM metadata analysis for file {FileId}"
10. try:
    - analysis = await llmService.AnalyzeMetadataAsync(textPreview, ct)
    - if analysis is null → log warning "LLM metadata analysis returned null for file {FileId}", return
    - determine newSubject = file.Subject 非空 ? file.Subject : analysis.Subject
      (即: IsNullOrWhitespace(file.Subject) 时取 analysis.Subject,否则保留原值)
    - determine newGrade / newYear 同理
    - if newSubject == file.Subject && newGrade == file.Grade && newYear == file.Year (无变化)→
        log info "LLM did not provide any missing metadata for file {FileId}", return
    - await fileService.UpdateMetadataAsync(documentFileId, newSubject, newGrade, newYear)
    - log info "Metadata updated by LLM for file {FileId}: Subject={Subject}, Grade={Grade}, Year={Year}"
    - try: await searchIndexService.UpdateDocumentFileMetadataAsync(documentFileId, newSubject, newGrade, newYear)
      catch(syncEx): log warning "Failed to sync OpenSearch metadata for file {FileId}"
11. catch OperationCanceledException when ct.IsCancellationRequested → rethrow
12. catch any other exception → log warning(ex) "LLM metadata analysis failed for file {FileId}", return (do not throw)
```

### 5.3 DocumentFileEndpoints — PUT /{id}/metadata

```
1. parse request body { subject?, grade?, year? }
2. call fileService.UpdateMetadataAsync(id, subject, grade, year)
3. if returns null → 404
4. try: searchIndexService.UpdateDocumentFileMetadataAsync(id, updated.Subject, updated.Grade, updated.Year)
5. catch: log warning
6. return 200 with updated model
```

### 5.4 OpenSearchIndexService.UpdateDocumentFileMetadataAsync

使用 OpenSearch `_update_by_query` API 按 `document_file_id` 匹配并更新 subject/grade/year:

```json
{
  "query": { "term": { "document_file_id": "<fileId>" } },
  "script": {
    "source": "ctx._source.subject = params.subject; ctx._source.grade = params.grade; ctx._source.year = params.year;",
    "params": { "subject": "...", "grade": "...", "year": "..." }
  }
}
```

## 6. 错误处理

| 场景 | 处理 | 代码位置 |
|------|------|---------|
| LLM 服务未注册(ApiKey 为空) | `IDocumentAnalysisService` 未注册,Worker 取到 null 后 skip,记 Info 日志 | `Program.cs` DI 条件注册;`MinerUFileParseWorker.AnalyzeMetadataIfMissingAsync:330-335` |
| LLM 未初始化/已禁用(ChunkSize<=0) | `AnalyzeMetadataAsync` 内部 return null,记 Info 日志 | `DocumentAnalysisService.AnalyzeMetadataAsync:171-175` |
| LLM 调用超时/网络错误 | catch → return null,Worker 记 Warning 日志 | `MinerUFileParseWorker:387-390` |
| LLM 返回非 JSON | `ParseMetadataAnalysis` catch JsonException → return null | `DocumentAnalysisService:294-298` |
| LLM 返回的空字符串/空白/"null"字面量 | 视为 null,仅填充实际有值的字段 | `DocumentAnalysisService.ParseMetadataAnalysis:277-285` |
| OpenSearch 更新失败 | inner try/catch,记 Warning 日志(syncEx),不阻塞 | `MinerUFileParseWorker:373-381` |
| 文档不存在 | `GetByIdAsync` → null,记 Warning 日志,不阻塞 | `MinerUFileParseWorker:312-316` |
| HTTP 请求体解析失败 | 端点层 catch → 400 `"Invalid request body"` | `DocumentFileEndpoints:285-289` |

## 7. 测试策略

### 7.1 当前测试现状

**当前无单元测试**。`src/Tests/Ruoyu.Study.DocLibrary.Tests/` 目录下不存在 `DocumentAnalysisServiceTests.cs`,元分析相关功能目前零测试覆盖。

### 7.2 建议单元测试方向 (UT)

以下测试方向基于纯逻辑 `internal` 可测方法设计,不依赖 LLM HTTP 或数据库:

- `DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt(string textPreview)`
- `DocumentAnalysisResponseParser.ParseMetadataAnalysis(string response, ILogger<DocumentAnalysisService> logger, JsonSerializerOptions jsonOptions)`
- `DocumentAnalysisService.ParseTokenCount(string? value)`

| # | 建议覆盖方向 | 涉及方法 | 覆盖 |
|---|------------|---------|------|
| UT-DM-01 | 空文本/空白文本 → `AnalyzeMetadataAsync` 返回 null | `AnalyzeMetadataAsync` | FR-04, FR-05 |
| UT-DM-02 | `ChunkSize <= 0`(LLM 未初始化)→ 返回 null | `AnalyzeMetadataAsync` | FR-05 |
| UT-DM-03 | 有效 JSON 响应 → 返回正确 subject/grade/year | `DocumentAnalysisResponseParser.ParseMetadataAnalysis` | FR-03, FR-04 |
| UT-DM-04 | JSON 中字段为空字符串 / 空白 → 视为 null | `DocumentAnalysisResponseParser.ParseMetadataAnalysis` | FR-04 |
| UT-DM-05 | JSON 中字段为 `"null"` 字符串字面量 → 视为 null | `DocumentAnalysisResponseParser.ParseMetadataAnalysis` | FR-04 |
| UT-DM-06 | 无效 JSON → 返回 null | `DocumentAnalysisResponseParser.ParseMetadataAnalysis` | FR-05 |
| UT-DM-07 | 长文本输入 → 截断到 2000 字符 | `AnalyzeMetadataAsync` | FR-04 |
| UT-DM-08 | `BuildMetadataAnalysisPrompt` 包含学科/年级/年份说明 | `DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt` | FR-04 |
| UT-DM-09 | `BuildMetadataAnalysisPrompt` 不含拆段策略关键词 | `DocumentAnalysisPromptBuilder.BuildMetadataAnalysisPrompt` | FR-04 |
| UT-DM-10 | markdown 代码块包装的 JSON 可正确解析 | `DocumentAnalysisResponseParser.ParseMetadataAnalysis` | FR-04 |
| UT-DM-11 | `"128K"` / `"1M"` / 纯数字 → 正确解析 token 数 | `DocumentAnalysisService.ParseTokenCount` | 初始化逻辑 |

### 7.3 建议集成测试方向 (IT)

| # | 建议覆盖方向 | 覆盖 |
|---|------------|------|
| IT-DM-01 | 解析完成后元数据全有值时不调用 LLM | AC-03 |
| IT-DM-02 | 解析完成后元数据缺失时调用 LLM 填充 | AC-04 |
| IT-DM-03 | LLM 失败不影响解析状态 | AC-05 |
| IT-DM-04 | LLM 未配置时跳过分析 | AC-06 |
| IT-DM-05 | 手动设置元数据后 OpenSearch 同步刷新 | AC-07 |
| IT-DM-06 | LLM 不覆盖已有元数据 | AC-08 |

## 8. 影响范围

| 组件 | 影响 |
|------|------|
| `document_files` 表 | 新增 3 列（nullable） |
| `DocumentFileEndpoints` | 新增 PUT 端点 |
| `MinerUFileParseWorker` | 解析完成后增加元数据分析步骤 |
| `DocumentAnalysisService` | 新增 AnalyzeMetadataAsync 方法（不修改现有方法） |
| `OpenSearchIndexService` | 新增 UpdateDocumentFileMetadataAsync 方法 |
| 前端管理页 | 后续可添加元数据显示/编辑（本期不实现） |
| 部署脚本 | 无变更（复用现有 LLM 配置） |

## 9. 部署脚本

**不修改**。现有 `start.sh` 中的 `LLM_API_KEY` / `LLM_BASE_URL` / `LLM_MODEL` 配置保留，`AnalyzeMetadataAsync` 复用同一套 `LlmDocumentAnalysis` 配置。
