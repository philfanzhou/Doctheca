# MinerUAgentParsing — 命名与风格约定

## 命名约定

| 类型 | 约定 | 示例 |
|------|------|------|
| 端点类 | 静态类，PascalCase + Endpoints 后缀 | `DocumentFileEndpoints` |
| 端点方法 | 私有静态，PascalCase | `UploadDocumentFile` |
| Worker | 类名 + Worker 后缀 | `MinerUFileParseWorker` |
| 私有方法 | camelCase 或 PascalCase | `PersistParseResultAsync` |
| Record（服务层） | PascalCase | `ImageMetadata` |
| 配置类 | PascalCase + Options 后缀 | `MinerUOptions` |

## 日志约定

- 结构化日志占位符：`{PropertyName}` 语法
- 异常对象传入：`LogError(ex, "message {Id}", id)`
- Worker 轮询：`LogInformation` 每次轮询间隔
- 解析状态变更：`LogInformation("Document parse status updated: {Id}, Status={Status}", ...)`

关键日志消息：
```
"MinerU file parse worker started"
"File {FileId} has {PageCount} pages (converted: {IsConverted})"
"Large file detected: {PageCount} pages, splitting into chunks of {MaxPages}"
"Chunk {Index} submitted: TaskId={TaskId}"
"MinerU parse completed: ParseId={ParseId}, FileId={FileId}"
"Split parse completed: ParseId={ParseId}, FileId={FileId}, Chunks={ChunkCount}"
```

## 错误处理约定

- Worker 外层 catch：标记 status=failed，记录 errorMessage
- OSS 操作失败：标记 failed，不阻塞（best-effort）
- OpenSearch 索引失败：记录 Warning，不阻塞解析
- LibreOffice 不可用：标记 failed，返回明确错误信息

## 配置约定

| 配置节 | 说明 |
|--------|------|
| `MinerU:ApiToken` | Bearer Token |
| `MinerU:BaseUrl` | API 地址（默认 https://mineru.net） |
| `MinerU:ModelVersion` | vlm 或 pipeline |
