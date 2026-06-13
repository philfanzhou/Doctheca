# 验证指南

如何验证 `ruoyu.docretrieval` 正常运行。

## 健康检查

```bash
curl http://localhost:5012/health
```

预期响应：

```json
{ "status": "healthy", "timestamp": "..." }
```

## 上传测试文档

```bash
curl -X POST http://localhost:5012/admin/documents/upload \
  -F "file=@test.pdf" \
  -F "title=Test Document" \
  -F "subject=English" \
  -F "grade=G10" \
  -F "year=2024"
```

必填表单字段：`file`、`title`、`subject`、`grade`、`year`。可选：`tags`。

支持的文件类型：PDF、Word（.doc/.docx）、PowerPoint（.ppt/.pptx）。最大大小：200 MB。

预期响应：

```json
{
  "success": true,
  "data": {
    "document_id": "...",
    "title": "Test Document",
    "job_id": "...",
    "status": "pending"
  }
}
```

## 文档列表

```bash
curl http://localhost:5012/admin/documents
```

支持的查询参数：`page`、`pageSize`、`status`、`subject`、`grade`、`keyword`、`year`。

带筛选条件的示例：

```bash
curl "http://localhost:5012/admin/documents?subject=English&grade=G10&page=1&pageSize=10"
```

## 检查文档状态

将 `{id}` 替换为上传时返回的 `document_id`：

```bash
curl http://localhost:5012/admin/documents/{id}/status
```

预期响应：

```json
{
  "success": true,
  "data": {
    "document_id": "...",
    "title": "Test Document",
    "status": "ready",
    "jobs": [
      {
        "job_id": "...",
        "status": "success",
        "parser_version": "...",
        "ocr_version": "...",
        "error_message": null,
        "started_at": "...",
        "finished_at": "..."
      }
    ]
  }
}
```

文档状态流转：`pending` → `processing` → `ready`（或 `failed`）。

任务状态流转：`pending` → `processing` → `success`（或 `failed`）。

## 运行测试

在 `backend/ruoyu.docretrieval/` 目录下执行：

```bash
dotnet test test/Ruoyu.Study.DocRetrieval.Tests
```

按功能模块筛选：

```bash
dotnet test --filter "FullyQualifiedName~DocumentUpload"
dotnet test --filter "FullyQualifiedName~ExactSearch"
dotnet test --filter "FullyQualifiedName~DocumentDeletion"
```

## 搜索测试端点

需要 OpenSearch（或数据库回退）可用：

```bash
curl "http://localhost:5012/admin/documents/search-test?query=algebra&pageSize=5"
```

查询参数：`query`（必填）、`phrase`（布尔值，默认 false）、`pageSize`（1–100，默认 20）、`pageToken`（可选游标）。
