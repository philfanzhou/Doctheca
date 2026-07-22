# 03-DESIGN — Document Parse 设计说明

## 1. 目录与文件结构

```
src/services/ruoyu.doclibrary/
├── src/
│   ├── Service/
│   │   ├── Endpoints/
│   │   │   ├── DocumentParseEndpoints.cs         # 解析记录端点（列表/删除）
│   │   │   └── DocumentFileEndpoints.cs          # 触发解析端点（POST /{id}/parse，属于本模块流程但归属文件管理）
│   │   ├── MinerUPrecisionClient.cs              # MinerU API 客户端（Singleton，含 MinerUParseResult / MinerUOptions / ImageMetadata）
│   │   ├── MinerUFileParseWorker.cs              # MinerU 文件解析后台 Worker（BackgroundService），仅保留轮询循环与 best-effort 后处理
│   │   ├── Parsing/
│   │   │   ├── MinerUParseOrchestrator.cs        # 解析流程编排：单文件 / 分块路径调度
│   │   │   └── MinerUResultPersistence.cs        # 解析结果持久化：ZIP / images / blocks / 状态更新
│   │   ├── PdfSplitService.cs                    # PDF 分页服务（Singleton，含 IPdfSplitService）
│   │   └── RemoteFileConversionService.cs        # 远程转换服务（Singleton，HTTP 调用 doc-converter，含 IFileConversionService）
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentParseModel.cs             # 解析领域模型
│   │   │   ├── DocumentParseStatus.cs            # 解析状态常量
│   │   │   ├── DocumentParseBlockModel.cs        # 解析 block 领域模型
│   │   │   ├── DocumentParseImageModel.cs        # 解析图片领域模型
│   │   │   └── DocumentAnalysisOptions.cs        # LLM 分析配置（解析完成后元数据 best-effort 分析）
│   │   ├── Services/
│   │   │   ├── IDocumentParseService.cs           # 解析服务接口
│   │   │   ├── DocumentParseService.cs           # 解析服务实现
│   │   │   └── DocumentParseBlockService.cs      # Block 服务接口 + 实现（IDocumentParseBlockService）
│   │   └── Repositories/
│   │       ├── IDocumentParseRepository.cs        # 解析仓储接口
│   │       ├── IDocumentParseBlockRepository.cs   # Block 仓储接口
│   │       ├── IDocumentParseImageRepository.cs   # 图片仓储接口
│   │       └── ISearchIndexService.cs            # OpenSearch 索引服务接口（Worker 调用）
│   └── Database/
│       ├── DatabaseInitializer.cs                 # 数据库初始化（CREATE TABLE IF NOT EXISTS + ALTER COLUMN）
│       ├── Entities/
│       │   ├── DocumentParseEntity.cs             # 解析数据库实体
│       │   ├── DocumentParseBlockEntity.cs        # 解析 block 数据库实体
│       │   └── DocumentParseImageEntity.cs        # 解析图片数据库实体
│       └── Repositories/
│           ├── DocumentParseRepository.cs         # 解析仓储实现
│           ├── DocumentParseBlockRepository.cs    # Block 仓储实现
│           └── DocumentParseImageRepository.cs    # 图片仓储实现
└── docs/modules/DocumentParse/                    # 本模块文档
```

> **注意**：没有 EF Core Migration。所有表结构通过 `DatabaseInitializer`（`CREATE TABLE IF NOT EXISTS`）和 `EnsureColumnsAsync`（`ALTER TABLE ... ADD COLUMN IF NOT EXISTS`）维护。

## 2. 设计决策

### 2.1 Worker 模式（BackgroundService）

**决策**：采用 `BackgroundService` + 轮询 `pending` 任务，而非消息队列。

**理由**：
- 当前部署规模下单机轮询足够（每 5 秒），无外部依赖
- 状态机已在数据库中持久化，Worker 崩溃重启后可恢复未完成任务
- 后续若需分布式扩展，可替换为分布式任务队列（当前 `IDocumentParseService.GetPendingJobsAsync` 抽象已隔离数据访问）

**取舍**：轮询存在 ≤5 秒延迟；所有 Worker 实例会竞争 `pending` 任务，多实例部署需额外加锁（当前为单实例部署）。

### 2.2 presigned URL 提交

**决策**：文件上传到自有 OSS 后生成 presigned URL，由 MinerU API 主动拉取，而非将文件字节直接 POST 到 MinerU。

**理由**：
- MinerU Precision API 仅支持 `url` 方式提交（不支持 multipart upload）
- 避免在 DocLibrary 服务中转大文件字节流，降低内存占用与超时风险
- 已有 `IOssService.GetPresignedUrlAsync` 抽象（3600 秒有效期，覆盖 MinerU 30 分钟超时）

### 2.3 分页策略（MaxPagesPerChunk = 200）

**决策**：超过 200 页的 PDF 拆分为多个子文档逐块提交，合并结果后存为一条 parse 记录。

**理由**：MinerU Precision API 单任务有页数上限，超过后解析质量下降或失败
- 分块并行当前为串行（逐块提交），简化错误处理与 OSS 路径管理
- 每块独立生成 presigned URL、独立上传 OSS（临时路径 `mineru/splits/{parse.Id}/chunk_{i}.pdf`），解析完成后清理
- 图片名添加 `chunk{i}_` 前缀避免跨块路径冲突
- Markdown 以 `\n\n---\n\n` 分隔拼接

**取舍**：分块边界可能导致跨页表格/段落被截断（MinerU 按子文档独立解析，无跨块上下文）。

### 2.4 best-effort 联动

**决策**：OpenSearch 索引、LLM 元数据分析、OSS 文件清理均为 best-effort（失败仅记 Warning，不阻塞主流程）。

**理由**：
- 解析核心结果已持久化在 DB，索引/元数据是增强能力
- 任一外部依赖不可用不应导致解析失败或删除失败
- 索引可在下次解析时重建；元数据可通过 `PUT /admin/document-files/{id}/metadata` 手动补填

### 2.5 覆盖式 Block 写入

**决策**：`InsertBlocksFromContentListAsync` 在插入前不显式删除旧 blocks，依赖 `IDocumentParseBlockRepository.AddBlocksAsync` 实现覆盖策略（先删后插）。

**理由**：同一 parse 的 block 全量替换比增量更新更简单可靠；`block_data` 列保留原始 JSON（兜底，MinerU 升级无需改 schema）。

### 2.6 状态常量

**决策**：状态值用常量字符串（`pending` / `parsing` / `parsed` / `failed`）存储在 `varchar` 列 + C# 常量类，而非 enum 或 int。

**理由**：字符串值在数据库、日志、API 响应中自解释，无需转换；DB 列约束 + 服务端校验防止非法值。

## 3. 数据库表结构

通过 `DatabaseInitializer.GetTableCreationSql` 与 `EnsureColumnsAsync` 创建，无 EF Core Migration。

### 3.1 document_parses

| 列名 | 类型 | 约束 | 默认值 | 说明 |
|------|------|------|--------|------|
| `id` | uuid | PK | | 解析记录 ID |
| `document_file_id` | uuid | NOT NULL, FK → `document_files(id)` ON DELETE CASCADE | | 关联文件 |
| `model_version` | varchar(20) | NOT NULL | `'vlm'` | 解析模型版本 |
| `status` | varchar(30) | NOT NULL | `'pending'` | 解析状态 |
| `external_task_id` | varchar(100) | NULL | | MinerU 任务 ID |
| `markdown_content` | text | NULL | | full.md 内容（图片路径已替换为 OSS 路径） |
| `content_list` | jsonb | NULL | | content_list.json 原文 |
| `content_list_v2` | jsonb | NULL | | content_list_v2.json 原文 |
| `model_json` | jsonb | NULL | | model.json 原文 |
| `layout_json` | jsonb | NULL | | layout.json 原文 |
| `zip_path` | varchar(500) | NULL | | ZIP 在 OSS 的路径 |
| `error_message` | text | NULL | | 失败原因 |
| `parsed_at` | timestamptz | NULL | | 解析完成时间 |

索引：`IX_document_parses_status`、`IX_document_parses_document_file_id`、`IX_document_parses_model_version`。

### 3.2 document_parse_images

| 列名 | 类型 | 约束 | 默认值 | 说明 |
|------|------|------|--------|------|
| `id` | uuid | PK | | |
| `parse_id` | uuid | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE | | |
| `image_name` | varchar(200) | NOT NULL | | 图片文件名 |
| `image_path` | varchar(500) | NOT NULL | | OSS 路径 |
| `content_type` | varchar(50) | NOT NULL | `'image/jpeg'` | MIME 类型 |

索引：`IX_document_parse_images_parse_id`。

### 3.3 document_parse_blocks

| 列名 | 类型 | 约束 | 默认值 | 说明 |
|------|------|------|--------|------|
| `id` | uuid | PK | | |
| `parse_id` | uuid | NOT NULL, FK → `document_parses(id)` ON DELETE CASCADE | | |
| `page_id` | integer | NOT NULL | | 页码（0-indexed） |
| `sort_index` | integer | NOT NULL | | 页内阅读顺序 |
| `block_type` | varchar(20) | NOT NULL | | 块类型（text / image / equation / ...） |
| `text_content` | text | NULL | | 文本/HTML/LaTeX |
| `image_id` | uuid | NULL, FK → `document_parse_images(id)` ON DELETE SET NULL | | 仅 image 类型 |
| `block_data` | jsonb | NOT NULL | | 原始 block JSON（兜底） |
| `created_at` | timestamptz | NOT NULL | `NOW()` | |

索引：`IX_document_parse_blocks_parse_id_page_id_sort_index`、`IX_document_parse_blocks_block_type`、`IX_document_parse_blocks_image_id`。

## 4. 数据流

### 4.1 触发解析 → 异步处理

```
管理员 → POST /admin/document-files/{id}/parse?modelVersion=vlm
  → DocumentFileEndpoints.ParseDocumentFile
  → 校验（文件存在 / 无进行中解析 / Token 配置）
  → parseService.CreateAsync(fileId, modelVersion)
  → 新建 document_parses 记录 (status=pending)
  → 返回 { parseId, status }

MinerUFileParseWorker.ExecuteAsync (每 5s)
  → parseService.GetPendingJobsAsync()
  → MinerUParseOrchestrator.ProcessFileAsync:
      1. 更新 status=parsing
      2. 下载源文件
      3. 非 PDF → 调用 doc-converter 转换为 PDF（转换后 PDF 用于后续页数计算和分片）
      4. 算 PDF 页数
      5a. ≤200 页 → ProcessSingleFileAsync:
          - 若为转换后 PDF：上传到 OSS 临时路径 mineru/converted/{parseId}.pdf
          - 生成 presigned URL → 提交 MinerU → 轮询 → 下载 ZIP → 调用 MinerUResultPersistence.PersistParseResultAsync 持久化
          - 解析完成后清理临时 OSS 路径
      5b. >200 页 → ProcessSplitFileAsync:
          - 用 pdfStream 本地切分为多个 PDF chunk
          - 每个 chunk 上传 OSS (mineru/splits/{parseId}/chunk_{i}.pdf)
          - 逐 chunk 提交 MinerU → 轮询 → 下载 ZIP
          - 合并 markdown / content_list / images（图片名加 chunk{i}_ 前缀）
          - 调用 MinerUResultPersistence.PersistMergedChunkResultsAsync 持久化
          - 解析完成后清理临时 chunk OSS 路径
  → 成功后（返回非空 markdown）：
      - IndexBlocksToSearchAsync (OpenSearch)
      - AnalyzeMetadataIfMissingAsync (LLM)
```

> **关键修正（2026-07-13）**：非 PDF 小文件路径原先把转换后 PDF 丢弃，直接把原始 DOCX 的 presigned URL 传给 MinerU，与转换逻辑不一致。现已修复：转换后 PDF 统一上传 OSS，MinerU 始终收到 PDF。

### 4.2 MinerU ZIP 处理流

```
MinerUPrecisionClient.DownloadAndProcessZipAsync
  → 下载 ZIP
  → 提取 full.md (必需)
  → 提取 content_list.json / content_list_v2.json / model.json / layout.json
  → 遍历 images/ 上传 OSS (mineru/{taskId}/{imageName})
  → Markdown 中 images/{name} → 完整 OSS 路径
  → 返回 MinerUParseResult
```

### 4.3 持久化流

```
MinerUResultPersistence.PersistParseResultAsync
  → 上传 ZIP 到 OSS (mineru/{fileId}/mineru-output.zip)
  → 写入 document_parse_images (每张图片)
  → blockService.InsertBlocksFromContentListAsync (blocks 覆盖写入)
  → parseService.UpdateStatusAsync(Parsed, markdown, contentList, ...)

MinerUResultPersistence.PersistMergedChunkResultsAsync
  → 合并所有 chunk 的 markdown / content_list / images
  → 上传第一个 chunk 的 ZIP 到 OSS (mineru/{fileId}/mineru-output.zip)
  → 写入 document_parse_images（图片名加 chunk{i}_ 前缀）
  → blockService.InsertBlocksFromContentListAsync (blocks 覆盖写入)
  → parseService.UpdateStatusAsync(status, markdown, contentList, ...)
```

解析成功后，Worker 再执行 best-effort 后置：

```
MinerUFileParseWorker
  → IndexBlocksToSearchAsync (OpenSearch)
  → AnalyzeMetadataIfMissingAsync (LLM)
```

### 4.4 删除联动流

```
DELETE /admin/document-parses/{parseId}
  → parseService.GetByIdAsync (校验存在)
  → 遍历 document_parse_images → ossService.DeleteAsync (best-effort)
  → parseService.DeleteParseAsync (级联删除 DB: images → parse)
  → searchIndexService.DeleteParseIndexAsync (best-effort)
```

## 5. 依赖的外部模块

| 接口 | 提供能力 | 所在模块 |
|------|---------|---------|
| `IDocumentFileService` | 文件 CRUD、元数据更新 | `Ruoyu.Study.DocLibrary.Domain.Services` |
| `ISearchIndexService` | OpenSearch 索引写/删/更新元数据 | `Ruoyu.Study.DocLibrary.Domain.Repositories` |
| `IDocumentAnalysisService` | LLM 元数据分析（可选，best-effort） | `Ruoyu.Study.DocLibrary.Domain.Repositories` |
| `IOssService` | OSS 上传/下载/删除/presigned URL | `Ruoyu.Study.Common.Oss` |
| MinerU Precision API | 文档解析（外部 HTTP） | `https://mineru.net` |
| doc-converter | DOC/PPT → PDF（外部 HTTP 服务） | `src/foundation/doc-converter`（独立部署，容器名 `doc-converter:5050`） |

> **变更说明（2026-07-13）**：原 `LibreOfficeConversionService`（本地进程调用 LibreOffice）已替换为 `RemoteFileConversionService`（HTTP 调用独立的 doc-converter 基础服务）。改造原因：剥离 LibreOffice 重依赖，加快 doclibrary 镜像构建；doc-converter 镜像构建一次可长期复用。详见 `src/foundation/doc-converter/docs/design.md`。

## 6. DI 注册

```csharp
// Program.cs
builder.Services.Configure<MinerUOptions>(builder.Configuration.GetSection(MinerUOptions.SectionName));
builder.Services.Configure<FileConversionOptions>(builder.Configuration.GetSection(FileConversionOptions.SectionName));

// FileConversion：命名 HttpClient + Singleton 包装。
// 说明：AddHttpClient<TInterface, TImplementation>() 默认注册为 Transient，会导致每次
// CreateScope().GetRequiredService<IFileConversionService>() 都 new 新实例（构造函数日志
// 每 5 秒被 MinerUFileParseWorker 重复打印一次）。改用 AddHttpClient("FileConversion")
// 注册命名 HttpClient（HttpClientFactory 池化 Handler，避免 socket 耗尽），再用
// AddSingleton<IFileConversionService> 工厂方式包装，确保真正的 Singleton 生命周期。
builder.Services.AddHttpClient("FileConversion", client =>
{
    var url = builder.Configuration["FileConversionService:Url"] ?? "http://doc-converter:5050";
    client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(180);
});
builder.Services.AddSingleton<IFileConversionService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var options = sp.GetRequiredService<IOptions<FileConversionOptions>>();
    var logger = sp.GetRequiredService<ILogger<RemoteFileConversionService>>();
    return new RemoteFileConversionService(factory.CreateClient("FileConversion"), options, logger);
});

builder.Services.AddSingleton<MinerUPrecisionClient>();
builder.Services.AddSingleton<IPdfSplitService, PdfSplitService>();

builder.Services.AddScoped<IDocumentParseRepository, DocumentParseRepository>();
builder.Services.AddScoped<IDocumentParseImageRepository, DocumentParseImageRepository>();
builder.Services.AddScoped<IDocumentParseBlockRepository, DocumentParseBlockRepository>();
builder.Services.AddScoped<IDocumentParseBlockService, DocumentParseBlockService>();
builder.Services.AddScoped<IDocumentParseService, DocumentParseService>();

// 解析流程编排与结果持久化（Worker 每轮通过 CreateScope 解析 Scoped 实例）
builder.Services.AddScoped<MinerUParseOrchestrator>();
builder.Services.AddScoped<MinerUResultPersistence>();

builder.Services.AddHostedService<MinerUFileParseWorker>();
```

注册关系到文件：
- `MinerUPrecisionClient`、`PdfSplitService`、`RemoteFileConversionService`（`IFileConversionService`）为 Singleton。
- `RemoteFileConversionService` 通过 `AddHttpClient("FileConversion", ...)` 注册命名 HttpClient（HttpClientFactory 池化 Handler 生命周期，默认 2 分钟）+ `AddSingleton<IFileConversionService>` 工厂方式包装（只 new 一次），实现真正的 Singleton 生命周期。
- `MinerUParseOrchestrator`、`MinerUResultPersistence` 为 Scoped，由 `MinerUFileParseWorker` 在每轮 `CreateScope()` 中解析。
- `MinerUFileParseWorker` 为 HostedService（Singleton），仅负责轮询与 best-effort 后处理。

## 7. 配置

```json
{
  "MinerU": {
    "ApiToken": "eyJ0eXAi...",
    "BaseUrl": "https://mineru.net",
    "ModelVersion": "vlm"
  },
  "FileConversionService": {
    "Url": "http://doc-converter:5050",
    "TimeoutSeconds": 120
  }
}
```

| 配置键 | 说明 |
|--------|------|
| `MinerU:ApiToken` | Bearer Token（免费额度 1000 页/天） |
| `MinerU:BaseUrl` | API 地址（默认 `https://mineru.net`） |
| `MinerU:ModelVersion` | 默认模型版本（`vlm` / `pipeline`），可被请求参数覆盖 |
| `FileConversionService:Url` | doc-converter 服务地址（默认 `http://doc-converter:5050`）；HTTP 调用超时硬编码 180s |

Section 常量：`MinerUOptions.SectionName = "MinerU"`、`FileConversionOptions.SectionName = "FileConversionService"`。

## 8. 安全

DocLibrary 为内网管理后台，所有 `/admin/*` 端点 `AllowAnonymous`，访问控制由部署层网络隔离实现。`MinerU:ApiToken` 通过 `appsettings.json` 或环境变量注入，不在 API 响应中暴露（日志中 SensitiveDataMasker 脱敏）。

## 9. 关键 record / 模型

```csharp
// MinerU 解析结果（单个任务 ZIP）
public record MinerUParseResult(
    byte[] ZipBytes,
    string Markdown,
    string ContentListJson,
    string? ContentListV2Json,
    string? ModelJson,
    string? LayoutJson,
    List<ImageMetadata> Images);

// 图片元数据
public record ImageMetadata(string ImageName, string S3Path, string ContentType, long FileSize);

// MinerU 配置
public class MinerUOptions { public const string SectionName = "MinerU"; ... }
```
