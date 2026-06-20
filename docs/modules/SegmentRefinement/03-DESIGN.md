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
│         ├── 逐页 LLM refine（失败页新建实体保留原内容）      │
│         ├── Delete old segments + occurrences               │
│         ├── Insert new segments + occurrences               │
│         ├── Update llm_profile_json                         │
│         └── Rebuild search index                            │
│                                                             │
│  RefineDocumentSegments() endpoint                          │
│    └── 保存备份（fire-and-forget，失败仅记日志）             │
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

### 2. document_segments 表移除 block_id 列

```sql
ALTER TABLE document_segments DROP COLUMN IF EXISTS block_id;
```

> SentenceId 格式从 `p{N}-b{M}-s{K}` 简化为 `p{N}-s{K}`，BlockId 已从代码和数据库中移除。

### 3. 新增 document_segment_backups 表

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
RefineSegmentsAsync 内部流程：
  │
  ├─ 1. 从 DB 读取现有 segments，按 PageId 分组
  │     用 segment text 拼回页面文本 → List<(PageNumber, Text)>
  │
  ├─ 2. 调用 LLM 重新分析 → 新 DocumentProfile（RefineProfileAsync）
  │
  ├─ 3. 按容量切块（ChunkByCapacity，与初始拆分共享同一逻辑）
  │
  ├─ 4. 每个 chunk 调用 LLM 重新分段（RefineSegmentTextAsync，带 corrections）
  │     MapOffsetToPage 回映射到页码
  │     LLM 失败的页 → 创建新实体复制原有 segment 内容（不回滚）
  │
  ├─ 5. 删除旧 segments + occurrences
  │
  ├─ 6. 写入新 segments + occurrences
  │
  ├─ 7. 更新文档 llm_profile_json → SaveChangesAsync
  │
  └─ 8. 重建搜索索引（失败仅记日志）
  │
  ▼
Endpoint 保存备份（fire-and-forget，失败仅记日志）
  │
  ▼
返回结果
```

> **注意**：
> - Refine 流程与初始拆分共享 `ChunkByCapacity` 和 `MapOffsetToPage` 逻辑
> - LLM 失败时创建新实体保留原内容，而非回滚到备份
> - 备份在 endpoint 中 fire-and-forget 保存，失败不影响 refine 结果

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
| 备份保存失败 | 无（fire-and-forget，仅记日志） | 无 |
