# KeyFlows — 关键业务流程

## 0. Identity 管理员登录与 Cookie 会话

```text
Admin Browser        DocLibrary                 Identity
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

Access Token 过期时，浏览器调用 `/admin/auth/refresh`；DocLibrary 使用 HttpOnly Refresh Cookie向 Identity 换取并验证新 Token 对。退出时 best-effort 撤销 Refresh Token并清理 Cookie。

---

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

## 3. QuestionBank 只读拉取

```text
QuestionBank                    DocLibrary
     │ GET /internal/question-bank/document-parses
     ├─────────────────────────►│ 仅返回 parsed parse
     │ GET .../{parseId}/blocks │
     ├─────────────────────────►│
     │ GET .../images/{imageId} │
     ├─────────────────────────►│
     │                          │
     │ 自身事务：拆题入库 + source parseId 唯一记录
```

三个请求都要求服务密钥。DocLibrary 不接收导入状态写回，同一 parse 可重复查询；QuestionBank 在自己的数据库中保证幂等。
