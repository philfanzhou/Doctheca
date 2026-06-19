# 03-DESIGN — SegmentRefinement 技术设计

## 架构概览

```
┌─────────────────────────────────────────────────────────────┐
│  Admin Frontend (Vue 3 + Element Plus)                      │
│  DocumentSegmentView.vue                                    │
│    ├── GET /admin/documents/{id}/segments                   │
│    └── POST /admin/documents/{id}/refine                    │
└──────────────────────────┬──────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────┐
│  DocRetrieval Service (.NET 8)                              │
│  DocumentAdminEndpoints.cs                                  │
│    ├── GetDocumentSegments() → IDocumentDomainService       │
│    └── RefineDocumentSegments() → IDocumentDomainService    │
│                                                             │
│  IDocumentDomainService                                     │
│    ├── GetSegmentsAsync(documentId)                         │
│    └── RefineSegmentsAsync(documentId, corrections)         │
│         ├── BackupSegmentsAsync()                           │
│         ├── BuildRefinementPrompt()                         │
│         ├── ILlmSegmentationService.RefineWithExamplesAsync │
│         ├── DeleteOldSegmentsAsync()                        │
│         ├── InsertNewSegmentsAsync()                        │
│         └── ReindexAsync()                                  │
└──────────────────────────┬──────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────┐
│  Database (PostgreSQL)                                      │
│    ├── documents.llm_profile_json (新增)                     │
│    ├── document_segments (读/写/删)                          │
│    ├── document_occurrences (删/写)                          │
│    └── document_segment_backups (新增，写)                    │
└─────────────────────────────────────────────────────────────┘
```

## 数据库变更

### 1. documents 表新增字段

```sql
ALTER TABLE documents ADD COLUMN llm_profile_json text NULL;
```

### 2. 新增 document_segment_backups 表

```sql
CREATE TABLE IF NOT EXISTS document_segment_backups (
    id uuid NOT NULL,
    document_id uuid NOT NULL,
    backup_data text NOT NULL,
    correction_count integer NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT PK_document_segment_backups PRIMARY KEY (id),
    CONSTRAINT FK_backups_document FOREIGN KEY (document_id)
        REFERENCES documents(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_backups_document_id
    ON document_segment_backups (document_id);
```

## 接口签名

### ILlmSegmentationService 新增方法

```csharp
public interface ILlmSegmentationService
{
    // 现有方法
    Task<DocumentProfile> AnalyzeDocumentAsync(string textPreview, CancellationToken ct = default);
    Task<List<SegmentResult>> SegmentTextAsync(string text, DocumentProfile profile, CancellationToken ct = default);

    // 新增：基于用户修正的 few-shot refinement
    Task<DocumentProfile> RefineProfileAsync(
        string textPreview,
        DocumentProfile originalProfile,
        List<SegmentCorrection> corrections,
        CancellationToken ct = default);

    Task<List<SegmentResult>> RefineSegmentTextAsync(
        string text,
        DocumentProfile profile,
        List<SegmentCorrection> corrections,
        CancellationToken ct = default);
}
```

### IDocumentDomainService 新增方法

```csharp
public interface IDocumentDomainService
{
    // 现有方法...

    // 新增
    Task<DocumentSegmentsDto> GetSegmentsAsync(Guid documentId, CancellationToken ct = default);
    Task<RefinementResult> RefineSegmentsAsync(
        Guid documentId,
        List<SegmentCorrectionDto> corrections,
        CancellationToken ct = default);
}
```

### 新增 Domain Models

```csharp
// 用户提交的修正
public record SegmentCorrection
{
    public List<string> OriginalSentenceIds { get; init; }  // 原始 segment IDs
    public string Action { get; init; }                      // merge|split|retype
    public string? NewText { get; init; }                    // 合并/拆分后的新文本
    public int? SplitPosition { get; init; }                 // 拆分位置（字符偏移）
    public string? NewSegmentType { get; init; }             // 新的 segment 类型
}

// API 返回的 segments 数据
public record DocumentSegmentsDto
{
    public Guid DocumentId { get; init; }
    public string Title { get; init; }
    public string Status { get; init; }
    public DocumentProfile? Profile { get; init; }
    public List<SegmentDto> Segments { get; init; }
    public int TotalCount { get; init; }
}

public record SegmentDto
{
    public Guid Id { get; init; }
    public string SentenceId { get; init; }
    public string SegmentType { get; init; }
    public string Text { get; init; }
    public int StartOffset { get; init; }
    public int EndOffset { get; init; }
    public int PageNumber { get; init; }
}

// 修正结果
public record RefinementResult
{
    public Guid DocumentId { get; init; }
    public Guid BackupId { get; init; }
    public int CorrectionCount { get; init; }
    public string Message { get; init; }
}
```

## 核心流程

### 修正提交流程

```
用户提交修正 (POST /admin/documents/{id}/refine)
  │
  ▼
验证文档状态 (必须为 ready)
  │
  ▼
获取原始文本 (从 OSS 重新下载)
  │
  ▼
备份当前 segments → document_segment_backups
  │
  ▼
构造 few-shot prompt (修正 examples + 原始文本)
  │
  ▼
调用 LLM 重新分析 → 新 DocumentProfile
  │
  ▼
调用 LLM 重新分段 → 新 segments
  │
  ▼
删除旧 segments + occurrences
  │
  ▼
写入新 segments + occurrences
  │
  ▼
重建搜索索引
  │
  ▼
更新文档 llm_profile_json
  │
  ▼
返回结果
```

### 回滚流程

```
重新拆分过程中发生异常
  │
  ▼
从 document_segment_backups 读取最近备份
  │
  ▼
删除新写入的 segments + occurrences
  │
  ▼
从备份恢复 segments + occurrences
  │
  ▼
恢复搜索索引
  │
  ▼
记录错误日志
```

## 前端设计

### 路由

```
/documents/:id/segments → DocumentSegmentView.vue
```

### API Client

```typescript
// services/docRetrievalApi.ts
class DocRetrievalApiClient {
  async getDocumentSegments(documentId: string): Promise<DocumentSegmentsResponse>
  async refineDocumentSegments(documentId: string, corrections: CorrectionDto[]): Promise<RefinementResponse>
}
```

### 组件结构

```
DocumentSegmentView.vue
  ├── LLM 分析结果展示 (el-descriptions)
  ├── Segment 列表 (el-table + checkbox)
  │   ├── 合并操作
  │   ├── 拆分操作 (el-dialog)
  │   └── 类型修改 (el-select)
  ├── 操作工具栏
  └── 提交确认 (el-dialog)
```

## 错误处理

| 错误 | HTTP 状态码 | 错误码 |
|------|------------|--------|
| 文档不存在 | 404 | DOCRETRIEVAL_DOCUMENT_NOT_FOUND |
| 文档未就绪 | 422 | DOCRETRIEVAL_DOCUMENT_NOT_READY |
| 修正数量超限 | 400 | DOCRETRIEVAL_TOO_MANY_CORRECTIONS |
| LLM 调用失败 | 500 | DOCRETRIEVAL_LLM_REFINE_FAILED |
| 回滚失败 | 500 | DOCRETRIEVAL_ROLLBACK_FAILED |
