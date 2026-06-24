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
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    [ConcurrencyCheck]
    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
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
    // 新增：解析记录列表（含文档名搜索）
    Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
    // 新增：删除单条解析记录（含 S3 图片清理）
    Task<bool> DeleteParseAsync(Guid parseId, IOssService ossService);
}
```

### IDocumentParseRepository（新增方法）

```csharp
public interface IDocumentParseRepository
{
    Task AddAsync(DocumentParseModel model);
    Task<DocumentParseModel?> GetByIdAsync(Guid id);
    Task<DocumentParseModel?> GetLatestByFileIdAsync(Guid documentFileId);
    Task UpdateAsync(DocumentParseModel model);
    Task<List<DocumentParseModel>> GetByStatusAsync(string status);
    // 新增：分页列表（含搜索）
    Task<(List<DocumentParseModel> Items, int TotalCount)> GetListAsync(int page, int size, string? search = null);
}
```

## 端点设计

### 文件管理端点组

```csharp
var fileGroup = app.MapGroup("/admin/document-files")
    .RequireAuthorization();

fileGroup.MapPost("/upload", UploadDocumentFile);
fileGroup.MapGet("/", ListDocumentFiles);          // 新增 parseStatus 过滤参数
fileGroup.MapGet("/{id:guid}", GetDocumentFile);
fileGroup.MapPost("/{id:guid}/parse", ParseDocumentFile);
fileGroup.MapDelete("/{id:guid}", DeleteDocumentFile);
fileGroup.MapGet("/{id:guid}/export/markdown", ExportMarkdown);
fileGroup.MapGet("/{id:guid}/export/html", ExportHtml);
```

### 解析记录端点组（新增）

```csharp
var parseGroup = app.MapGroup("/admin/document-parses")
    .RequireAuthorization();

parseGroup.MapGet("/", ListDocumentParses);                // API-8
parseGroup.MapDelete("/{parseId:guid}", DeleteDocumentParse); // API-9
parseGroup.MapGet("/{parseId:guid}/export/markdown", ExportParseMarkdown); // API-10
parseGroup.MapGet("/{parseId:guid}/export/html", ExportParseHtml);        // API-11
```

### 文件列表过滤

```
GET /admin/document-files?page=1&pageSize=20&parseStatus=
  │
  ├─ parseStatus 为空：返回所有文件
  ├─ parseStatus=unparsed：返回无 parse 记录的文件
  ├─ parseStatus=pending/parsing/parsed/failed：返回最新 parse 为对应状态的文件
  └─ 实现方式：
       1. 查询所有文件（分页）
       2. 对每个文件查询最新 parse
       3. 在内存中过滤 parseStatus
       4. 重新分页返回
```

### 解析记录列表

```
GET /admin/document-parses?page=1&pageSize=20&search=
  │
  ├─ 1. 查询 document_parses JOIN document_files
  ├─ 2. search 非空时：WHERE file_name ILIKE '%search%'
  ├─ 3. ORDER BY parsed_at DESC NULLS LAST
  └─ 4. 返回 { id, fileName, status, parsedAt, errorMessage }
```

### 删除解析记录

```
DELETE /admin/document-parses/{parseId}
  │
  ├─ 1. 查询解析记录 → 不存在返回 404
  ├─ 2. 查询关联图片 → 从 S3 删除每张图片
  ├─ 3. 删除数据库记录（级联：parse_images → parse）
  └─ 4. 返回 { id, deleted } （原始文件不受影响）
```

### 按解析 ID 导出

```
GET /admin/document-parses/{parseId}/export/markdown
GET /admin/document-parses/{parseId}/export/html
  │
  ├─ 1. 查询解析记录 → 不存在返回 404
  ├─ 2. 校验 status=parsed → 非 parsed 返回 422
  ├─ 3. 查询关联的 document_file 获取文件名
  ├─ 4. 查询关联图片
  └─ 5. 与按文件 ID 导出相同的 ZIP/HTML 构建逻辑
```

## 上传流程

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

## 解析流程

```
POST /admin/document-files/{id}/parse
  │
  ├─ 1. 查询文件 → 不存在返回 404
  ├─ 2. 查询最新 parse → 如果 status ∈ {pending, parsing} 返回 422
  ├─ 3. 校验 MinerU Token 配置 → 未配置返回 503
  ├─ 4. 调用 DocumentParseService.CreateAsync(fileId) → 新建 parse 记录
  └─ 5. 返回 { id, parseId, status }
```

## MinerUFileParseWorker 流程

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

## 图片管理策略

- **S3 路径**：`documents/mineru/{taskId}/{imageName}`（由 `DownloadAndProcessZipAsync` 上传）
- **MD 中的路径**：`DownloadAndProcessZipAsync` 将相对路径替换为 S3 路径
- **查看时**：`GetDocumentFile` 端点将 S3 路径替换为 presigned URL
- **图片元数据**：`DownloadAndProcessZipAsync` 返回 `List<ImageMetadata>`，Worker 直接写入 `document_parse_images` 表
- **Presigned URL 有效期**：1 小时
- **删除文件时**：级联删除 S3 上的源文件和所有关联解析的图片
- **删除解析记录时**：仅删除该解析关联的 S3 图片，不删除原始文件

## 导出流程设计

### 按文件 ID 导出（使用最新 parse）

```
GET /admin/document-files/{id}/export/markdown
GET /admin/document-files/{id}/export/html
  │
  ├─ 1. 查询文件 → 不存在返回 404
  ├─ 2. 查询最新 parse → 未解析或非 parsed 返回 422
  ├─ 3. 获取 markdown_content
  ├─ 4. 获取关联图片列表
  ├─ 5. 替换路径（MD: S3→相对, HTML: S3→base64）
  └─ 6. 返回 ZIP/HTML
```

### 按解析 ID 导出

```
GET /admin/document-parses/{parseId}/export/markdown
GET /admin/document-parses/{parseId}/export/html
  │
  ├─ 1. 查询解析记录 → 不存在返回 404
  ├─ 2. 校验 status=parsed → 非 parsed 返回 422
  ├─ 3. 查询关联的 document_file 获取文件名
  ├─ 4. 获取 markdown_content 和关联图片
  ├─ 5. 替换路径（MD: S3→相对, HTML: S3→base64）
  └─ 6. 返回 ZIP/HTML
```

## 依赖的外部模块

| 接口 | 提供能力 | 所在模块 |
|------|---------|---------|
| `IDocumentFileService` | 文件 CRUD | `Ruoyu.Study.DocRetrieval.Domain.Services` |
| `IDocumentParseService` | 解析 CRUD、状态更新、列表、删除 | `Ruoyu.Study.DocRetrieval.Domain.Services` |
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
