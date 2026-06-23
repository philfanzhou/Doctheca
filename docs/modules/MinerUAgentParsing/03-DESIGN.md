# MinerU 文档解析（持久化版）— 设计说明 (DESIGN)

## 目录与文件结构

```
src/services/ruoyu.docretrieval/
├── src/
│   ├── Service/
│   │   ├── DocumentAdminEndpoints.cs              # 现有端点 + 新增 document-files 端点
│   │   ├── MinerUPrecisionClient.cs               # MinerU API 客户端（复用）
│   │   └── MinerUFileParseWorker.cs               # 新增：MinerU 文件解析后台 Worker
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentFileModel.cs                # 新增：文件领域模型
│   │   │   └── DocumentFileImageModel.cs           # 新增：文件图片领域模型
│   │   ├── Services/
│   │   │   └── IDocumentFileService.cs             # 新增：文件服务接口
│   │   └── Repositories/
│   │       ├── IDocumentFileRepository.cs           # 新增：文件仓储接口
│   │       └── IDocumentFileImageRepository.cs      # 新增：图片仓储接口
│   └── Database/
│       ├── Entities/
│       │   ├── DocumentFileEntity.cs               # 新增：文件数据库实体
│       │   └── DocumentFileImageEntity.cs           # 新增：图片数据库实体
│       ├── Repositories/
│       │   ├── DocumentFileRepository.cs            # 新增：文件仓储实现
│       │   └── DocumentFileImageRepository.cs       # 新增：图片仓储实现
│       └── Migrations/
│           └── (新增 Migration)
└── docs/modules/MinerUAgentParsing/
    ├── 01-FEATURE.md
    ├── 02-SPEC.md
    └── 03-DESIGN.md (本文件)
```

## 数据模型

### DocumentFileEntity

```csharp
[Table("document_files")]
public class DocumentFileEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("file_name")]
    [Required]
    [MaxLength(500)]
    public string FileName { get; set; } = string.Empty;

    [Column("file_path")]
    [Required]
    [MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    [Column("file_size")]
    public long FileSize { get; set; }

    [Column("content_type")]
    [Required]
    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;

    [Column("status")]
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = DocumentFileStatus.Uploaded;

    [Column("external_task_id")]
    [MaxLength(100)]
    public string? ExternalTaskId { get; set; }

    [Column("markdown_content")]
    public string? MarkdownContent { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("parsed_at")]
    public DateTimeOffset? ParsedAt { get; set; }

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    [ConcurrencyCheck]
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

### DocumentFileImageEntity

```csharp
[Table("document_file_images")]
public class DocumentFileImageEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("document_file_id")]
    [Required]
    public Guid DocumentFileId { get; set; }

    [Column("image_name")]
    [Required]
    [MaxLength(200)]
    public string ImageName { get; set; } = string.Empty;

    [Column("image_path")]
    [Required]
    [MaxLength(500)]
    public string ImagePath { get; set; } = string.Empty;

    [Column("content_type")]
    [Required]
    [MaxLength(50)]
    public string ContentType { get; set; } = "image/jpeg";

    [Column("file_size")]
    public long FileSize { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [ForeignKey(nameof(DocumentFileId))]
    public DocumentFileEntity? DocumentFile { get; set; }
}
```

### 状态常量

```csharp
public static class DocumentFileStatus
{
    public const string Uploaded = "uploaded";
    public const string PendingParse = "pending_parse";
    public const string Parsing = "parsing";
    public const string Parsed = "parsed";
    public const string ParseFailed = "parse_failed";
}
```

## 服务接口

```csharp
public interface IDocumentFileService
{
    Task<DocumentFileModel> CreateAsync(DocumentFileModel model);
    Task<DocumentFileModel?> GetByIdAsync(Guid id);
    Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size, string? status = null);
    Task<DocumentFileModel> UpdateStatusAsync(Guid id, string status, string? errorMessage = null, string? markdownContent = null, string? externalTaskId = null);
    Task<bool> DeleteAsync(Guid id);
    Task<List<DocumentFileModel>> GetPendingParseJobsAsync();
    Task AddImageAsync(DocumentFileImageModel image);
    Task<List<DocumentFileImageModel>> GetImagesByFileIdAsync(Guid documentFileId);
}
```

## 端点设计

### 新增端点组

```csharp
// 在 DocumentAdminEndpoints.MapDocumentAdminEndpoints 中新增
var fileGroup = app.MapGroup("/admin/document-files")
    .RequireAuthorization();

fileGroup.MapPost("/upload", UploadDocumentFile);
fileGroup.MapGet("/", ListDocumentFiles);
fileGroup.MapGet("/{id:guid}", GetDocumentFile);
fileGroup.MapPost("/{id:guid}/parse", ParseDocumentFile);
fileGroup.MapDelete("/{id:guid}", DeleteDocumentFile);
```

### 上传流程

```
POST /admin/document-files/upload
  │
  ├─ 1. 校验 HasFormContentType
  ├─ 2. 提取 file → 校验存在性/大小/格式
  ├─ 3. 上传到 S3 (documents/docretrieval-files/{Guid}{ext})
  ├─ 4. 构建 DocumentFileModel (status=uploaded)
  ├─ 5. 调用 DocumentFileService.CreateAsync
  └─ 6. 返回 { id, fileName, fileSize, status }
```

### 解析流程

```
POST /admin/document-files/{id}/parse
  │
  ├─ 1. 查询文件 → 不存在返回 404
  ├─ 2. 校验 status ∈ {uploaded, parse_failed} → 否则 422
  ├─ 3. 校验 MinerU Token 配置 → 未配置返回 503
  ├─ 4. 更新 status = pending_parse
  └─ 5. 返回 { id, status }
```

### MinerUFileParseWorker 流程

```
MinerUFileParseWorker (BackgroundService, 每 5 秒轮询)
  │
  ├─ 1. 查询 GetPendingParseJobsAsync()
  │
  ├─ 2. 对每个 pending_parse 任务：
  │     ├─ 更新 status=parsing
  │     ├─ 生成 S3 presigned URL
  │     ├─ 调用 MinerUPrecisionClient.SubmitUrlAsync → 获取 task_id
  │     ├─ 更新 external_task_id
  │     ├─ 轮询 MinerU 状态（每 5 秒，最长 30 分钟）
  │     ├─ 完成后：
  │     │   ├─ 调用 MinerUPrecisionClient.DownloadAndProcessZipAsync
  │     │   │   （下载 ZIP → 提取 MD → 图片上传 S3 → 替换路径）
  │     │   ├─ 写入 markdown_content
  │     │   ├─ 写入 document_file_images 记录
  │     │   └─ 更新 status=parsed, parsed_at=now
  │     └─ 失败时：
  │         └─ 更新 status=parse_failed, error_message
  │
  └─ 3. 继续下一个 pending_parse 任务
```

### 查看文件详情

```
GET /admin/document-files/{id}
  │
  ├─ 1. 查询文件 → 不存在返回 404
  ├─ 2. 查询关联图片
  ├─ 3. 对每张图片生成 presigned URL
  ├─ 4. 替换 markdown_content 中的图片路径为 presigned URL
  └─ 5. 返回完整信息
```

## 图片管理策略

- **S3 路径**：`documents/mineru/{taskId}/{imageName}`（由 `DownloadAndProcessZipAsync` 上传）
- **MD 中的路径**：`DownloadAndProcessZipAsync` 将相对路径替换为 S3 路径（如 `documents/mineru/task-xyz/abc.jpg`）
- **查看时**：`GetDocumentFile` 端点将 S3 路径替换为 presigned URL
- **图片元数据**：`DownloadAndProcessZipAsync` 返回 `List<ImageMetadata>`（含 ImageName、S3Path、ContentType、FileSize），Worker 直接写入 `document_file_images` 表
- **Presigned URL 有效期**：1 小时
- **删除文件时**：级联删除 S3 上的源文件和所有关联图片

## 依赖的外部模块

| 接口 | 提供能力 | 所在模块 |
|------|---------|---------|
| `IDocumentFileService` | 文件 CRUD、状态更新 | `Ruoyu.Study.DocRetrieval.Domain.Services` |
| `MinerUPrecisionClient` | MinerU API 提交/轮询/下载 | `Ruoyu.Study.DocRetrieval.Service` |
| `IOssService` | S3 上传/下载/删除/presigned URL | `Ruoyu.Study.Common.Oss` |

## DI 注册

```csharp
// Program.cs
builder.Services.AddScoped<IDocumentFileService, DocumentFileService>();
builder.Services.AddSingleton<MinerUPrecisionClient>();
builder.Services.AddHostedService<MinerUFileParseWorker>();
```
