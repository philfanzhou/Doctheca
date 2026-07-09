# KeyFlows — 关键业务流程

## 1. 文件上传 → MinerU 解析 → 可搜索

```
  Admin UI          DocLibrary           OSS              MinerUFileParseWorker      OpenSearch
    │                    │                   │                 │                      │
    │ POST /admin/document-files/upload      │                 │                      │
    │───────────────────►│                   │                 │                      │
    │                    │ PutObject()       │                 │                      │
    │                    │──────────────────►│                 │                      │
    │                    │ 文件上传到 OSS    │                 │                      │
    │                    │                   │                 │                      │
    │                    │ 写入 document_files (status=uploaded)                      │
    │   201 + file_id    │                   │                 │                      │
    │◄───────────────────│                   │                 │                      │
    │                    │                   │                 │                      │
    │ POST /admin/document-files/{id}/parse  │                 │                      │
    │───────────────────►│                   │                 │                      │
    │                    │ 写入 document_parses (status=pending)                     │
    │                    │                   │                 │                      │
    │                    │                   │    轮询 pending 任务                   │
    │                    │                   │◄────────────────│                      │
    │                    │  生成 presigned URL                  │                      │
    │                    │  提交 MinerU API → 获取 task_id      │                      │
    │                    │  更新 status=parsing, external_task_id                    │
    │                    │                   │                 │                      │
    │                    │  轮询 MinerU 状态                    │                      │
    │                    │  完成后下载 ZIP → 提取 MD + 图片     │                      │
    │                    │  图片上传 OSS → 替换 MD 路径         │                      │
    │                    │  写入 document_parse_blocks / document_parse_images       │
    │                    │  更新 status=parsed                  │                      │
    │                    │                   │                 │                      │
    │                    │                   │  IndexParseBlocksAsync()               │
    │                    │                   │─────────────────►                      │
    │                    │                   │                 │                      │
```

**触发条件**：用户通过 Admin UI 上传文件并触发 MinerU 解析
**参与服务**：Admin UI → DocLibrary → OSS → PostgreSQL；MinerUFileParseWorker → MinerU Precision API + OpenSearch
**数据流转**：OSS 文件 → MinerU 解析 → 结构化 blocks/images 写入 3 张表 → 搜索引擎索引

---

## 2. 精确搜索 (ExactSearch)

```
  Admin UI / HTTP Client  DocLibrary           OpenSearch
    │                    │                       │
    │ GET /admin/documents/search?query=... │                       │
    │───────────────────►│                       │
    │                    │ SearchDocumentAsync(query, filter) │
    │                    │──────────────────────────────────────────────►
    │                    │    results            │
    │                    │◄──────────────────────│
    │                    │                       │
    │    results         │  (OpenSearch 不可用时返回空结果并记 LogWarning) │
    │◄───────────────────│                       │
```

**触发条件**：HTTP GET `/admin/documents/search`
**降级**：OpenSearch 不可用时返回空结果并记录 LogWarning

---
