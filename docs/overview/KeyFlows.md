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

> **链路说明（2026-07 更新）**：上图描述的是 **LLM 拆段链路**（`IngestionWorker`），为历史保留链路。当前 OpenSearch 索引源已切换到 **MinerU 解析链路**：`MinerUFileParseWorker` 解析 `document_files` 完成后，将 `document_parse_blocks` 索引到 OpenSearch（`IndexParseBlocksAsync`）。详见 [OpenSearchBlockIndexing](../modules/OpenSearchBlockIndexing/01-FEATURE.md)。LLM 链路的 `IndexDocumentSegmentsAsync` 保留但新代码不再调用。

---

## 2. 精确搜索 (ExactSearch)

```
  Admin UI / HTTP Client  DocLibrary           PostgreSQL              OpenSearch
    │                    │                       │                      │
    │ GET /admin/documents/search?query=... │                     │                      │
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

**触发条件**：HTTP GET `/admin/documents/search`
**降级**：OpenSearch 不可用时回退到 `document_occurrences` 倒排索引（token_text 匹配）

---
