# 06-CONVENTIONS — Document Parse 命名与风格约定

## 命名约定

| 类型 | 约定 | 示例 |
|------|------|------|
| Worker 类 | 类名 + `Worker` 后缀，继承 `BackgroundService` | `MinerUFileParseWorker` |
| API 客户端 | 服务名 + `Client` 后缀 | `MinerUPrecisionClient` |
| 端点类 | 静态类，PascalCase + `Endpoints` 后缀 | `DocumentParseEndpoints` |
| 端点方法 | 私有静态，PascalCase | `ListDocumentParses`、`DeleteDocumentParse` |
| 转档/分页服务 | 类名 + `Service` 后缀，接口前缀 `I` | `RemoteFileConversionService` / `IFileConversionService`、`PdfSplitService` / `IPdfSplitService` |
| 领域服务 | 接口前缀 `I`，实现类名 + `Service` | `IDocumentParseService` / `DocumentParseService` |
| 配置类 | PascalCase + `Options` 后缀 | `MinerUOptions` |
| record（服务层） | PascalCase | `ImageMetadata`、`MinerUParseResult` |
| 状态常量 | 静态常量类 + `const string` | `DocumentParseStatus.Pending` |
| 模型类 | PascalCase + `Model` 后缀（领域）/ + `Entity`（数据库） | `DocumentParseModel` / `DocumentParseEntity` |

## 文件组织约定

- 小型服务（转档、分页）的接口与实现**合并在一个 `.cs` 文件中**（`PdfSplitService.cs`、`RemoteFileConversionService.cs`、`DocumentParseBlockService.cs`），避免碎片化
- 大型客户端（`MinerUPrecisionClient`）的 record 与配置类**同文件定义**（`MinerUParseResult`、`MinerUOptions`、`ImageMetadata`）
- 端点类为 `static class`，通过 `WebApplication.MapXxxEndpoints` 扩展方法注册

## 日志约定

- 结构化日志占位符：`{PropertyName}` 语法
- 异常对象传入：`LogError(ex, "message {Id}", id)` / `LogWarning(ex, "message {Id}", id)`
- Worker 轮询：启动时 `LogInformation("MinerU file parse worker started")`
- 关键状态变更：`LogInformation("Document parse status updated: {Id}, Status={Status}", ...)`
- MinerU ZIP 诊断：每次下载 ZIP 后输出完整条目列表 `ZIP entries for task {TaskId}: {Entries}`

关键日志消息（代码中实际存在）：
```
"MinerU file parse worker started"
"Non-PDF file detected: {FileName} ({ContentType}), converting to PDF"
"Successfully converted {FileName} to PDF"
"File {FileId} has {PageCount} pages (converted: {IsConverted})"
"Large file detected: {PageCount} pages, splitting into chunks of {MaxPages}"
"Chunk {Index} uploaded to S3: {Path}"
"Chunk {Index} submitted: TaskId={TaskId}"
"MinerU parse completed: ParseId={ParseId}, FileId={FileId}, ..."
"Split parse completed: ParseId={ParseId}, FileId={FileId}, Chunks={ChunkCount}"
"Document parse status updated: {Id}, Status={Status}"
"ZIP entries for task {TaskId}: {Entries}"
```

## 错误处理约定

- **Worker 外层 catch**：单任务异常 → 标记该任务 `status=failed`，记录 `error_message`，继续处理下一个任务
- **Worker 轮询层 catch**：异常记 `LogError`，下一轮继续（不退出 Worker）
- **OSS 操作失败**：best-effort，记 `LogWarning`，不阻塞主流程
- **OpenSearch 索引失败**：best-effort，记 `LogWarning`，不阻塞解析/删除
- **doc-converter 不可用**：转档 HTTP 调用失败/超时返回 `null`，标记 `failed` 并给出明确错误信息
- **MinerU API 失败**：`SubmitUrlAsync` / `PollStatusAsync` 抛 `InvalidOperationException`，Worker 外层捕获标记 `failed`
- **OperationCanceledException**：在 `ExecuteAsync` / `ProcessFileAsync` 中识别停止信号，rethrow 以优雅退出

## 状态常量约定

```csharp
public static class DocumentParseStatus
{
    public const string Pending = "pending";
    public const string Parsing = "parsing";
    public const string Parsed = "parsed";
    public const string Failed = "failed";
}
```

- 状态值使用小写字符串常量，**不使用 enum / int**
- `failed` 为保留历史记录，重试通过 `CreateAsync` 新建记录实现
- `modelVersion` 仅接受 `"vlm"` 或 `"pipeline"`，由 `ParseDocumentFile` 端点校验

## 配置约定

| 配置键 | 说明 | 默认值 |
|--------|------|--------|
| `MinerU:ApiToken` | Bearer Token（免费额度 1000 页/天） | 无（必填） |
| `MinerU:BaseUrl` | API 地址 | `https://mineru.net` |
| `MinerU:ModelVersion` | 默认模型版本 | 无（请求参数默认 `vlm`） |

Section 常量：`MinerUOptions.SectionName = "MinerU"`。

## 数据库约定

- 使用 `DatabaseInitializer`（`CREATE TABLE IF NOT EXISTS` + `ALTER TABLE ADD COLUMN IF NOT EXISTS`），**无 EF Core Migration**
- 表名 / 列名使用 snake_case；C# 属性使用 PascalCase（通过 `[Column("...")]` 映射）
- 外键级联：`document_parses.document_file_id` ON DELETE CASCADE；`document_parse_blocks.parse_id` ON DELETE CASCADE；`document_parse_blocks.image_id` ON DELETE SET NULL
- `document_files.updated_at` 由数据库触发器 `set_document_files_updated_at` 自动维护

## 异步约定

- 异步方法以 `Async` 后缀命名（`CreateAsync`、`SubmitUrlAsync` 等）
- 避免 `async void`
- Worker 使用 `CancellationToken` 传递停止信号（`stoppingToken.ThrowIfCancellationRequested()`）
- `scopeProvider.CreateScope()` 创建作用域解析 Scoped 服务（Worker 为 Singleton）

## 区块解析约定

- `block_data` 保留 MinerU 原始 JSON（兜底，MinerU 产出字段变化无需改 schema）
- 文本提取优先级：`text` → `content` → `body`（覆盖表格 HTML 等场景）
- image 类型 block 通过 `img_path`（相对路径如 `images/xxx.jpg`）提取文件名匹配图片 ID
- 分块合并时图片名添加 `chunk{i}_` 前缀避免跨块冲突
