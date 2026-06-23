# MinerU 文档解析（持久化版）— 设计说明 (DESIGN)

## 目录与文件结构

```
src/services/ruoyu.docretrieval/
├── src/
│   ├── Service/
│   │   ├── DocumentAdminEndpoints.cs              # 端点（文件 + 解析 + 导出）
│   │   ├── MinerUPrecisionClient.cs               # MinerU API 客户端（复用）
│   │   ├── MinerUFileParseWorker.cs               # MinerU 文件解析后台 Worker
│   │   └── ImageMetadata.cs                       # 图片元数据 record
│   ├── Domain/
│   │   ├── Models/
│   │   │   ├── DocumentFileModel.cs                # 文件领域模型
│   │   │   ├── DocumentParseModel.cs               # 解析领域模型
│   │   │   └── DocumentParseImageModel.cs          # 解析图片领域模型
│   │   ├── Services/
│   │   │   ├── IDocumentFileService.cs             # 文件服务接口
│   │   │   └── IDocumentParseService.cs             # 解析服务接口
│   │   └── Repositories/
│   │       ├── IDocumentFileRepository.cs           # 文件仓储接口
│   │       ├── IDocumentParseRepository.cs          # 解析仓储接口
│   │       └── IDocumentParseImageRepository.cs     # 解析图片仓储接口
│   └── Database/
│       ├── Entities/
│       │   ├── DocumentFileEntity.cs               # 文件数据库实体
│       │   ├── DocumentParseEntity.cs              # 解析数据库实体
│       │   └── DocumentParseImageEntity.cs          # 解析图片数据库实体
│       ├── Repositories/
│       │   ├── DocumentFileRepository.cs            # 文件仓储实现
│       │   ├── DocumentParseRepository.cs           # 解析仓储实现
│       │   └── DocumentParseImageRepository.cs       # 解析图片仓储实现
│       └── Migrations/
│           └── (Migration)
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

    [Column("content_type")]
    [Required]
    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    [ConcurrencyCheck]
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

### DocumentParseEntity

```csharp
[Table("document_parses")]
public class DocumentParseEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("document_file_id")]
    [Required]
    public Guid DocumentFileId { get; set; }

    [Column("status")]
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = DocumentParseStatus.Pending;

    [Column("external_task_id")]
    [MaxLength(100)]
    public string? ExternalTaskId { get; set; }

    [Column("markdown_content")]
    public string? MarkdownContent { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("parsed_at")]
    public DateTimeOffset? ParsedAt { get; set; }

    [ForeignKey(nameof(DocumentFileId))]
    public DocumentFileEntity? DocumentFile { get; set; }
}
```

### DocumentParseImageEntity

```csharp
[Table("document_parse_images")]
public class DocumentParseImageEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("parse_id")]
    [Required]
    public Guid ParseId { get; set; }

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

    [ForeignKey(nameof(ParseId))]
    public DocumentParseEntity? Parse { get; set; }
}
```

### 状态常量

```csharp
public static class DocumentParseStatus
{
    public const string Pending = "pending";
    public const string Parsing = "parsing";
    public const string Parsed = "parsed";
    public const string Failed = "failed";
}
```

## 服务接口

### IDocumentFileService

```csharp
public interface IDocumentFileService
{
    Task<DocumentFileModel> CreateAsync(DocumentFileModel model);
    Task<DocumentFileModel?> GetByIdAsync(Guid id);
    Task<(List<DocumentFileModel> Items, int TotalCount)> GetListAsync(int page, int size);
    Task<bool> DeleteAsync(Guid id);
}
```

### IDocumentParseService

```csharp
public interface IDocumentParseService
{
    Task<DocumentParseModel> CreateAsync(Guid documentFileId);
    Task<DocumentParseModel?> GetByIdAsync(Guid id);
    Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
    Task<DocumentParseModel> UpdateStatusAsync(Guid id, string status, string? errorMessage = null, string? markdownContent = null, string? externalTaskId = null);
    Task<List<DocumentParseModel>> GetPendingJobsAsync();
    Task AddImageAsync(DocumentParseImageModel image);
    Task<List<DocumentParseImageModel>> GetImagesByParseIdAsync(Guid parseId);
    Task<List<DocumentParseImageModel>> GetImagesByFileIdAsync(Guid documentFileId);
}
```

## 端点设计

```csharp
var fileGroup = app.MapGroup("/admin/document-files")
    .RequireAuthorization();

fileGroup.MapPost("/upload", UploadDocumentFile);
fileGroup.MapGet("/", ListDocumentFiles);
fileGroup.MapGet("/{id:guid}", GetDocumentFile);
fileGroup.MapPost("/{id:guid}/parse", ParseDocumentFile);
fileGroup.MapDelete("/{id:guid}", DeleteDocumentFile);
fileGroup.MapGet("/{id:guid}/export/markdown", ExportMarkdown);
fileGroup.MapGet("/{id:guid}/export/html", ExportHtml);
```

### 上传流程

```
POST /admin/document-files/upload
  │
  ├─ 1. 校验 HasFormContentType
  ├─ 2. 提取 file → 校验存在性/大小/格式
  ├─ 3. 上传到 S3 (documents/docretrieval-files/{Guid}{ext})
  ├─ 4. 构建 DocumentFileModel
  ├─ 5. 调用 DocumentFileService.CreateAsync
  └─ 6. 返回 { id, fileName, contentType }
```

### 解析流程

```
POST /admin/document-files/{id}/parse
  │
  ├─ 1. 查询文件 → 不存在返回 404
  ├─ 2. 查询最新 parse → 如果 status ∈ {pending, parsing} 返回 422
  ├─ 3. 校验 MinerU Token 配置 → 未配置返回 503
  ├─ 4. 调用 DocumentParseService.CreateAsync(fileId) → 新建 parse 记录
  └─ 5. 返回 { id, parseId, status }
```

### MinerUFileParseWorker 流程

```
MinerUFileParseWorker (BackgroundService, 每 5 秒轮询)
  │
  ├─ 1. 查询 GetPendingJobsAsync()
  │
  ├─ 2. 对每个 pending 任务：
  │     ├─ 更新 status=parsing
  │     ├─ 查询关联的 document_file 获取 S3 路径
  │     ├─ 生成 S3 presigned URL
  │     ├─ 调用 MinerUPrecisionClient.SubmitUrlAsync → 获取 task_id
  │     ├─ 更新 external_task_id
  │     ├─ 轮询 MinerU 状态（每 5 秒，最长 30 分钟）
  │     ├─ 完成后：
  │     │   ├─ 调用 MinerUPrecisionClient.DownloadAndProcessZipAsync
  │     │   ├─ 写入 markdown_content
  │     │   ├─ 写入 document_parse_images 记录
  │     │   └─ 更新 status=parsed, parsed_at=now
  │     └─ 失败时：
  │         └─ 更新 status=failed, error_message
  │
  └─ 3. 继续下一个 pending 任务
```

### 查看文件详情

```
GET /admin/document-files/{id}
  │
  ├─ 1. 查询文件 → 不存在返回 404
  ├─ 2. 查询最新 parse 记录
  ├─ 3. 如果有 parse 且 status=parsed：
  │     ├─ 查询关联图片
  │     ├─ 对每张图片生成 presigned URL
  │     └─ 替换 markdown_content 中的图片路径
  └─ 4. 返回 { file info, parse: { ... } 或 null }
```

### 删除文件

```
DELETE /admin/document-files/{id}
  │
  ├─ 1. 查询文件 → 不存在返回 404
  ├─ 2. 查询所有 parse 的图片 → 从 S3 删除
  ├─ 3. 从 S3 删除源文件
  └─ 4. 删除数据库记录（级联：parse_images → parses → file）
```

## 图片管理策略

- **S3 路径**：`documents/mineru/{taskId}/{imageName}`（由 `DownloadAndProcessZipAsync` 上传）
- **MD 中的路径**：`DownloadAndProcessZipAsync` 将相对路径替换为 S3 路径
- **查看时**：`GetDocumentFile` 端点将 S3 路径替换为 presigned URL
- **图片元数据**：`DownloadAndProcessZipAsync` 返回 `List<ImageMetadata>`，Worker 直接写入 `document_parse_images` 表
- **Presigned URL 有效期**：1 小时
- **删除文件时**：级联删除 S3 上的源文件和所有关联解析的图片

## 导出流程设计

### 导出 MD+图片 ZIP

```
GET /admin/document-files/{id}/export/markdown
  │
  ├─ 1. 查询文件 → 不存在返回 404
  ├─ 2. 查询最新 parse → 未解析或非 parsed 返回 422
  ├─ 3. 获取 markdown_content
  ├─ 4. 获取关联图片列表
  ├─ 5. 将 MD 中的 S3 路径替换为 images/{imageName}
  ├─ 6. 下载每张图片的二进制数据（从 S3）
  ├─ 7. 构建 ZIP：{fileName}.md + images/{imageName}
  └─ 8. 返回 application/zip
```

### 导出 HTML

```
GET /admin/document-files/{id}/export/html
  │
  ├─ 1. 查询文件 → 不存在返回 404
  ├─ 2. 查询最新 parse → 未解析或非 parsed 返回 422
  ├─ 3. 获取 markdown_content
  ├─ 4. 下载每张图片 → 转 base64 data URI
  ├─ 5. 将 MD 中的 S3 路径替换为 data URI
  ├─ 6. Markdig 转 HTML + 内联 CSS
  └─ 7. 返回 text/html
```

## 依赖的外部模块

| 接口 | 提供能力 | 所在模块 |
|------|---------|---------|
| `IDocumentFileService` | 文件 CRUD | `Ruoyu.Study.DocRetrieval.Domain.Services` |
| `IDocumentParseService` | 解析 CRUD、状态更新 | `Ruoyu.Study.DocRetrieval.Domain.Services` |
| `MinerUPrecisionClient` | MinerU API 提交/轮询/下载 | `Ruoyu.Study.DocRetrieval.Service` |
| `IOssService` | S3 上传/下载/删除/presigned URL | `Ruoyu.Study.Common.Oss` |

## DI 注册

```csharp
// Program.cs
builder.Services.AddScoped<IDocumentFileRepository, DocumentFileRepository>();
builder.Services.AddScoped<IDocumentParseRepository, DocumentParseRepository>();
builder.Services.AddScoped<IDocumentParseImageRepository, DocumentParseImageRepository>();
builder.Services.AddScoped<IDocumentFileService, DocumentFileService>();
builder.Services.AddScoped<IDocumentParseService, DocumentParseService>();
builder.Services.AddSingleton<MinerUPrecisionClient>();
builder.Services.AddHostedService<MinerUFileParseWorker>();
```
