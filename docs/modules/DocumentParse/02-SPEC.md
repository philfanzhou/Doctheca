# 02-SPEC — Document Parse 详细规格

## 1. 解析状态机

```
触发解析 ──▶ pending ──Worker 拉取──▶ parsing ──成功──▶ parsed
                                            │
                                            │ 失败（API 失败 / 超时 / 转档失败）
                                            ▼
                                          failed（保留记录，可新建重试）
```

状态常量定义于 `DocumentParseStatus` (`Domain/Models/DocumentParseStatus.cs`)：

| 常量 | 值 | 说明 |
|------|-----|------|
| `Pending` | `"pending"` | 等待 Worker 拉取 |
| `Parsing` | `"parsing"` | Worker 正在处理 |
| `Parsed` | `"parsed"` | 解析完成 |
| `Failed` | `"failed"` | 解析失败 |

`ParsedAt` 在 `UpdateStatusAsync` 入参 `status == Parsed` 时由服务自动设置为 `DateTimeOffset.UtcNow`（`DocumentParseService.cs:76`）。`Failed` 状态保留历史记录，重试通过新建解析记录实现（`CreateAsync` 不删除旧记录）。

同文件同 `modelVersion` 仅允许一条 `pending` / `parsing` 记录，由 `GetLatestByFileIdAndModelAsync` 校验。

## 2. HTTP 端点

### 2.1 解析记录列表

```
GET /admin/document-parses?page=1&pageSize=20&search=
```

定义于 `DocumentParseEndpoints.ListDocumentParses` (`Service/Endpoints/DocumentParseEndpoints.cs:26-60`)。

- 参数：`page`（默认 1）、`pageSize`（默认 20）、`search`（可选，按文件名模糊匹配）
- 排序：`ParsedAt DESC`，然后 `Id DESC`（`DocumentParseRepository.GetListAsync`）
- 响应：`{ success, data: [...], total, page, pageSize, totalPages }`
- `data` 每条：`{ id, fileId, fileName, modelVersion, status, parsedAt, errorMessage }`
- `fileName` 来自关联的 `document_files.file_name`（通过 `IDocumentFileService.GetByIdAsync` 获取）；无关联文件时显示 `"Unknown"`

### 2.2 删除解析记录

```
DELETE /admin/document-parses/{parseId}
```

定义于 `DocumentParseEndpoints.DeleteDocumentParse` (`Service/Endpoints/DocumentParseEndpoints.cs:62-108`)。

执行顺序：
1. 查询解析记录 → 不存在返回 404 `DOCLIBRARY_PARSE_NOT_FOUND`
2. 查询关联图片 → 逐张调用 `ossService.DeleteAsync`（失败仅记 Warning，不阻塞）
3. 调用 `parseService.DeleteParseAsync(parseId)` → 级联删除 DB 中的 images 与 parse 记录
4. 调用 `searchIndexService.DeleteParseIndexAsync(parseId)` 删除 OpenSearch 索引（best-effort，失败仅记 Warning）
5. 返回 `{ success, data: { id, deleted: true } }`

原始文档文件（`document_files`）不受影响。

### 2.3 触发解析（属于本模块流程，但端点定义在 DocumentFileEndpoints）

```
POST /admin/document-files/{id}/parse?modelVersion=vlm
```

定义于 `DocumentFileEndpoints.ParseDocumentFile` (`Service/Endpoints/DocumentFileEndpoints.cs:235-269`)。校验链：
1. `modelVersion` 合法性 → 非法返回 400 `DOCLIBRARY_INVALID_MODEL_VERSION`
2. 文件存在性 → 不存在返回 404 `DOCLIBRARY_FILE_NOT_FOUND`
3. 同 modelVersion 进行中解析 → 存在返回 422 `DOCLIBRARY_PARSE_IN_PROGRESS`
4. MinerU Token 配置 → 未配置返回 503 `DOCLIBRARY_MINERU_NOT_CONFIGURED`
5. 调用 `parseService.CreateAsync(id, modelVersion)` → 返回 `{ id, parseId, status, modelVersion }`

## 3. MinerUFileParseWorker 流程

定义于 `Service/MinerUFileParseWorker.cs`，继承 `BackgroundService`。

### 3.1 轮询主循环 (`ExecuteAsync`)

- 轮询间隔 `_pollInterval = 5s`
- 每次轮询：从 scope 解析 `IDocumentFileService`、`IDocumentParseService`、`MinerUPrecisionClient`、`IOssService`、`IPdfSplitService`、`IFileConversionService`
- 调用 `parseService.GetPendingJobsAsync()` 获取所有 `pending` 任务
- 逐个调用 `ProcessFileAsync`；单个任务异常捕获并标记 `failed`，不影响后续任务

### 3.2 单任务处理 (`ProcessFileAsync`)

1. `UpdateStatusAsync(parse.Id, Parsing)` → 状态变为 `parsing`
2. `fileService.GetByIdAsync` → 文件不存在抛异常（外层捕获标记 failed）
3. `ossService.DownloadAsync(file.FilePath)` → 下载源文件
4. **非 PDF 转档**：`ContentType` 非 `application/pdf` 且文件名不以 `.pdf` 结尾 → 调用 `IFileConversionService.ConvertToPdfAsync`
   - 返回 `null`（doc-converter 不可用 / 超时 / HTTP 错误 / 非 2xx）→ 标记 failed，错误信息提示转档失败
5. **页数检测**：`pdfSplitService.GetPageCount(pdfStream)`
6. **分支**：
   - `pageCount <= MaxPagesPerChunk (200)` → `ProcessSingleFileAsync`
   - `pageCount > 200` → `ProcessSplitFileAsync`

### 3.3 普通文件解析 (`ProcessSingleFileAsync`)

1. `ossService.GetPresignedUrlAsync(file.FilePath, 3600)` → 生成 presigned URL
2. `dataId = parse.DocumentFileId.ToString("N")[..16]`（取 UUID 前 16 位）
3. `minerUClient.SubmitUrlAsync(presignedUrl, dataId, modelVersion, ct)` → 获取 `taskId`
4. `UpdateStatusAsync(parse.Id, Parsing, externalTaskId: taskId)` → 持久化 task ID
5. `PollAndDownloadAsync` → 轮询 + 下载 ZIP
6. `PersistParseResultAsync` → 持久化结果

### 3.4 持久化单文件结果 (`PersistParseResultAsync`)

1. 上传完整 ZIP 到 OSS（路径 `mineru/{parse.DocumentFileId}/mineru-output.zip`）→ best-effort
2. 逐张图片写入 `document_parse_images`（通过 `parseService.AddImageAsync`），构建 `imageNameToId` 映射
3. 解析 `content_list.json` 并通过 `blockService.InsertBlocksFromContentListAsync` 写入 blocks
4. `parseService.UpdateStatusAsync(Parsed, markdownContent, contentList, contentListV2, modelJson, layoutJson, zipPath)` → 状态变为 `parsed`
5. **best-effort 后置**：
   - `IndexBlocksToSearchAsync` → 索引 OpenSearch（失败记 Warning）
   - `AnalyzeMetadataIfMissingAsync` → LLM 分析元数据（失败记 Warning）

### 3.5 大文件分块解析 (`ProcessSplitFileAsync`)

1. `pdfSplitService.SplitPdf(sourceStream, MaxPagesPerChunk)` → 拆分 PDF
2. 每个 chunk 上传到 OSS（临时路径 `mineru/splits/{parse.Id}/chunk_{chunkIndex}.pdf`）
3. 逐个 chunk 提交：`dataId = DocumentFileId前16位 + "_chunk{i}"` → `SubmitUrlAsync` → `PollAndDownloadAsync`
4. 任一 chunk 失败 → 收集错误信息；全部失败则标记 `failed`；部分成功则保存合并结果但仍标记 `failed`
5. `PersistMergedChunkResultsAsync` 合并并持久化
6. `finally`：清理 OSS 上的临时 chunk 文件 + 释放 chunk 流

### 3.6 合并结果持久化 (`PersistMergedChunkResultsAsync`)

- Markdown 拼接：`string.Join("\n\n---\n\n", chunkResults.Select(r => r.Markdown))`
- content_list 合并：`MergeContentListArrays`（遍历各 chunk JSON 数组，合并为单个数组）
- 图片名添加 `chunk{i}_` 前缀避免跨 chunk 冲突
- ZIP 取第一个 chunk 的作为规范备份
- `layout_json` / `model.json` / `content_list_v2` 取第一个非空 chunk 的值
- 仅当最终 `status == Parsed` 时执行 OpenSearch 索引与 LLM 元数据分析

### 3.7 轮询与下载 (`PollAndDownloadAsync`)

- 超时 `_mineruTimeout = 30 分钟`
- 轮询间隔 `_mineruPollInterval = 5s`
- 调用 `minerUClient.PollStatusAsync(taskId, ct)` 直到 `state == "done"`
- `state == "failed"` → 抛异常（外层捕获标记 failed）
- 超时 → 抛 `"MinerU parse timed out"`
- 完成后调用 `DownloadAndProcessZipAsync(fullZipUrl, taskId, ossService, ct)`

## 4. MinerU Precision 客户端规格

定义于 `Service/MinerUPrecisionClient.cs`，注册为 Singleton。

### 4.1 SubmitUrlAsync

- 前置条件：`MinerUOptions.ApiToken` 非空，否则抛 `InvalidOperationException`
- 请求：`POST /api/v4/extract/task`，Body `{ url, model_version, is_ocr: true, enable_formula: true, enable_table: true, data_id? }`
- 成功响应：`code == 0`，返回 `data.task_id`
- 失败响应：记录错误体并抛 `InvalidOperationException`

### 4.2 PollStatusAsync

- 请求：`GET /api/v4/extract/task/{taskId}`
- 返回：`(state, fullZipUrl, errMsg)`
- `code != 0` → 抛异常

### 4.3 DownloadAndProcessZipAsync

- 独立 `HttpClient`（超时 5 分钟）下载 ZIP
- ZIP 条目诊断日志：`ZIP entries for task {TaskId}: {Entries}`
- 提取规则（优先级匹配）：
  - `full.md`：精确匹配根目录 → 不存在抛异常
  - `content_list.json`：精确匹配 → 后缀 `_content_list.json` → 子目录 `/content_list.json` → 默认 `"[]"`
  - `content_list_v2.json`：同上优先级 → 默认 `null`
  - `model.json`：后缀 `_model.json` → 精确匹配 `model.json` → 默认 `null`
  - `layout.json`：精确匹配 → 后缀 `_layout.json` → 子目录 `/layout.json` → 默认 `null`
  - 图片：所有以 `images/` 开头且 `Length > 0` 的条目
- 图片处理：上传 OSS（路径 `mineru/{taskId}/{imageName}`），Markdown 中 `images/{name}` 替换为完整 OSS 路径
- 返回 `MinerUParseResult` record

## 5. 转档服务规格

定义于 `Service/RemoteFileConversionService.cs`，接口 `IFileConversionService`、配置类 `FileConversionOptions` 同文件。

- `IsAvailable`：恒为 `true`（HTTP 服务的可用性在首次调用时惰性检测，不再构造时检测本地进程）
- `ConvertToPdfAsync`：
  - 以 `multipart/form-data` 方式 POST 文件到 doc-converter 的 `/convert` 端点
  - HTTP 超时由 `FileConversionOptions.TimeoutSeconds` 控制（默认 120 秒，略大于 doc-converter 内部 60 秒转换超时）
  - `TaskCanceledException`（内含 `TimeoutException`）→ 记 LogError，返回 `null`
  - `HttpRequestException` → 记 LogError，返回 `null`
  - 非 2xx 状态码 → 记 LogError，返回 `null`
  - 成功 → 读取响应体为 `MemoryStream` 返回
- 配置：`FileConversion:Url`（默认 `http://doc-converter:5050`）、`FileConversion:TimeoutSeconds`（默认 120）

## 6. PDF 分页服务规格

定义于 `Service/PdfSplitService.cs`，接口 `IPdfSplitService` 同文件。基于 **PdfSharpCore**。

- `GetPageCount(Stream)`：`PdfReader.Open(stream, PdfDocumentOpenMode.Import)` → `doc.PageCount`
- `SplitPdf(Stream, maxPagesPerChunk = 200)`：`Import` 模式打开 → 按 `startPage` 步进，每个 chunk 新建 `PdfDocument` 并逐页 `AddPage` → 返回 `List<(chunkIndex, chunkStream)>`

## 7. 接口变更（C# 签名）

### 7.1 IDocumentParseService

```csharp
Task<DocumentParseModel> CreateAsync(Guid documentFileId, string modelVersion = "vlm");
Task<DocumentParseModel?> GetByIdAsync(Guid id);
Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
Task<DocumentParseModel?> GetLatestByFileIdAndModelAsync(Guid documentFileId, string modelVersion);
Task<DocumentParseModel> UpdateStatusAsync(Guid id, string status,
    string? errorMessage = null, string? markdownContent = null,
    string? externalTaskId = null, string? contentList = null,
    string? contentListV2 = null, string? modelJson = null,
    string? layoutJson = null, string? zipPath = null);
Task<List<DocumentParseModel>> GetPendingJobsAsync();
Task AddImageAsync(DocumentParseImageModel image);
Task<List<DocumentParseImageModel>> GetImagesByParseIdAsync(Guid parseId);
Task<List<DocumentParseImageModel>> GetImagesByFileIdAsync(Guid documentFileId);
Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
Task<bool> DeleteParseAsync(Guid parseId);
Task<List<DocumentParseModel>> GetByFileIdAsync(Guid documentFileId);
```

### 7.2 IDocumentParseBlockService

```csharp
Task InsertBlocksFromContentListAsync(Guid parseId, string contentListJson,
    IDictionary<string, Guid>? imageNameToId = null);
```

### 7.3 IDocumentParseRepository

```csharp
Task<DocumentParseModel> AddAsync(DocumentParseModel model);
Task<DocumentParseModel?> GetByIdAsync(Guid id);
Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
Task<DocumentParseModel?> GetLatestByFileIdAndModelAsync(Guid documentFileId, string modelVersion);
Task<List<DocumentParseModel>> GetByFileIdAsync(Guid documentFileId);
Task<DocumentParseModel> UpdateAsync(DocumentParseModel model);
Task<List<DocumentParseModel>> GetByStatusAsync(string status);
Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
Task DeleteAsync(Guid id);
```

### 7.4 IDocumentParseBlockRepository

```csharp
Task AddBlocksAsync(Guid parseId, IEnumerable<DocumentParseBlockModel> blocks);
Task<List<DocumentParseBlockModel>> GetByParseIdAsync(Guid parseId);
Task<List<DocumentParseBlockModel>> GetByParseAndPageAsync(Guid parseId, int pageId);
```

### 7.5 MinerUPrecisionClient

```csharp
Task<string> SubmitUrlAsync(string fileUrl, string? dataId = null,
    string? modelVersion = null, CancellationToken ct = default);
Task<(string State, string? FullZipUrl, string? ErrMsg)> PollStatusAsync(
    string taskId, CancellationToken ct = default);
Task<MinerUParseResult> DownloadAndProcessZipAsync(string zipUrl, string taskId,
    IOssService ossService, CancellationToken ct = default);
```

## 8. 错误处理表

| 场景 | 处理 | 结果 |
|------|------|------|
| MinerU Token 未配置 | `ParseDocumentFile` 校验返回 503 | `DOCLIBRARY_MINERU_NOT_CONFIGURED` |
| 同 modelVersion 解析进行中 | `ParseDocumentFile` 校验返回 422 | `DOCLIBRARY_PARSE_IN_PROGRESS` |
| 文件不存在 | `ParseDocumentFile` 返回 404 | `DOCLIBRARY_FILE_NOT_FOUND` |
| 解析记录不存在 | `DeleteDocumentParse` 返回 404 | `DOCLIBRARY_PARSE_NOT_FOUND` |
| MinerU 提交失败 | `SubmitUrlAsync` 抛异常 → Worker 捕获 | 标记 `failed`，记录 `error_message` |
| MinerU 轮询失败/超时 | `PollAndDownloadAsync` 抛异常 | 标记 `failed` |
| 转档失败（doc-converter 不可用） | `ProcessFileAsync` 分支 | 标记 `failed`，明确错误信息 |
| 转档失败（返回 null） | `ConvertToPdfAsync` 返回 null | 标记 `failed` |
| 大文件分块部分失败 | `ProcessSplitFileAsync` 收集错误 | 合并成功块但仍标记 `failed`，`error_message` 含失败块信息 |
| OSS 图片删除失败 | `DeleteDocumentParse` catch | 记 Warning，不阻塞 |
| OpenSearch 索引删除失败 | `DeleteDocumentParse` catch | 记 Warning，不阻塞 |
| 单任务异常 | `ExecuteAsync` foreach catch | 标记该任务 `failed`，继续处理下一个 |
| Worker 轮询异常 | `ExecuteAsync` 外层 catch | 记 Error，下一轮继续 |

## 9. 测试策略

### 9.1 单元测试 (UT)

位于 `src/Tests/Ruoyu.Study.DocLibrary.Tests/`。

**DocumentParseBlockServiceTests.cs**（9 个测试）：覆盖 `InsertBlocksFromContentListAsync` 的 JSON 解析、sort index 分页累加、image 关联、原始 JSON 保留、空/非法输入防御、默认 page_id、多字段文本提取。

**PdfSplitServiceTests.cs**（9 个测试）：覆盖 `GetPageCount` 准确性、`SplitPdf` 小文件单块/整除/余数/单页场景、块页数正确性、Markdown 分隔符拼接、图片名前缀冲突避免。

**FileConversionServiceTests.cs**（4 个测试）：覆盖 `IsAvailable` 构造可用性、不可用时 `ConvertToPdfAsync` 抛异常、PDF 检测（ContentType / 扩展名）。

### 9.2 集成测试（通过端点验证，规划）

| 场景 | 端点 |
|------|------|
| 触发解析 | `POST /admin/document-files/{id}/parse` |
| 解析记录列表 | `GET /admin/document-parses` |
| 删除解析记录 | `DELETE /admin/document-parses/{parseId}` |

### 9.3 测试限制

本模块的 Worker、MinerUPrecisionClient 依赖外部 HTTP，**无单元测试覆盖**（PdfSplitServiceTests 仅覆盖 PdfSharpCore 拆分纯逻辑；FileConversionServiceTests 使用 `StubHttpHandler` 覆盖 `RemoteFileConversionService` 的 HTTP 调用路径）。端到端解析流程需集成测试或手动验证。
