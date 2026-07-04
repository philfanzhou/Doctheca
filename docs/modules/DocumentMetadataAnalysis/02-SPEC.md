# 02-SPEC — Document Metadata Analysis 详细规格

## 1. 数据库变更

### 1.1 document_files 表新增列

| 列名 | 类型 | 约束 | 说明 |
|------|------|------|------|
| `subject` | varchar(50) | nullable | 学科:English/语文/数学/物理/化学/生物/其他 |
| `grade` | varchar(20) | nullable | 年级:K/G1-G12 |
| `year` | varchar(10) | nullable | 年份:4位数字如 2024 |

EF Core 迁移文件:`AddMetadataToDocumentFiles`。

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

`DocumentFileModel` 同步新增对应字段。`DocumentFileRepository.MapToEntity` / `MapToModel` 同步映射。

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

### 2.3 ILlmSegmentationService

```csharp
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
  "id": "...",
  "fileName": "...",
  "subject": "English",
  "grade": "G10",
  "year": "2024",
  ...
}

Response 404: Document not found
```

**DTO 定义** (`DocumentFileEndpoints.cs`):

```csharp
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

## 4. LLM 提示词

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
{textPreview}
---
```

## 5. 实现规格

### 5.1 LlmSegmentationService.AnalyzeMetadataAsync

```
1. await InitializeAsync(ct)
2. if textPreview is empty → return null, log warning
3. truncate textPreview to 2000 chars
4. build prompt (focused on metadata only)
5. try:
   - call CallLlmAsync(prompt, ct)
   - parse JSON → DocumentMetadataAnalysis
   - return result
6. catch any exception:
   - log warning "LLM metadata analysis failed"
   - return null
```

### 5.2 MinerUFileParseWorker 集成

在 `PersistParseResultAsync` 和 `PersistMergedChunkResultsAsync` 末尾（状态变为 Parsed 之后、OpenSearch 索引之后）调用新方法:

```
AnalyzeMetadataIfMissingAsync(parse.DocumentFileId, result.Markdown, scopeProvider, ct)
```

```
AnalyzeMetadataIfMissingAsync(documentFileId, markdownContent, scopeProvider, ct):
1. resolve ILlmSegmentationService? from scope (may be null if not configured)
2. resolve IDocumentFileService from scope
3. get document file by id
4. if file not found → log warning, return
5. if file.Subject, file.Grade, file.Year all non-empty → log info "metadata already set, skip LLM", return
6. if llmService is null → log info "LLM not configured, skip metadata analysis", return
7. if markdownContent is empty → log info "no markdown content, skip", return
8. extract first 2000 chars of markdownContent
9. try:
   - analysis = await llmService.AnalyzeMetadataAsync(textPreview, ct)
   - if analysis is null → log warning "LLM returned null", return
   - determine newSubject = file.Subject ?? analysis.Subject (only fill if currently empty)
   - determine newGrade = file.Grade ?? analysis.Grade
   - determine newYear = file.Year ?? analysis.Year
   - if all three unchanged (LLM didn't provide any missing values) → log info, return
   - await fileService.UpdateMetadataAsync(documentFileId, newSubject, newGrade, newYear)
   - log info "metadata updated by LLM"
   - try: await searchIndexService.UpdateDocumentFileMetadataAsync(documentFileId, newSubject, newGrade, newYear)
   - catch: log warning "failed to sync OpenSearch metadata"
10. catch OperationCanceledException → rethrow
11. catch any other exception → log warning "LLM metadata analysis failed", return (do not throw)
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

| 场景 | 处理 |
|------|------|
| LLM 服务未注册 | 跳过分析，记 Info 日志 |
| LLM 调用超时 | 跳过分析，记 Warning 日志 |
| LLM 返回非 JSON | 跳过分析，记 Warning 日志 |
| LLM 返回 null 字段 | 仅更新非 null 的字段 |
| OpenSearch 更新失败 | 记 Warning 日志，不阻塞 |
| 文档不存在 | 记 Warning 日志，不阻塞 |

## 7. 测试策略

### 7.1 单元测试 (UT)

纯逻辑测试，不依赖 LLM HTTP 或数据库。共 15 个测试，位于 `LlmSegmentationServiceTests.cs`。

| # | 测试方法 | 覆盖 |
|---|---------|------|
| UT-DM-01 | `AnalyzeMetadataAsync_EmptyText_ReturnsNull` — 空文本返回 null | FR-04, FR-05 |
| UT-DM-02 | `AnalyzeMetadataAsync_ValidResponse_ReturnsMetadata` — 有效响应返回正确元数据 | FR-03, FR-04 |
| UT-DM-03 | `AnalyzeMetadataAsync_NullFieldsInResponse_ReturnsNullFields` — JSON 中 null 字段保持 null | FR-04 |
| UT-DM-04 | `AnalyzeMetadataAsync_EmptyStringFieldsInResponse_ReturnsNullFields` — 空字符串字段视为 null | FR-04 |
| UT-DM-05 | `AnalyzeMetadataAsync_NullStringLiteralInResponse_ReturnsNullFields` — "null" 字符串字面量视为 null | FR-04 |
| UT-DM-06 | `AnalyzeMetadataAsync_InvalidJson_ReturnsNull` — 无效 JSON 返回 null | FR-05 |
| UT-DM-07 | `AnalyzeMetadataAsync_LlmCallFails_ReturnsNull` — LLM 调用失败返回 null | FR-05 |
| UT-DM-08 | `AnalyzeMetadataAsync_LongText_TruncatesTo2000Chars` — 长文本截断到 2000 字符 | FR-04 |
| UT-DM-09 | `BuildMetadataAnalysisPrompt_ContainsSubjectInstructions` — 提示词包含学科说明 | FR-04 |
| UT-DM-10 | `BuildMetadataAnalysisPrompt_ContainsGradeInstructions` — 提示词包含年级说明 | FR-04 |
| UT-DM-11 | `BuildMetadataAnalysisPrompt_ContainsYearInstructions` — 提示词包含年份说明 | FR-04 |
| UT-DM-12 | `BuildMetadataAnalysisPrompt_DoesNotContainSegmentationStrategy` — 提示词不含拆段策略 | FR-04 |
| UT-DM-13 | `BuildMetadataAnalysisPrompt_ContainsTextInput` — 提示词包含文本输入占位 | FR-04 |
| UT-DM-14 | `ParseMetadataAnalysis_MarkdownCodeBlockWrapper_ParsesCorrectly` — markdown 代码块包装的 JSON 可解析 | FR-04 |
| UT-DM-15 | `ParseMetadataAnalysis_PartialFields_PreservesNulls` — 部分字段缺失时保留 null | FR-04 |

### 7.2 集成测试 (规划)

| # | 测试用例 | 覆盖 |
|---|---------|------|
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
| `LlmSegmentationService` | 新增 AnalyzeMetadataAsync 方法（不修改现有方法） |
| `OpenSearchIndexService` | 新增 UpdateDocumentFileMetadataAsync 方法 |
| 前端管理页 | 后续可添加元数据显示/编辑（本期不实现） |
| 部署脚本 | 无变更（复用现有 LLM 配置） |

## 9. 部署脚本

**不修改**。现有 `start.sh` 中的 `LLM_API_KEY` / `LLM_BASE_URL` / `LLM_MODEL` 配置保留，`AnalyzeMetadataAsync` 复用同一套 `LlmSegmentation` 配置。
