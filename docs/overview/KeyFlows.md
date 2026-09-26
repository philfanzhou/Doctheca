# KeyFlows — 关键业务流程

## 0. Identity 管理员登录与 Cookie 会话

```text
Admin Browser        Doctheca                 Identity
     │ POST /admin/auth/login                       │
     ├────────────────►│                            │
     │                 │ POST /api/auth/token       │
     │                 ├───────────────────────────►│
     │                 │ access + refresh token     │
     │                 │◄───────────────────────────┤
     │                 │ OIDC/JWKS 验证 + role=admin│
     │ HttpOnly Cookie │                            │
     │◄────────────────┤                            │
     │ GET/POST /admin/*（Cookie 自动携带）          │
     ├────────────────►│                            │
```

Access Token 过期时，浏览器调用 `/admin/auth/refresh`；Doctheca 使用 HttpOnly Refresh Cookie向 Identity 换取并验证新 Token 对。退出时 best-effort 撤销 Refresh Token并清理 Cookie。

---

## 1. 文件上传 → StructaDoc 解析 → 可搜索

```
  Admin UI          Doctheca            StructaDoc           StructaDocParseWorker    OpenSearch
    │                    │                    │                       │                   │
    │ POST /admin/document-files/upload       │                       │                   │
    │───────────────────►│                    │                       │                   │
    │                    │ POST /api/v1/documents (multipart "file")   │                   │
    │                    │───────────────────►│                       │                   │
    │                    │ 201 documentId     │                       │                   │
    │                    │◄───────────────────│                       │                   │
    │                    │ 写入 document_files (structadoc_document_id)│                   │
    │   200 + file_id    │                    │                       │                   │
    │◄───────────────────│                    │                       │                   │
    │                    │                    │                       │                   │
    │ POST /admin/document-files/{id}/parse   │                       │                   │
    │───────────────────►│                    │                       │                   │
    │                    │ 写入 document_parses (status=pending)       │                   │
    │                    │                    │   轮询 pending/parsing 任务（5 秒）        │
    │                    │                    │◄──────────────────────│                   │
    │                    │                    │ POST parse-runs       │                   │
    │                    │                    │  (Idempotency-Key=parseId)                │
    │                    │                    │ 201 parseRunId → status=parsing            │
    │                    │                    │ （内部执行 MinerU 解析、Office 转档、大 PDF 分块）
    │                    │                    │ GET parse-runs/{id} 轮询至终态             │
    │                    │                    │◄──────────────────────│                   │
    │                    │                    │ succeeded → 拉取 Blocks/Markdown/Assets    │
    │                    │                    │ 写入 document_parse_blocks / _images       │
    │                    │                    │ 更新 status=parsed    │                   │
    │                    │                    │                       │ IndexParseBlocksAsync()
    │                    │                    │                       │──────────────────►│
```

**触发条件**：用户通过 Admin UI 上传文件并触发解析
**参与服务**：Admin UI → Doctheca → StructaDoc（原件与解析产物主责）→ PostgreSQL；StructaDocParseWorker → OpenSearch
**数据流转**：文件字节直接转发 StructaDoc → Parse Run 异步解析 → Blocks/Markdown/Assets 同步进本地 3 张表 → 搜索引擎索引
**存量兼容**：迁移前上传的文件（仅有 OSS 路径）在首次触发解析时由 Worker 惰性上传到 StructaDoc 并回填引用；迁移前的解析记录保持只读

---

## 2. 精确搜索 (ExactSearch)

```
  Admin UI / HTTP Client  Doctheca           OpenSearch
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
