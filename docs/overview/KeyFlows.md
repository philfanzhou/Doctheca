# KeyFlows — 关键业务流程

## 1. 文档上传 → 解析 → 可搜索

```
  Admin UI          DocLibrary           OSS              Worker             OpenSearch
    │                    │                   │                 │                      │
    │ POST /upload       │                   │                 │                      │
    │───────────────────►│                   │                 │                      │
    │                    │ PutObject()       │                 │                      │
    │                    │──────────────────►│                 │                      │
    │                    │ 文件上传到 OSS    │                 │                      │
    │                    │                   │                 │                      │
    │                    │ 写入 documents 表 (status=pending)  │                      │
    │                    │ 写入 ingestion_jobs (status=pending)│                      │
    │   201 + doc_id     │                   │                 │                      │
    │◄───────────────────│                   │                 │                      │
    │                    │                   │                 │                      │
    │                    │                   │    5s poll      │                      │
    │                    │                   │◄────────────────│                      │
    │                    │                   │  GetPendingJobs │                      │
    │                    │                   │─────────────────►                      │
    │                    │                   │                 │                      │
    │                    │  StartJob (status=processing)        │                      │
    │                    │                   │                 │                      │
    │                    │  Download(file_path)                │                      │
    │                    │──────────────────►│                 │                      │
    │                    │    file stream    │                 │                      │
    │                    │◄──────────────────│                 │                      │
    │                    │                   │                 │                      │
    │                    │  Parse(Document)  │                 │                      │
    │                    │  → pages, segments, questions, tokens                     │
    │                    │                   │                 │                      │
    │                    │  写入 document_pages, segments, questions, occurrences    │
    │                    │                   │                 │                      │
    │                    │  UpdateDocumentStatus(ready)         │                      │
    │                    │  CompleteJob(success)                │                      │
    │                    │                   │                 │                      │
    │                    │                   │  IndexSegments() │                      │
    │                    │                   │─────────────────►                      │
    │                    │                   │                 │                      │
```

**触发条件**：用户通过 Admin UI 上传 PDF/DOCX 文件
**参与服务**：Admin UI → DocLibrary → OSS → PostgreSQL；Worker → OpenSearch
**数据流转**：OSS 文件 → 解析器 → 结构化数据写入 6 张表 → 搜索引擎索引

---

## 2. 精确搜索 (ExactSearch)

```
  Client             DocLibrary           PostgreSQL              OpenSearch
    │                    │                       │                      │
    │ ExactSearch(query) │                       │                      │
    │───────────────────►│                       │                      │
    │                    │ OpenSearch 是否可用?   │                      │
    │                    │──────────────────────────────────────────────►
    │                    │                      ✓│                      │
    │                    │◄──────────────────────────────────────────────
    │                    │                       │                      │
    │                    │ SearchDocumentAsync(query, filter)            │
    │                    │──────────────────────────────────────────────►
    │                    │    results            │                      │
    │                    │◄──────────────────────│                      │
    │                    │                       │                      │
    │    results         │  (失败则回退到 PostgreSQL 倒排索引)            │
    │◄───────────────────│                       │                      │
```

**触发条件**：gRPC 调用 `ExactSearch`
**降级**：OpenSearch 不可用时回退到 `document_occurrences` 倒排索引（token_text 匹配）

---
