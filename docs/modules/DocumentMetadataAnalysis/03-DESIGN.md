# DocumentMetadataAnalysis — 设计说明

## 本功能在项目中的目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentMetadataAnalysis.cs        # LLM 分析结果 record
│   │   │   ├── DocumentAnalysisOptions.cs          # LLM 配置选项
│   │   │   └── DocLibraryConstants.cs            # 学科/年级常量
│   │   ├── Services/
│   │   │   ├── IDocumentAnalysisService.cs         # LLM 分析接口
│   │   │   ├── DocumentFileService.cs              # 文件服务（含 UpdateMetadataAsync）
│   │   │   └── ISearchDomainService.cs             # 搜索领域服务
│   │   └── Repositories/
│   │       ├── IDocumentFileRepository.cs           # 文件仓储接口（含 UpdateMetadataAsync）
│   │       └── ISearchIndexService.cs               # 搜索索引接口（含 UpdateDocumentFileMetadataAsync）
│   ├── Service/
│   │   ├── DocumentAnalysisService.cs               # LLM 分析实现（流式 SSE 调用）
│   │   └── Endpoints/
│   │       └── DocumentFileEndpoints.cs             # PUT /{id}/metadata 端点
│   └── Database/
│       ├── Entities/
│       │   └── DocumentFileEntity.cs                 # 含 Subject/Grade/Year
│       └── DocRetrievalDbContext.cs                  # Fluent API 配置
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

1. `MinerUFileParseWorker` 在 `PersistParseResultAsync` / `PersistMergedChunkResultsAsync` 末尾调用
2. 检查 `document_files` 的 subject/grade/year：
   - 三个字段全有值 → 跳过 LLM
   - LLM 服务未注册 → 跳过（Info 日志）
   - Markdown 为空 → 跳过
3. 提取 Markdown 前 2000 字符，调用 `DocumentAnalysisService.AnalyzeMetadataAsync`
4. LLM 返回 `DocumentMetadataAnalysis` record（字段可能为 null）
5. 仅填充当前为空的字段（不覆盖已有值）
6. 调用 `DocumentFileService.UpdateMetadataAsync` 更新 DB
7. Best-effort：调用 `ISearchIndexService.UpdateDocumentFileMetadataAsync` 刷新 OpenSearch

## LLM 调用详情

### DocumentAnalysisService.AnalyzeMetadataAsync

- **初始化**：`InitializeAsync` 动态获取模型 context length，计算 ChunkSize（cap 2500 chars）
- **输入截断**：取 Markdown 前 2000 字符
- **流式 SSE 调用**：`CallStreamingAsync`，temperature=0.1，system prompt 要求直接返回 JSON
- **响应解析**：提取 markdown 代码块中的 JSON，空字符串和 "null" 字符串字面量视为 null
- **失败处理**：任何异常返回 null，调用方记 Warning 日志

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
// Program.cs — LLM 服务仅在 ApiKey 配置时注册
if (llmSection.Exists() && !string.IsNullOrEmpty(llmSection["ApiKey"]))
{
    builder.Services.AddTransient<IDocumentAnalysisService, DocumentAnalysisService>();
}
// IDocumentFileService 和 ISearchIndexService 始终注册
```
